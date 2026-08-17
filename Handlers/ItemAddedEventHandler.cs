using System;
using System.Threading;
using System.Threading.Tasks;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.MediaEncoding;
using MediaBrowser.Controller.Persistence;
using MediaBrowser.Model.Logging;
using MediaBrowser.Model.Serialization;
using StrmTool.Common;

namespace StrmTool.Handlers
{
    public class ItemAddedEventHandler : IDisposable
    {
        private readonly ILogger _logger;
        private readonly CancellationTokenSource? _cancellationTokenSource;
        private readonly SemaphoreSlim _semaphore;
        private readonly MediaInfoManager _mediaInfoManager;
        private readonly StrmFileProcessor _strmFileProcessor;
        private int _pendingTaskCount;
        private const int MaxPendingTasks = 100;
        private bool _disposed;

        public ItemAddedEventHandler(
            ILogger logger,
            ILibraryManager libraryManager,
            IItemRepository itemRepository,
            IJsonSerializer jsonSerializer,
            IMediaProbeManager mediaProbeManager,
            CancellationTokenSource? cancellationTokenSource = null,
            MediaInfoManager? mediaInfoManager = null,
            StrmFileProcessor? strmFileProcessor = null)
        {
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
            if (libraryManager == null) throw new ArgumentNullException(nameof(libraryManager));
            if (itemRepository == null) throw new ArgumentNullException(nameof(itemRepository));
            if (jsonSerializer == null) throw new ArgumentNullException(nameof(jsonSerializer));
            if (mediaProbeManager == null) throw new ArgumentNullException(nameof(mediaProbeManager));
            _cancellationTokenSource = cancellationTokenSource;

            // 使用用户配置的 MaxConcurrency，默认为3
            var config = Plugin.GetSafeConfiguration();
            var maxConcurrency = config.MaxConcurrency;
            _semaphore = new SemaphoreSlim(maxConcurrency, maxConcurrency);

            _mediaInfoManager = mediaInfoManager ?? new MediaInfoManager(logger, libraryManager, itemRepository, jsonSerializer);
            _strmFileProcessor = strmFileProcessor ?? new StrmFileProcessor(
                logger, libraryManager, itemRepository, mediaProbeManager, jsonSerializer, _mediaInfoManager);
        }

        public void Dispose()
        {
            Dispose(true);
            GC.SuppressFinalize(this);
        }

        protected virtual void Dispose(bool disposing)
        {
            if (_disposed)
                return;

            if (disposing)
            {
                _semaphore.Dispose();
            }

            _disposed = true;
        }

        public void OnItemAdded(object sender, ItemChangeEventArgs e)
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

            // 已具备完整媒体信息的项无需排队，直接跳过（避免无意义的延迟和任务入队）
            if (MediaInfoHelper.HasCompleteMediaInfo(e.Item))
            {
                Common.LogHelper.Debug(_logger, $"{e.Item.Name} already has complete media info, skipping");
                return;
            }

            Common.LogHelper.Info(_logger, $"New strm file detected: {e.Item.Name}");

            // 检查待处理任务数量，防止内存压力
            if (Interlocked.Increment(ref _pendingTaskCount) > MaxPendingTasks)
            {
                Interlocked.Decrement(ref _pendingTaskCount);
                Common.LogHelper.Warn(_logger, $"Too many pending tasks ({MaxPendingTasks}), skipping {e.Item.Name}. It will be processed by scheduled task.");
                return;
            }

            var cancellationToken = _cancellationTokenSource?.Token ?? CancellationToken.None;
            var delayMs = config.ProcessingDelayMs;

            // 使用有限并发控制处理新文件
            _ = Task.Run(async () =>
            {
                try
                {
                    await ProcessItemWithErrorHandlingAsync(e.Item, delayMs, cancellationToken);
                }
                catch (Exception ex)
                {
                    Common.LogHelper.Error(_logger, $"Unhandled error in background task for {e.Item.Name}: {ex.Message}");
                }
                finally
                {
                    Interlocked.Decrement(ref _pendingTaskCount);
                }
            }, cancellationToken);
        }

        private async Task ProcessItemWithErrorHandlingAsync(BaseItem item, int delayMs, CancellationToken cancellationToken)
        {
            try
            {
                cancellationToken.ThrowIfCancellationRequested();

                // 从JSON恢复是纯本地操作：不延迟、不占用探测并发槽
                if (MediaInfoHelper.ShouldRestoreFromJson(item, _mediaInfoManager))
                {
                    var restoreResult = await _strmFileProcessor.TryRestoreFromJsonAsync(item, cancellationToken).ConfigureAwait(false);
                    if (restoreResult == ProcessResult.RestoredFromJson || restoreResult == ProcessResult.Skipped)
                    {
                        LogProcessResult(item.Name, restoreResult);
                        return;
                    }

                    LogProcessResult(item.Name, restoreResult);
                }

                // 需要探测远程媒体：先延迟再占用并发槽，避免对远程服务器造成压力
                if (delayMs > 0)
                {
                    Common.LogHelper.Debug(_logger, $"Applying delay: {delayMs}ms before processing {item.Name}");
                    await Task.Delay(delayMs, cancellationToken).ConfigureAwait(false);
                }

                await _semaphore.WaitAsync(cancellationToken).ConfigureAwait(false);
                try
                {
                    var result = await _strmFileProcessor.ExtractAndExportAsync(item, cancellationToken).ConfigureAwait(false);
                    LogProcessResult(item.Name, result);
                }
                finally
                {
                    _semaphore.Release();
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
