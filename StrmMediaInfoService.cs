using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
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
    /// STRM 媒体流相关共享服务：扫描、读取媒体流、探测远程媒体并写入数据库。
    /// </summary>
    public class StrmMediaInfoService
    {
        private readonly ILogger _logger;
        private readonly ILibraryManager _libraryManager;
        private readonly IMediaEncoder _mediaEncoder;
        private readonly IMediaStreamRepository _mediaStreamRepository;

        private static readonly ConcurrentDictionary<Type, Func<BaseItem, List<MediaStream>>> MediaStreamResolvers
            = new ConcurrentDictionary<Type, Func<BaseItem, List<MediaStream>>>();

        private const int MaxResolverCacheSize = 100;

        public StrmMediaInfoService(
            ILibraryManager libraryManager,
            IMediaEncoder mediaEncoder,
            IMediaStreamRepository mediaStreamRepository,
            ILogger logger)
        {
            _libraryManager = libraryManager;
            _mediaEncoder = mediaEncoder;
            _mediaStreamRepository = mediaStreamRepository;
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

            CleanupResolverCacheIfNeeded();

            return strmItems
                .Where(i => i != null && !string.IsNullOrWhiteSpace(i.Path))
                .GroupBy(i => i.Path, StringComparer.OrdinalIgnoreCase)
                .Select(g => g.First())
                .ToList();
        }

        private static void CleanupResolverCacheIfNeeded()
        {
            if (MediaStreamResolvers.Count >= MaxResolverCacheSize)
            {
                var keysToRemove = MediaStreamResolvers.Keys.Take(MaxResolverCacheSize / 2).ToList();
                foreach (var key in keysToRemove)
                {
                    MediaStreamResolvers.TryRemove(key, out _);
                }
            }
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
                var type = item.GetType();

                var resolver = MediaStreamResolvers.GetOrAdd(type, t =>
                {
                    var prop = t.GetProperty("MediaStreams", BindingFlags.Public | BindingFlags.Instance);
                    if (prop != null && typeof(IEnumerable<MediaStream>).IsAssignableFrom(prop.PropertyType))
                    {
                        return new Func<BaseItem, List<MediaStream>>(bi =>
                        {
                            var value = prop.GetValue(bi);
                            if (value is List<MediaStream> list)
                            {
                                return list;
                            }

                            if (value is IEnumerable<MediaStream> enumerable)
                            {
                                return enumerable.ToList();
                            }

                            return new List<MediaStream>();
                        });
                    }

                    var method = t.GetMethod("GetMediaStreams", BindingFlags.Public | BindingFlags.Instance);
                    if (method != null && typeof(IEnumerable<MediaStream>).IsAssignableFrom(method.ReturnType))
                    {
                        return new Func<BaseItem, List<MediaStream>>(bi =>
                        {
                            var result = method.Invoke(bi, null);
                            if (result is List<MediaStream> list)
                            {
                                return list;
                            }

                            if (result is IEnumerable<MediaStream> enumerable)
                            {
                                return enumerable.ToList();
                            }

                            return new List<MediaStream>();
                        });
                    }

                    return _ => new List<MediaStream>();
                });

                return resolver(item);
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
            var fileName = Path.GetFileNameWithoutExtension(item.Path);

            try
            {
                var strmContent = ReadStrmSourcePath(item.Path);
                if (string.IsNullOrWhiteSpace(strmContent))
                {
                    _logger.LogWarning("StrmTool - STRM file is empty: {Path}", item.Path);
                    return new List<MediaStream>();
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
                    _mediaStreamRepository.SaveMediaStreams(item.Id, mediaInfo.MediaStreams, cancellationToken);
                    _logger.LogDebug("StrmTool - Successfully saved {Count} media streams for {Name}",
                        mediaInfo.MediaStreams.Count, fileName);
                    return mediaInfo.MediaStreams.ToList();
                }

                _logger.LogDebug("StrmTool - No media streams found for {Name}", fileName);
                return new List<MediaStream>();
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "StrmTool - Error probing STRM content for {Name}", fileName);
                return new List<MediaStream>();
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
    }
}
