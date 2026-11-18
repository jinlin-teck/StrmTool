using System;
using System.Collections.Generic;
using System.IO;
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
        /// <summary>
        /// .strm 文本文件最大允许读取字节数（64 KB），防止误命名为 .strm 的大体积二进制文件导致 ReadLineAsync OOM
        /// </summary>
        private const long MaxStrmFileBytes = 64 * 1024;

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
                    Common.LogHelper.Warn(_logger, $"STRM file is empty or invalid: {item.Path}");
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
                        ExtractChapters = !isAudio,
                    },
                    cancellationToken).ConfigureAwait(false);

                if (mediaInfo?.MediaStreams != null && mediaInfo.MediaStreams.Count > 0)
                {
                    var streamsToSave = MediaStreamMerger.PrepareMediaStreamsForSave(
                        item, mediaInfo.MediaStreams, _libraryManager);
                    _itemRepository.SaveMediaStreams(item.InternalId, streamsToSave, cancellationToken);

                    SaveProbedChaptersIfAbsent(item, isAudio, mediaInfo);
                    MediaInfoHelper.ApplyMediaSourceInfo(item, mediaInfo, _libraryManager, cancellationToken);

                    Common.LogHelper.Debug(_logger, $"Successfully saved {streamsToSave.Count} media streams and updated item properties for {fileName}");
                    return streamsToSave;
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

        private void SaveProbedChaptersIfAbsent(BaseItem item, bool isAudio, MediaInfo mediaInfo)
        {
            if (isAudio ||
                !(item is Video) ||
                mediaInfo.Chapters == null ||
                mediaInfo.Chapters.Length == 0)
            {
                return;
            }

            var existingChapters = _itemRepository.GetChapters(item);
            if (existingChapters == null || existingChapters.Count == 0)
            {
                _itemRepository.SaveChapters(item.InternalId, new List<ChapterInfo>(mediaInfo.Chapters));
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

        private static async Task<string> ReadStrmSourcePathAsync(string strmFilePath, ILogger logger, CancellationToken cancellationToken)
        {
            try
            {
                var fileInfo = new FileInfo(strmFilePath);
                if (!fileInfo.Exists)
                {
                    Common.LogHelper.Warn(logger, $"STRM file not found: {strmFilePath}");
                    return string.Empty;
                }

                if (fileInfo.Length > MaxStrmFileBytes)
                {
                    Common.LogHelper.Warn(
                        logger,
                        $"STRM file exceeds maximum allowed size ({fileInfo.Length} bytes > {MaxStrmFileBytes} bytes), skipping: {strmFilePath}");
                    return string.Empty;
                }

                using var reader = new StreamReader(strmFilePath);
                while (true)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    string? line = await reader.ReadLineAsync().ConfigureAwait(false);
                    if (line == null)
                    {
                        break;
                    }

                    // 跳过空行与以 # 开头的注释行，并剥离可能残留的零宽 BOM (\uFEFF)，避免协议前缀匹配失效
                    var sourcePath = line.Trim().Trim('\uFEFF');
                    if (!string.IsNullOrWhiteSpace(sourcePath) &&
                        !sourcePath.StartsWith("#", StringComparison.Ordinal))
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
