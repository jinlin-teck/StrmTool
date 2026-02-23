using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.MediaEncoding;
using MediaBrowser.Controller.Persistence;
using MediaBrowser.Model.Dlna;
using MediaBrowser.Model.Dto;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.Logging;
using MediaBrowser.Model.MediaInfo;

namespace StrmTool.Common
{
    public class StrmMediaInfoService
    {
        private readonly ILogger _logger;
        private readonly ILibraryManager _libraryManager;
        private readonly IMediaProbeManager _mediaProbeManager;
        private readonly IItemRepository _itemRepository;

        public StrmMediaInfoService(
            ILogger logger,
            ILibraryManager libraryManager,
            IMediaProbeManager mediaProbeManager,
            IItemRepository itemRepository)
        {
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
            _libraryManager = libraryManager ?? throw new ArgumentNullException(nameof(libraryManager));
            _mediaProbeManager = mediaProbeManager ?? throw new ArgumentNullException(nameof(mediaProbeManager));
            _itemRepository = itemRepository ?? throw new ArgumentNullException(nameof(itemRepository));
        }

        public async Task<List<MediaStream>> ProbeAndSaveMediaStreamsAsync(BaseItem item, CancellationToken cancellationToken)
        {
            var fileName = Path.GetFileNameWithoutExtension(item.Path);

            try
            {
                var strmContent = ReadStrmSourcePath(item.Path, _logger);
                if (string.IsNullOrWhiteSpace(strmContent))
                {
                    Common.LogHelper.Warn(_logger, $"STRM file is empty: {item.Path}");
                    return new List<MediaStream>();
                }

                var isAudio = item.MediaType == MediaType.Audio;
                var mediaInfo = await _mediaProbeManager.GetMediaInfo(
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
                    _itemRepository.SaveMediaStreams(item.InternalId, mediaInfo.MediaStreams, cancellationToken);
                    
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

                    _libraryManager.UpdateItems(new List<BaseItem> { item }, null,
                        ItemUpdateType.MetadataImport, false, false, null, cancellationToken);
                    
                    Common.LogHelper.Debug(_logger, $"Successfully saved {mediaInfo.MediaStreams.Count} media streams and updated item properties for {fileName}");
                    return mediaInfo.MediaStreams.ToList();
                }

                Common.LogHelper.Debug(_logger, $"No media streams found for {fileName}");
                return new List<MediaStream>();
            }
            catch (Exception ex)
            {
                Common.LogHelper.ErrorException(_logger, $"Error probing STRM content for {fileName}", ex);
                return new List<MediaStream>();
            }
        }

        private static MediaProtocol GetProtocolFromPath(string path)
        {
            return path?.ToLowerInvariant() switch
            {
                var p when p?.StartsWith("http://") == true || p?.StartsWith("https://") == true => MediaProtocol.Http,
                var p when p?.StartsWith("rtmp://") == true => MediaProtocol.Rtmp,
                var p when p?.StartsWith("rtsp://") == true => MediaProtocol.Rtsp,
                var p when p?.StartsWith("ftp://") == true => MediaProtocol.Ftp,
                _ => MediaProtocol.File
            };
        }

        /// <summary>
        /// 验证媒体路径格式是否有效（支持URL、本地路径、UNC路径等Emby可访问的任何格式）
        /// </summary>
        private static bool IsValidMediaPath(string path)
        {
            // 排除空字符串和明显无效的内容（如注释行）
            if (string.IsNullOrWhiteSpace(path))
                return false;
            
            // 排除以 # 开头的注释行
            if (path.TrimStart().StartsWith("#", StringComparison.Ordinal))
                return false;
            
            // 其他所有非空内容都认为是有效的，交给Emby处理
            // 支持格式：
            // - URL: http://, https://, rtmp://, rtsp://, ftp://
            // - Linux路径: /media/movies/somemovie.mp4
            // - Windows路径: C:\Movies\some.mov, \\server\share\movie.mp4
            // - 相对路径: movies/somemovie.mp4
            // - 甚至简单的文件名
            return true;
        }

        private static string ReadStrmSourcePath(string strmFilePath, ILogger logger)
        {
            try
            {
                if (!File.Exists(strmFilePath))
                {
                    Common.LogHelper.Warn(logger, $"STRM file not found: {strmFilePath}");
                    return string.Empty;
                }
                
                using var stream = File.OpenText(strmFilePath);
                string? line;
                while ((line = stream.ReadLine()) != null)
                {
                    var sourcePath = line.Trim();
                    if (!string.IsNullOrWhiteSpace(sourcePath) && IsValidMediaPath(sourcePath))
                    {
                        return sourcePath;
                    }
                }

                return string.Empty;
            }
            catch (Exception ex)
            {
                Common.LogHelper.ErrorException(logger, $"Error reading STRM file: {strmFilePath}", ex);
                return string.Empty;
            }
        }
    }
}
