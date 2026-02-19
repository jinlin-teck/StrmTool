using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.MediaEncoding;
using MediaBrowser.Controller.Persistence;
using MediaBrowser.Model.Logging;
using MediaBrowser.Model.Serialization;

namespace StrmTool.Common
{
    /// <summary>
    /// STRM文件处理类，提供统一的处理逻辑
    /// </summary>
    public class StrmFileProcessor
    {
        private readonly ILogger _logger;
        private readonly ILibraryManager _libraryManager;
        private readonly IItemRepository _itemRepository;
        private readonly MediaInfoManager _mediaInfoManager;
        private readonly StrmMediaInfoService _mediaInfoService;

        public StrmFileProcessor(
            ILogger logger,
            ILibraryManager libraryManager,
            IItemRepository itemRepository,
            IMediaProbeManager mediaProbeManager,
            IJsonSerializer jsonSerializer,
            MediaInfoManager? mediaInfoManager = null)
        {
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
            _libraryManager = libraryManager ?? throw new ArgumentNullException(nameof(libraryManager));
            _itemRepository = itemRepository ?? throw new ArgumentNullException(nameof(itemRepository));
            if (jsonSerializer == null) throw new ArgumentNullException(nameof(jsonSerializer));

            _mediaInfoManager = mediaInfoManager ?? new MediaInfoManager(logger, libraryManager, itemRepository, jsonSerializer);
            _mediaInfoService = new StrmMediaInfoService(logger, libraryManager, mediaProbeManager, itemRepository);
        }

        /// <summary>
        /// 处理单个STRM文件
        /// </summary>
        public async Task<ProcessResult> ProcessStrmFileAsync(BaseItem item, CancellationToken cancellationToken = default)
        {
            using var monitor = new PerformanceMonitor(_logger, "Processing", item.Name);

            try
            {
                _logger.Debug($"StrmTool - Processing {item.Name}");

                if (MediaInfoHelper.HasCompleteMediaInfo(item))
                {
                    _logger.Info($"StrmTool - {item.Name} already has complete media info, skipping...");
                    return ProcessResult.Skipped;
                }

                if (MediaInfoHelper.ShouldRestoreFromJson(item, _mediaInfoManager))
                {
                    return await RestoreFromJsonAsync(item, cancellationToken);
                }

                return await ExtractAndExportAsync(item, cancellationToken);
            }
            catch (Exception ex)
            {
                _logger.Error($"StrmTool - Error processing {item.Name} ({item.Path}): {ex.Message}");
                return ProcessResult.Failed;
            }
        }

        private async Task<ProcessResult> RestoreFromJsonAsync(BaseItem item, CancellationToken cancellationToken)
        {
            _logger.Debug($"StrmTool - Found JSON file for {item.Name}, attempting to restore from JSON...");

            await _mediaInfoManager.RestoreItemAsync(item, cancellationToken);

            var streams = item.GetMediaStreams() ?? new System.Collections.Generic.List<MediaBrowser.Model.Entities.MediaStream>();
            bool hasVideo = streams.Any(s => s.Type == MediaBrowser.Model.Entities.MediaStreamType.Video);
            bool hasAudio = streams.Any(s => s.Type == MediaBrowser.Model.Entities.MediaStreamType.Audio);

            _logger.Debug($"StrmTool - {item.Name}: Restored from JSON. Video:{hasVideo}, Audio:{hasAudio}");
            return ProcessResult.RestoredFromJson;
        }

        private async Task<ProcessResult> ExtractAndExportAsync(BaseItem item, CancellationToken cancellationToken)
        {
            _logger.Debug($"StrmTool - No JSON file found for {item.Name}, probing media info...");

            var beforeStreams = item.GetMediaStreams() ?? new System.Collections.Generic.List<MediaBrowser.Model.Entities.MediaStream>();
            _logger.Debug($"StrmTool - Before: {beforeStreams.Count} streams");

            var streams = await _mediaInfoService.ProbeAndSaveMediaStreamsAsync(item, cancellationToken);

            var streamList = item.GetMediaStreams() ?? new System.Collections.Generic.List<MediaBrowser.Model.Entities.MediaStream>();
            bool hasVideo = streamList.Any(s => s.Type == MediaBrowser.Model.Entities.MediaStreamType.Video);
            bool hasAudio = streamList.Any(s => s.Type == MediaBrowser.Model.Entities.MediaStreamType.Audio);

            _logger.Info($"StrmTool - {item.Name}: Probed media info. Streams {beforeStreams.Count}→{streams.Count}. Video:{hasVideo}, Audio:{hasAudio}");

            // 只要有任意一种媒体流就算成功
            bool isSuccess = hasVideo || hasAudio;

            if (!isSuccess)
            {
                _logger.Warn($"StrmTool - {item.Name} may still lack full media info");
                return ProcessResult.ExtractionFailed;
            }

            try
            {
                await _mediaInfoManager.ExportItemAsync(item, cancellationToken);
                _logger.Debug($"StrmTool - {item.Name}: Media info exported to JSON file");
                return ProcessResult.ExtractedAndExported;
            }
            catch (Exception ex)
            {
                _logger.Error($"StrmTool - Error exporting {item.Name} to JSON: {ex.Message}");
                return ProcessResult.ExtractionFailed;
            }
        }
    }

    /// <summary>
    /// 处理结果枚举
    /// </summary>
    public enum ProcessResult
    {
        /// <summary>
        /// 已跳过（已有完整信息）
        /// </summary>
        Skipped,
        
        /// <summary>
        /// 从JSON恢复成功
        /// </summary>
        RestoredFromJson,
        
        /// <summary>
        /// 提取并导出成功
        /// </summary>
        ExtractedAndExported,
        
        /// <summary>
        /// 提取失败
        /// </summary>
        ExtractionFailed,
        
        /// <summary>
        /// 处理失败
        /// </summary>
        Failed
    }
}
