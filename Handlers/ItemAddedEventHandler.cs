using System;
using System.Collections.Concurrent;
using System.Threading;
using System.Threading.Tasks;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Logging;
using StrmTool.Common;

namespace StrmTool.Handlers
{
    /// <summary>
    /// 新入库 STRM 事件处理器：
    /// 采用“本地快车道 (_localQueue) + 受限远程探测慢车道 (_probeQueue)”双通道分离调度，
    /// 确保慢速远程探测与节流延迟永远不会阻塞后续具备本地 JSON 备份条目的即时恢复。
    /// </summary>
    public class ItemAddedEventHandler
    {
        private sealed class ProbeWorkItem
        {
            public ProbeWorkItem(
                BaseItem item,
                string dedupKey,
                StrmFileProcessor.InFlightLease? lease,
                bool canFallbackToLegacyJson)
            {
                Item = item;
                DedupKey = dedupKey;
                Lease = lease;
                CanFallbackToLegacyJson = canFallbackToLegacyJson;
            }

            public BaseItem Item { get; }
            public string DedupKey { get; }
            public StrmFileProcessor.InFlightLease? Lease { get; }
            public bool CanFallbackToLegacyJson { get; }
        }

        /// <summary>
        /// 待处理队列最大总容量（本地快车道 + 远程探测队列共享上界）
        /// </summary>
        private const int MaxQueuedItems = 50000;
        private const int MinLocalWorkers = 4;
        private const int MaxLocalWorkers = 8;

        private readonly ILogger _logger;
        private readonly ILibraryManager _libraryManager;
        private readonly MediaInfoManager _mediaInfoManager;
        private readonly StrmFileProcessor _strmFileProcessor;
        private readonly CancellationToken _cancellationToken;

        private readonly ConcurrentQueue<BaseItem> _localQueue = new ConcurrentQueue<BaseItem>();
        private readonly ConcurrentQueue<ProbeWorkItem> _probeQueue = new ConcurrentQueue<ProbeWorkItem>();
        private readonly ConcurrentDictionary<string, byte> _queuedKeys =
            new ConcurrentDictionary<string, byte>(MediaInfoJsonStore.PathStringComparer);
        private int _queuedCount;
        private int _activeLocalWorkers;
        private int _activeProbeWorkers;

        internal int QueuedCount => _localQueue.Count + _probeQueue.Count;
        internal int LocalQueueCount => _localQueue.Count;
        internal int ProbeQueueCount => _probeQueue.Count;

        public ItemAddedEventHandler(
            ILogger logger,
            ILibraryManager libraryManager,
            MediaInfoManager mediaInfoManager,
            StrmFileProcessor strmFileProcessor,
            CancellationToken cancellationToken = default)
        {
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
            _libraryManager = libraryManager ?? throw new ArgumentNullException(nameof(libraryManager));
            _mediaInfoManager = mediaInfoManager ?? throw new ArgumentNullException(nameof(mediaInfoManager));
            _strmFileProcessor = strmFileProcessor ?? throw new ArgumentNullException(nameof(strmFileProcessor));
            _cancellationToken = cancellationToken;
        }

        public void OnItemAdded(object sender, ItemChangeEventArgs e)
        {
            try
            {
                if (e?.Item == null ||
                    string.IsNullOrEmpty(e.Item.Path) ||
                    !MediaInfoHelper.IsStrmFile(e.Item.Path))
                {
                    return;
                }

                LibraryScopeFilter.InvalidateScopedCountCache();

                if (_cancellationToken.IsCancellationRequested)
                {
                    return;
                }

                var config = Plugin.GetSafeConfiguration();
                if (!config.EnableAutoExtract)
                {
                    Common.LogHelper.Debug(_logger, $"Auto-extract is disabled, skipping: {e.Item.Name}");
                    return;
                }

                if (!MediaInfoHelper.IsItemInConfiguredLibraries(e.Item, _libraryManager, config))
                {
                    Common.LogHelper.Debug(_logger, $"Item outside configured libraries, skipping: {e.Item.Name}");
                    return;
                }

                if (!config.IncludeExtraVideos && MediaInfoHelper.IsExtraVideoItem(e.Item))
                {
                    Common.LogHelper.Debug(_logger, $"Extra video processing is disabled, skipping: {e.Item.Name}");
                    return;
                }

                var dedupKey = StrmFileProcessor.GetDedupKey(e.Item);
                if (!TryReserveSlot(dedupKey, out bool rejectedByCapacity))
                {
                    if (rejectedByCapacity)
                    {
                        Common.LogHelper.Warn(
                            _logger,
                            $"Event queue reached capacity ({MaxQueuedItems}), skipping {e.Item.Name}. It will be processed by scheduled task.");
                    }

                    return;
                }

                _localQueue.Enqueue(e.Item);
                EnsureLocalWorkersRunning();
            }
            catch (Exception ex)
            {
                // 事件处理器绝不能把异常抛回 Emby 的事件管线
                Common.LogHelper.Error(_logger, $"Error handling item added event: {ex.Message}");
            }
        }

