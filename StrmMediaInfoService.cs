using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Data.Enums;
using Microsoft.Extensions.Logging;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.MediaEncoding;
using MediaBrowser.Controller.Persistence;
using MediaBrowser.Model.Dlna;
using MediaBrowser.Model.Dto;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.MediaInfo;

namespace StrmTool
{
    /// <summary>
    /// 媒体探测结果，包含流信息和元数据
    /// </summary>
    public class MediaProbeResult
    {
        public List<MediaStream> MediaStreams { get; set; } = new List<MediaStream>();
        public long Size { get; set; }
        public long? RunTimeTicks { get; set; }
        public string Container { get; set; }
        public int TotalBitrate { get; set; }
        public int Width { get; set; }
        public int Height { get; set; }
        public bool Success => MediaStreams != null && MediaStreams.Count > 0;
    }

    /// <summary>
    /// STRM 媒体流相关共享服务：扫描、读取媒体流、探测远程媒体并写入数据库。
    /// </summary>
    public class StrmMediaInfoService
    {
        private readonly ILogger _logger;
        private readonly ILibraryManager _libraryManager;
        private readonly IMediaEncoder _mediaEncoder;
        private readonly IMediaStreamRepository _mediaStreamRepository;
        private readonly IItemRepository _itemRepository;

        public StrmMediaInfoService(
            ILibraryManager libraryManager,
            IMediaEncoder mediaEncoder,
            IMediaStreamRepository mediaStreamRepository,
            IItemRepository itemRepository,
            ILogger logger)
        {
            _libraryManager = libraryManager;
            _mediaEncoder = mediaEncoder;
            _mediaStreamRepository = mediaStreamRepository;
            _itemRepository = itemRepository;
            _logger = logger;
        }

        /// <summary>
        /// 递归查找目录下所有的 strm 文件
        /// </summary>
        /// <param name="directoryPath">要扫描的目录路径</param>
        /// <param name="cancellationToken">取消令牌</param>
        /// <returns>找到的所有 strm 文件对应的库条目列表</returns>
        public List<BaseItem> FindStrmFilesInDirectory(string directoryPath, CancellationToken cancellationToken)
        {
            var strmFiles = new List<BaseItem>();
            var dirs = new Stack<string>();
            dirs.Push(directoryPath);

            while (dirs.Count > 0)
            {
                if (cancellationToken.IsCancellationRequested)
                {
                    break;
                }

                var current = dirs.Pop();
                try
                {
                    IEnumerable<string> files = Enumerable.Empty<string>();
                    try
                    {
                        files = Directory.EnumerateFiles(current)
                            .Where(path => path.EndsWith(".strm", StringComparison.OrdinalIgnoreCase));
                    }
                    catch (Exception ex)
                    {
                        _logger.LogWarning(ex, "StrmTool - Error enumerating files in {Directory}", current);
                    }

                    foreach (var strmPath in files)
                    {
                        if (cancellationToken.IsCancellationRequested)
                        {
                            break;
                        }

                        try
                        {
                            var item = _libraryManager.FindByPath(strmPath, false);
                            if (item != null)
                            {
                                strmFiles.Add(item);
                            }
                            else
                            {
                                _logger.LogDebug("StrmTool - Could not find library item for path: {Path}", strmPath);
                            }
                        }
                        catch (Exception ex)
                        {
                            _logger.LogDebug(ex, "StrmTool - Error processing file {Path}", strmPath);
                        }
                    }

                    IEnumerable<string> subDirs = Enumerable.Empty<string>();
                    try
                    {
                        subDirs = Directory.EnumerateDirectories(current);
                    }
                    catch (Exception ex)
                    {
                        _logger.LogWarning(ex, "StrmTool - Error enumerating directories in {Directory}", current);
                    }

                    foreach (var sub in subDirs)
                    {
                        if (cancellationToken.IsCancellationRequested)
                        {
                            break;
                        }

                        dirs.Push(sub);
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "StrmTool - Error scanning directory {Directory}", current);
                }
            }

            return strmFiles;
        }

