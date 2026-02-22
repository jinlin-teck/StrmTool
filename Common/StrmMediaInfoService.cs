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
                    LogHelper.Warn(_logger, $"STRM file is empty: {item.Path}");
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
                    
                    LogHelper.Debug(_logger, $"Successfully saved {mediaInfo.MediaStreams.Count} media streams and updated item properties for {fileName}");
                    return mediaInfo.MediaStreams.ToList();
                }

                LogHelper.Debug(_logger, $"No media streams found for {fileName}");
                return new List<MediaStream>();
            }
            catch (Exception ex)
            {
                LogHelper.ErrorException(_logger, $"Error probing STRM content for {fileName}", ex);
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

        private static string ReadStrmSourcePath(string strmFilePath, ILogger logger)
        {
            try
            {
                if (!File.Exists(strmFilePath))
                {
                    LogHelper.Warn(logger, $"STRM file not found: {strmFilePath}");
                    return string.Empty;
                }
                
                using var stream = File.OpenText(strmFilePath);
                string? line;
                while ((line = stream.ReadLine()) != null)
                {
                    var sourcePath = line.Trim();
                    if (!string.IsNullOrWhiteSpace(sourcePath))
                    {
                        return sourcePath;
                    }
                }

                return string.Empty;
            }
            catch (Exception ex)
            {
                LogHelper.ErrorException(logger, $"Error reading STRM file: {strmFilePath}", ex);
                return string.Empty;
            }
        }
    }
}