        private bool TryReserveSlot(string dedupKey, out bool rejectedByCapacity)
        {
            rejectedByCapacity = false;
            if (!_queuedKeys.TryAdd(dedupKey, 0))
            {
                return false;
            }

            if (Interlocked.Increment(ref _queuedCount) > MaxQueuedItems)
            {
                ReleaseSlot(dedupKey);
                rejectedByCapacity = true;
                return false;
            }

            return true;
        }

        private void ReleaseSlot(string dedupKey)
        {
            if (_queuedKeys.TryRemove(dedupKey, out _))
            {
                Interlocked.Decrement(ref _queuedCount);
            }
        }

        private void EnsureLocalWorkersRunning()
        {
            if (_cancellationToken.IsCancellationRequested)
            {
                CancelRemainingQueuesOnShutdown();
                return;
            }

            if (_localQueue.IsEmpty)
            {
                return;
            }

            int targetWorkers = Math.Clamp(
                Math.Max(MinLocalWorkers, ProcessingConcurrencyLimiter.MaxConcurrency),
                MinLocalWorkers,
                MaxLocalWorkers);

            while (!_localQueue.IsEmpty)
            {
                int current = Volatile.Read(ref _activeLocalWorkers);
                if (current >= targetWorkers)
                {
                    break;
                }

                if (Interlocked.CompareExchange(ref _activeLocalWorkers, current + 1, current) == current)
                {
                    _ = Task.Run(DrainLocalQueueAsync);
                }
            }
        }

        private void EnsureProbeWorkersRunning()
        {
            if (_cancellationToken.IsCancellationRequested)
            {
                CancelRemainingQueuesOnShutdown();
                return;
            }

            if (_probeQueue.IsEmpty)
            {
                return;
            }

            int targetWorkers = Math.Max(1, ProcessingConcurrencyLimiter.MaxConcurrency);
            while (!_probeQueue.IsEmpty)
            {
                int current = Volatile.Read(ref _activeProbeWorkers);
                if (current >= targetWorkers)
                {
                    break;
                }

                if (Interlocked.CompareExchange(ref _activeProbeWorkers, current + 1, current) == current)
                {
                    _ = Task.Run(DrainProbeQueueAsync);
                }
            }
        }

        /// <summary>
        /// 第一阶段 Worker（本地快车道）：只执行毫秒级的完整性检查、本地 JSON 恢复与数据库补备份，
        /// 绝不等待远程探测或延迟；确需远程探测的条目移交到 _probeQueue 由慢车道独立处理。
        /// </summary>
        private async Task DrainLocalQueueAsync()
        {
            try
            {
                while (!_cancellationToken.IsCancellationRequested && _localQueue.TryDequeue(out var item))
                {
                    await ProcessLocalStageForItemAsync(item).ConfigureAwait(false);
                }
            }
            catch (Exception ex)
            {
                Common.LogHelper.Error(_logger, $"Unhandled error in local event queue worker: {ex.Message}");
            }
            finally
            {
                Interlocked.Decrement(ref _activeLocalWorkers);

                if (_cancellationToken.IsCancellationRequested)
                {
                    CancelRemainingQueuesOnShutdown();
                }
                else if (!_localQueue.IsEmpty)
                {
                    EnsureLocalWorkersRunning();
                }
            }
        }