        /// <summary>
        /// 获取库中所有的 strm 文件（去重）
        /// </summary>
        /// <param name="cancellationToken">取消令牌</param>
        /// <returns>所有 strm 文件对应的库条目列表，永不为 null</returns>
        public List<BaseItem> GetAllStrmItems(CancellationToken cancellationToken)
        {
            var rootFolders = _libraryManager.GetVirtualFolders()
                .SelectMany(vf => vf.Locations)
                .Distinct()
                .ToList();

            var strmItems = new List<BaseItem>();
            foreach (var rootFolder in rootFolders)
            {
                if (cancellationToken.IsCancellationRequested)
                {
                    break;
                }

                try
                {
                    strmItems.AddRange(FindStrmFilesInDirectory(rootFolder, cancellationToken));
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "StrmTool - Error scanning folder {Folder}", rootFolder);
                }
            }

            return strmItems
                .Where(i => i != null && !string.IsNullOrWhiteSpace(i.Path))
                .GroupBy(i => i.Path, StringComparer.OrdinalIgnoreCase)
                .Select(g => g.First())
                .ToList();
        }

        /// <summary>
        /// 获取库条目的媒体流信息
        /// </summary>
        /// <param name="item">库条目对象</param>
        /// <returns>媒体流列表，若无法获取则返回空列表</returns>
        public List<MediaStream> GetItemMediaStreams(BaseItem item)
        {
            try
            {
                // 使用 IHasMediaSources 接口获取媒体流，避免反射
                if (item is IHasMediaSources hasMediaSources)
                {
                    var streams = hasMediaSources.GetMediaStreams();
                    if (streams != null)
                    {
                        return streams.ToList();
                    }
                }

                return new List<MediaStream>();
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "StrmTool - Error getting media streams for {ItemType}", item.GetType().Name);
                return new List<MediaStream>();
            }
        }

        /// <summary>
        /// 保存库条目的媒体流信息
        /// </summary>
        /// <param name="itemId">库条目ID</param>
        /// <param name="mediaStreams">要保存的媒体流列表</param>
        /// <param name="cancellationToken">取消令牌</param>
        public void SaveMediaStreams(Guid itemId, List<MediaStream> mediaStreams, CancellationToken cancellationToken)
        {
            _mediaStreamRepository.SaveMediaStreams(itemId, mediaStreams, cancellationToken);
        }

        public async Task<List<MediaStream>> ProbeAndSaveMediaStreamsAsync(BaseItem item, CancellationToken cancellationToken)
        {
            var result = await ProbeMediaStreamsAsync(item, cancellationToken).ConfigureAwait(false);
            return result.MediaStreams;
        }

        /// <summary>
        /// 探测媒体流并返回完整结果（包含元数据）
        /// </summary>
        public async Task<MediaProbeResult> ProbeMediaStreamsAsync(BaseItem item, CancellationToken cancellationToken)
        {
            var fileName = Path.GetFileNameWithoutExtension(item.Path);
            var result = new MediaProbeResult();

            try
            {
                var strmContent = ReadStrmSourcePath(item.Path);
                if (string.IsNullOrWhiteSpace(strmContent))
                {
                    _logger.LogWarning("StrmTool - STRM file is empty: {Path}", item.Path);
                    return result;
                }

                var isAudio = item.MediaType == MediaType.Audio;
                var mediaInfo = await _mediaEncoder.GetMediaInfo(
                    new MediaInfoRequest
                    {
                        MediaSource = new MediaSourceInfo
                        {
                            Path = strmContent,
                            Protocol = GetProtocolFromPath(strmContent),
                        },
                        MediaType = isAudio ? DlnaProfileType.Audio : DlnaProfileType.Video,
                        ExtractChapters = false,
                    },
                    cancellationToken).ConfigureAwait(false);

                if (mediaInfo?.MediaStreams != null && mediaInfo.MediaStreams.Count > 0)
                {
                    // 先更新 item 属性，这些修改会随 UpdateToRepositoryAsync 一起持久化
                    item.Size = mediaInfo.Size.GetValueOrDefault();
                    item.RunTimeTicks = mediaInfo.RunTimeTicks;
                    item.Container = mediaInfo.Container;
                    item.TotalBitrate = mediaInfo.Bitrate.GetValueOrDefault();

                    var videoStream = mediaInfo.MediaStreams
                        .Where(s => s.Type == MediaStreamType.Video && s.Width.HasValue && s.Height.HasValue)
                        .OrderByDescending(s => (long)(s.Width ?? 0) * (s.Height ?? 0))
                        .FirstOrDefault();

                    if (videoStream != null)
                    {
                        item.Width = videoStream.Width ?? 0;
                        item.Height = videoStream.Height ?? 0;
                    }

                    // 保存媒体流信息
                    _mediaStreamRepository.SaveMediaStreams(item.Id, mediaInfo.MediaStreams, cancellationToken);

                    // 同时持久化 item 属性修改
                    await SaveItemAsync(item, cancellationToken).ConfigureAwait(false);

                    // 填充返回结果
                    result.MediaStreams = mediaInfo.MediaStreams.ToList();
                    result.Size = mediaInfo.Size.GetValueOrDefault();
                    result.RunTimeTicks = mediaInfo.RunTimeTicks;
                    result.Container = mediaInfo.Container;
                    result.TotalBitrate = mediaInfo.Bitrate.GetValueOrDefault();
                    result.Width = videoStream?.Width ?? 0;
                    result.Height = videoStream?.Height ?? 0;

                    _logger.LogDebug("StrmTool - Successfully saved {Count} media streams and updated item properties for {Name}",
                        mediaInfo.MediaStreams.Count, fileName);
                    return result;
                }

                _logger.LogDebug("StrmTool - No media streams found for {Name}", fileName);
                return result;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "StrmTool - Error probing STRM content for {Name}", fileName);
                return result;
            }
        }

        private static MediaProtocol GetProtocolFromPath(string path)
        {
            if (path.StartsWith("http://", StringComparison.OrdinalIgnoreCase) ||
                path.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
            {
                return MediaProtocol.Http;
            }

            if (path.StartsWith("rtmp://", StringComparison.OrdinalIgnoreCase))
            {
                return MediaProtocol.Rtmp;
            }

            if (path.StartsWith("rtsp://", StringComparison.OrdinalIgnoreCase))
            {
                return MediaProtocol.Rtsp;
            }

            if (path.StartsWith("ftp://", StringComparison.OrdinalIgnoreCase))
            {
                return MediaProtocol.Ftp;
            }

            return MediaProtocol.File;
        }

        private static string ReadStrmSourcePath(string strmFilePath)
        {
            try
            {
                foreach (var line in File.ReadLines(strmFilePath))
                {
                    var sourcePath = line.Trim();
                    if (!string.IsNullOrWhiteSpace(sourcePath))
                    {
                        return sourcePath;
                    }
                }

                return string.Empty;
            }
            catch (FileNotFoundException)
            {
                return string.Empty;
            }
            catch (DirectoryNotFoundException)
            {
                return string.Empty;
            }
            catch (IOException)
            {
                return string.Empty;
            }
            catch (UnauthorizedAccessException)
            {
                return string.Empty;
            }
        }

        private async Task SaveItemAsync(BaseItem item, CancellationToken cancellationToken)
        {
            try
            {
                await item.UpdateToRepositoryAsync(ItemUpdateType.MetadataImport, cancellationToken).ConfigureAwait(false);
                _logger.LogDebug("StrmTool - Successfully saved item changes via UpdateToRepositoryAsync");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "StrmTool - Error saving item changes");
            }
        }
    }
}
