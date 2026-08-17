using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.MediaEncoding;
using MediaBrowser.Controller.Persistence;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.Logging;
using MediaBrowser.Model.Serialization;

namespace StrmTool.Common
{
    /// <summary>
    /// STRM文件处理类，提供恢复与探测两个明确操作。
    /// 不在内部自动从恢复降级到探测——降级涉及节流策略，由持有并发信号量的调用方编排。
    /// </summary>
    public class StrmFileProcessor
    {
        private readonly ILogger _logger;
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
            if (libraryManager == null) throw new ArgumentNullException(nameof(libraryManager));
            if (itemRepository == null) throw new ArgumentNullException(nameof(itemRepository));
            if (mediaProbeManager == null) throw new ArgumentNullException(nameof(mediaProbeManager));
            if (jsonSerializer == null) throw new ArgumentNullException(nameof(jsonSerializer));

            _mediaInfoManager = mediaInfoManager ?? new MediaInfoManager(logger, libraryManager, itemRepository, jsonSerializer);
            _mediaInfoService = new StrmMediaInfoService(logger, libraryManager, mediaProbeManager, itemRepository);
        }

        /// <summary>
        /// 尝试从 JSON 文件恢复媒体信息。
        /// 纯本地操作，不访问远程媒体；失败时返回 RestoreFailed，不自动降级到探测。
        /// </summary>
        public async Task<ProcessResult> TryRestoreFromJsonAsync(
            BaseItem item,
            CancellationToken cancellationToken = default)
        {
            using var monitor = new PerformanceMonitor(_logger, "Restore", item.Name);

            try
            {
                if (MediaInfoHelper.HasCompleteMediaInfo(item))
                {
                    LogHelper.Info(_logger, $"{item.Name} already has complete media info, skipping...");
                    return ProcessResult.Skipped;
                }

                LogHelper.Debug(_logger, $"Attempting to restore {item.Name} from JSON...");

                var restored = await _mediaInfoManager.RestoreItemAsync(item, cancellationToken).ConfigureAwait(false);
                if (!restored)
                {
                    LogHelper.Warn(_logger, $"JSON restore failed for {item.Name}");
                    return ProcessResult.RestoreFailed;
                }

                // 恢复后重新查询一次媒体流，用于确认结果
                var (hasVideo, hasAudio) = MediaInfoHelper.GetStreamSummary(item.GetMediaStreams());

                LogHelper.Debug(_logger, $"{item.Name}: Restored from JSON. Video:{hasVideo}, Audio:{hasAudio}");
                return ProcessResult.RestoredFromJson;
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                LogHelper.Error(_logger, $"Error restoring {item.Name} ({item.Path}): {ex.Message}");
                return ProcessResult.Failed;
            }
        }

        /// <summary>
        /// 探测远程媒体信息并导出到 JSON 文件。
        /// 会访问远程媒体源，调用方负责并发限制与探测前延迟。
        /// </summary>
        public async Task<ProcessResult> ExtractAndExportAsync(
            BaseItem item,
            CancellationToken cancellationToken = default)
        {
            using var monitor = new PerformanceMonitor(_logger, "Processing", item.Name);

            try
            {
                LogHelper.Debug(_logger, $"Probing media info for {item.Name}...");

                // 探测前再确认一次：等待期间媒体信息可能已被其他途径补全
                var beforeStreams = item.GetMediaStreams() ?? new List<MediaStream>();
                if (MediaInfoHelper.HasCompleteMediaInfo(beforeStreams))
                {
                    LogHelper.Info(_logger, $"{item.Name} already has complete media info, skipping...");
                    return ProcessResult.Skipped;
                }

                var streams = await _mediaInfoService.ProbeAndSaveMediaStreamsAsync(item, cancellationToken).ConfigureAwait(false);

                var (hasVideo, hasAudio) = MediaInfoHelper.GetStreamSummary(item.GetMediaStreams());

                LogHelper.Info(_logger, $"{item.Name}: Probed media info. Streams {beforeStreams.Count}→{streams.Count}. Video:{hasVideo}, Audio:{hasAudio}");

                // 只要有任意一种媒体流就算成功
                if (!hasVideo && !hasAudio)
                {
                    LogHelper.Warn(_logger, $"{item.Name} may still lack full media info");
                    return ProcessResult.ExtractionFailed;
                }

                var exportSucceeded = await _mediaInfoManager.ExportItemAsync(item, cancellationToken).ConfigureAwait(false);
                if (!exportSucceeded)
                {
                    LogHelper.Warn(_logger, $"{item.Name}: Media info extraction succeeded but JSON export failed");
                    return ProcessResult.ExtractionFailed;
                }

                LogHelper.Debug(_logger, $"{item.Name}: Media info exported to JSON file");
                return ProcessResult.ExtractedAndExported;
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                LogHelper.Error(_logger, $"Error processing {item.Name} ({item.Path}): {ex.Message}");
                return ProcessResult.Failed;
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
        /// 从JSON恢复失败（JSON缺失、损坏或内容无效），调用方可降级到探测
        /// </summary>
        RestoreFailed,

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