        private async Task ProcessLocalStageForItemAsync(BaseItem item)
        {
            var dedupKey = StrmFileProcessor.GetDedupKey(item);
            bool handedOffToProbeQueue = false;
            StrmFileProcessor.InFlightLease? lease = null;

            try
            {
                _cancellationToken.ThrowIfCancellationRequested();

                var config = Plugin.GetSafeConfiguration();
                if (!config.EnableAutoExtract)
                {
                    return;
                }

                if (!MediaInfoHelper.IsItemInConfiguredLibraries(item, _libraryManager, config))
                {
                    return;
                }

                if (!config.IncludeExtraVideos && MediaInfoHelper.IsExtraVideoItem(item))
                {
                    return;
                }

                bool isUnlockedMusicNeedingTagProbe =
                    config.EnableMusicMetadata &&
                    AudioMetadataHelper.IsMusicLibraryAudio(item, _libraryManager) &&
                    !item.IsLocked &&
                    !_mediaInfoManager.HasProbedAudioTagsInJson(item);

                if (MediaInfoHelper.HasCompleteMediaInfo(item, _mediaInfoManager, _libraryManager) &&
                    MediaInfoHelper.HasJsonFile(item, _mediaInfoManager) &&
                    !isUnlockedMusicNeedingTagProbe)
                {
                    Common.LogHelper.Debug(_logger, $"{item.Name} already has complete media info, skipping");
                    return;
                }

                Common.LogHelper.Info(_logger, $"New strm file detected: {item.Name}");

                // 若其他入口（如计划任务）已在处理该路径，不占用本地快车道等待远程探测，直接交由慢车道等待/接力
                if (!_strmFileProcessor.TryAcquireInFlight(dedupKey, out lease, out _))
                {
                    _probeQueue.Enqueue(new ProbeWorkItem(item, dedupKey, lease: null, canFallbackToLegacyJson: false));
                    handedOffToProbeQueue = true;
                    EnsureProbeWorkersRunning();
                    return;
                }

                var (localResult, canFallback) = await _strmFileProcessor
                    .ExecuteLocalStageAsync(item, _cancellationToken)
                    .ConfigureAwait(false);
                if (localResult.HasValue)
                {
                    lease!.Complete(localResult.Value);
                    LogProcessResult(item.Name, localResult.Value);
                    return;
                }

                // 本地无法完成，将已持有的在途租约无缝移交至远程探测队列，本地快车道立即返回处理后续条目
                var transferredLease = lease;
                lease = null;
                _probeQueue.Enqueue(new ProbeWorkItem(item, dedupKey, transferredLease, canFallback));
                handedOffToProbeQueue = true;
                EnsureProbeWorkersRunning();
            }
            catch (OperationCanceledException)
            {
                lease?.Cancel(_cancellationToken);
                Common.LogHelper.Debug(_logger, $"Local stage cancelled for {item.Name}");
            }
            catch (Exception ex)
            {
                lease?.Complete(ProcessResult.Failed);
                Common.LogHelper.Error(_logger, $"Error in local stage for item {item.Name}: {ex.Message}");
            }
            finally
            {
                if (!handedOffToProbeQueue)
                {
                    lease?.Dispose();
                    ReleaseSlot(dedupKey);
                }
            }
        }

        /// <summary>
        /// 第二阶段 Worker（远程探测慢车道）：按受限并发执行延迟与远程 ffprobe 探测
        /// </summary>
        private async Task DrainProbeQueueAsync()
        {
            try
            {
                while (!_cancellationToken.IsCancellationRequested && _probeQueue.TryDequeue(out var work))
                {
                    await ProcessProbeWorkItemAsync(work).ConfigureAwait(false);
                }
            }
            catch (Exception ex)
            {
                Common.LogHelper.Error(_logger, $"Unhandled error in probe event queue worker: {ex.Message}");
            }
            finally
            {
                Interlocked.Decrement(ref _activeProbeWorkers);

                if (_cancellationToken.IsCancellationRequested)
                {
                    CancelRemainingQueuesOnShutdown();
                }
                else if (!_probeQueue.IsEmpty)
                {
                    EnsureProbeWorkersRunning();
                }
            }
        }

        private async Task ProcessProbeWorkItemAsync(ProbeWorkItem work)
        {
            try
            {
                _cancellationToken.ThrowIfCancellationRequested();

                var config = Plugin.GetSafeConfiguration();
                if (!config.EnableAutoExtract)
                {
                    work.Lease?.Cancel();
                    return;
                }

                if (!MediaInfoHelper.IsItemInConfiguredLibraries(work.Item, _libraryManager, config) ||
                    (!config.IncludeExtraVideos && MediaInfoHelper.IsExtraVideoItem(work.Item)))
                {
                    work.Lease?.Cancel();
                    Common.LogHelper.Debug(
                        _logger,
                        $"Skipped queued remote probe due to updated scope configuration: {work.Item.Name}");
                    return;
                }

                ProcessResult result;
                if (work.Lease != null)
                {
                    result = await _strmFileProcessor.ExecuteRemoteStageAsync(
                        work.Item,
                        config.ProcessingDelayMs,
                        work.CanFallbackToLegacyJson,
                        _cancellationToken).ConfigureAwait(false);
                    work.Lease.Complete(result);
                }
                else
                {
                    result = await _strmFileProcessor
                        .ProcessItemAsync(work.Item, config.ProcessingDelayMs, _cancellationToken)
                        .ConfigureAwait(false);
                }

                LogProcessResult(work.Item.Name, result);
            }
            catch (OperationCanceledException)
            {
                work.Lease?.Cancel(_cancellationToken);
                Common.LogHelper.Debug(_logger, $"Remote probing cancelled for {work.Item.Name}");
            }
            catch (Exception ex)
            {
                work.Lease?.Complete(ProcessResult.Failed);
                Common.LogHelper.Error(_logger, $"Error probing item {work.Item.Name}: {ex.Message}");
            }
            finally
            {
                work.Lease?.Dispose();
                ReleaseSlot(work.DedupKey);
            }
        }

        private void CancelRemainingQueuesOnShutdown()
        {
            while (_localQueue.TryDequeue(out var item))
            {
                ReleaseSlot(StrmFileProcessor.GetDedupKey(item));
            }

            while (_probeQueue.TryDequeue(out var work))
            {
                work.Lease?.Cancel(_cancellationToken);
                ReleaseSlot(work.DedupKey);
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
