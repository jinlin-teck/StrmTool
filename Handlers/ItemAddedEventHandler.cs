using System;
using System.Threading;
using System.Threading.Tasks;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Logging;
using StrmTool.Common;

namespace StrmTool.Handlers
{
    public class ItemAddedEventHandler
    {
        private readonly ILogger _logger;
        private readonly CancellationTokenSource? _cancellationTokenSource;
        private readonly MediaInfoManager _mediaInfoManager;
        private readonly StrmFileProcessor _strmFileProcessor;
        private int _pendingTaskCount;
        private const int MaxPendingTasks = 100;

        public ItemAddedEventHandler(
            ILogger logger,
            MediaInfoManager mediaInfoManager,
            StrmFileProcessor strmFileProcessor,
            CancellationTokenSource? cancellationTokenSource = null)
        {
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
            _mediaInfoManager = mediaInfoManager ?? throw new ArgumentNullException(nameof(mediaInfoManager));
            _strmFileProcessor = strmFileProcessor ?? throw new ArgumentNullException(nameof(strmFileProcessor));
            _cancellationTokenSource = cancellationTokenSource;
        }

        public void OnItemAdded(object sender, ItemChangeEventArgs e)
        {
            try
            {
                if (e.Item == null || string.IsNullOrEmpty(e.Item.Path) ||
                    !MediaInfoHelper.IsStrmFile(e.Item.Path))
                {
                    return;
                }

                if (_cancellationTokenSource?.IsCancellationRequested == true)
                {
                    return;
                }

                // 每个事件只取一次配置
                var config = Plugin.GetSafeConfiguration();
                if (!config.EnableAutoExtract)
                {
                    Common.LogHelper.Debug(_logger, $"Auto-extract is disabled, skipping: {e.Item.Name}");
                    return;
                }

                // 检查待处理任务数量，防止内存压力
                if (Interlocked.Increment(ref _pendingTaskCount) > MaxPendingTasks)
                {
                    Interlocked.Decrement(ref _pendingTaskCount);
                    Common.LogHelper.Warn(_logger, $"Too many pending tasks ({MaxPendingTasks}), skipping {e.Item.Name}. It will be processed by scheduled task.");
                    return;
                }

                var cancellationToken = _cancellationTokenSource?.Token ?? CancellationToken.None;
                var delayMs = config.ProcessingDelayMs;

                // 不把 token 传给 Task.Run：即使任务在调度前被取消，委托仍会执行到 finally 里的计数回收
                _ = Task.Run(async () =>
                {
                    try
                    {
                        await ProcessItemWithErrorHandlingAsync(e.Item, delayMs, cancellationToken).ConfigureAwait(false);
                    }
                    catch (Exception ex)
                    {
                        Common.LogHelper.Error(_logger, $"Unhandled error in background task for {e.Item.Name}: {ex.Message}");
                    }
                    finally
                    {
                        Interlocked.Decrement(ref _pendingTaskCount);
                    }
                });
            }
            catch (Exception ex)
            {
                // 事件处理器绝不能把异常抛回 Emby 的事件管线
                Common.LogHelper.Error(_logger, $"Error handling item added event: {ex.Message}");
            }
        }

        private async Task ProcessItemWithErrorHandlingAsync(BaseItem item, int delayMs, CancellationToken cancellationToken)
        {
            try
            {
                cancellationToken.ThrowIfCancellationRequested();

                // 完整性检查放在后台线程，避免同步仓储/文件 IO 阻塞媒体库扫描线程；
                // 同时把日志也移到这里，避免全库扫描时对已完成条目刷屏。
                if (MediaInfoHelper.HasCompleteMediaInfo(item, _mediaInfoManager) &&
                    MediaInfoHelper.HasJsonFile(item, _mediaInfoManager))
                {
                    Common.LogHelper.Debug(_logger, $"{item.Name} already has complete media info, skipping");
                    return;
                }

                Common.LogHelper.Info(_logger, $"New strm file detected: {item.Name}");

                // 从JSON恢复是纯本地操作：不延迟、不占用探测并发槽
                if (MediaInfoHelper.ShouldRestoreFromJson(item, _mediaInfoManager))
                {
                    var restoreResult = await _strmFileProcessor.TryRestoreFromJsonAsync(item, cancellationToken).ConfigureAwait(false);
                    LogProcessResult(item.Name, restoreResult);
                    if (restoreResult.IsTerminalSuccess())
                    {
                        return;
                    }
                }

                // 数据库已完整时只在本地补备份，不延迟、不占远程探测槽
                var ensured = await _strmFileProcessor.TryEnsureJsonFromDatabaseAsync(item, cancellationToken).ConfigureAwait(false);
                if (ensured != null)
                {
                    LogProcessResult(item.Name, ensured.Value);
                    return;
                }

                // 需要探测远程媒体：先占用全局探测槽，再应用延迟，避免对远程服务器造成压力
                var limiter = await ProcessingConcurrencyLimiter.AcquireAsync(cancellationToken).ConfigureAwait(false);
                try
                {
                    if (delayMs > 0)
                    {
                        Common.LogHelper.Debug(_logger, $"Applying delay: {delayMs}ms before processing {item.Name}");
                        await Task.Delay(delayMs, cancellationToken).ConfigureAwait(false);
                    }

                    var result = await _strmFileProcessor.ProbeAndExportAsync(item, cancellationToken).ConfigureAwait(false);
                    LogProcessResult(item.Name, result);
                }
                finally
                {
                    ProcessingConcurrencyLimiter.Release(limiter);
                }
            }
            catch (OperationCanceledException)
            {
                Common.LogHelper.Debug(_logger, $"Processing cancelled for {item.Name}");
            }
            catch (Exception ex)
            {
                Common.LogHelper.Error(_logger, $"Error processing item {item.Name}: {ex.Message}");
            }
        }

        private void LogProcessResult(string itemName, ProcessResult result)
        {
            switch (result)
            {
                case ProcessResult.Skipped:
                    Common.LogHelper.Debug(_logger, $"{itemName} was skipped");
                    break;
                case ProcessResult.RestoredFromJson:
                    Common.LogHelper.Info(_logger, $"{itemName} successfully restored from JSON");
                    break;
                case ProcessResult.UpgradedJsonFromDb:
                    Common.LogHelper.Info(_logger, $"{itemName} upgraded legacy JSON from existing DB metadata");
                    break;
                case ProcessResult.NeedsAudioTagProbe:
                    Common.LogHelper.Debug(_logger, $"{itemName} legacy JSON lacks audio tags, probing remote media");
                    break;
                case ProcessResult.RestoreFailed:
                    Common.LogHelper.Debug(_logger, $"{itemName} JSON restore failed, falling back to probing");
                    break;
                case ProcessResult.ExtractedAndExported:
                    Common.LogHelper.Info(_logger, $"{itemName} successfully extracted and exported");
                    break;
                case ProcessResult.ExtractionFailed:
                    Common.LogHelper.Warn(_logger, $"{itemName} extraction failed, will be processed by scheduled task");
                    break;
                case ProcessResult.Failed:
                    Common.LogHelper.Error(_logger, $"{itemName} processing failed");
                    break;
            }
        }
    }
}
