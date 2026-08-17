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
                var strmContent = await ReadStrmSourcePathAsync(item.Path, _logger, cancellationToken).ConfigureAwait(false);
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

                    MediaInfoHelper.ApplyMediaSourceInfo(item, mediaInfo, _libraryManager, cancellationToken);

                    Common.LogHelper.Debug(_logger, $"Successfully saved {mediaInfo.MediaStreams.Count} media streams and updated item properties for {fileName}");
                    return mediaInfo.MediaStreams.ToList();
                }

                Common.LogHelper.Debug(_logger, $"No media streams found for {fileName}");
                return new List<MediaStream>();
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                Common.LogHelper.ErrorException(_logger, $"Error probing STRM content for {fileName}", ex);
                return new List<MediaStream>();
            }
        }

        private static MediaProtocol GetProtocolFromPath(string path)
        {
            // 使用 OrdinalIgnoreCase 比较，避免 ToLowerInvariant 产生字符串分配
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

        private static async Task<string> ReadStrmSourcePathAsync(string strmFilePath, ILogger logger, CancellationToken cancellationToken)
        {
            try
            {
                if (!File.Exists(strmFilePath))
                {
                    Common.LogHelper.Warn(logger, $"STRM file not found: {strmFilePath}");
                    return string.Empty;
                }

                using var reader = new StreamReader(strmFilePath);
                string? line;
                while (true)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    line = await reader.ReadLineAsync().ConfigureAwait(false);
                    if (line == null)
                    {
                        break;
                    }

                    var sourcePath = line.Trim();
                    if (!string.IsNullOrWhiteSpace(sourcePath) && IsValidMediaPath(sourcePath))
                    {
                        return sourcePath;
                    }
                }

                return string.Empty;
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                Common.LogHelper.ErrorException(logger, $"Error reading STRM file: {strmFilePath}", ex);
                return string.Empty;
            }
        }
    }
}
