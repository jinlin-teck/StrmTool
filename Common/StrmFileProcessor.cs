using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Globalization;
using System.Threading;
using System.Threading.Tasks;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Audio;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.MediaEncoding;
using MediaBrowser.Controller.Persistence;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.Logging;

namespace StrmTool.Common
{
    /// <summary>
    /// STRM 文件处理器：统一编排“本地 JSON 恢复 → 本地 DB 补写备份 → 全局并发节流与远程探测（含失败保底）”三阶段流水线，
    /// 并维护按路径的在途任务协调（Singleflight）：并发调用方会等待并复用成功的在途结果，若前序任务失败或被其发起方取消则自动接力重试。
    /// </summary>
    public class StrmFileProcessor
    {
        internal sealed class InFlightLease : IDisposable
        {
            private readonly string _key;
            private readonly TaskCompletionSource<ProcessResult> _tcs =
                new TaskCompletionSource<ProcessResult>(TaskCreationOptions.RunContinuationsAsynchronously);
            private int _completed;

            public InFlightLease(string key)
            {
                _key = key;
            }

            public Task<ProcessResult> Task => _tcs.Task;

            public void Complete(ProcessResult result)
            {
                if (Interlocked.Exchange(ref _completed, 1) == 0)
                {
                    // 先从在途字典移除，再唤醒等待者，确保因失败醒来准备重试的等待者不会再次读到旧租约
                    InFlightLeases.TryRemove(_key, out _);
                    _tcs.TrySetResult(result);
                }
            }

            public void Cancel(CancellationToken cancellationToken = default)
            {
                if (Interlocked.Exchange(ref _completed, 1) == 0)
                {
                    InFlightLeases.TryRemove(_key, out _);
                    if (cancellationToken.CanBeCanceled && cancellationToken.IsCancellationRequested)
                    {
                        _tcs.TrySetCanceled(cancellationToken);
                    }
                    else
                    {
                        _tcs.TrySetCanceled();
                    }
                }
            }

            public void Dispose()
            {
                Complete(ProcessResult.Failed);
            }
        }

        private static readonly ConcurrentDictionary<string, InFlightLease> InFlightLeases =
            new ConcurrentDictionary<string, InFlightLease>(MediaInfoJsonStore.PathStringComparer);

        private readonly ILogger _logger;
        private readonly ILibraryManager _libraryManager;
        private readonly MediaInfoManager _mediaInfoManager;
        private readonly StrmMediaInfoService _mediaInfoService;

        public StrmFileProcessor(
            ILogger logger,
            ILibraryManager libraryManager,
            IItemRepository itemRepository,
            IMediaProbeManager mediaProbeManager,
            MediaInfoManager mediaInfoManager)
        {
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
            _libraryManager = libraryManager ?? throw new ArgumentNullException(nameof(libraryManager));
            if (itemRepository == null) throw new ArgumentNullException(nameof(itemRepository));
            if (mediaProbeManager == null) throw new ArgumentNullException(nameof(mediaProbeManager));

            _mediaInfoManager = mediaInfoManager ?? throw new ArgumentNullException(nameof(mediaInfoManager));
            _mediaInfoService = new StrmMediaInfoService(logger, libraryManager, mediaProbeManager, itemRepository);
        }

        internal static void ClearInFlightState()
        {
            foreach (var kvp in InFlightLeases)
            {
                kvp.Value.Cancel();
            }

            InFlightLeases.Clear();
        }

        internal static string GetDedupKey(BaseItem item)
        {
            return !string.IsNullOrEmpty(item.Path)
                ? item.Path
                : item.InternalId.ToString(CultureInfo.InvariantCulture);
        }

        internal bool TryAcquireInFlight(
            string dedupKey,
            out InFlightLease? lease,
            out Task<ProcessResult>? existingTask)
        {
            while (true)
            {
                if (InFlightLeases.TryGetValue(dedupKey, out var existing))
                {
                    lease = null;
                    existingTask = existing.Task;
                    return false;
                }

                var candidate = new InFlightLease(dedupKey);
                if (InFlightLeases.TryAdd(dedupKey, candidate))
                {
                    lease = candidate;
                    existingTask = null;
                    return true;
                }
            }
        }

        /// <summary>
        /// 执行单个 STRM 条目的完整处理流水线：
        /// 若同一路径已有在途处理，等待其结果并在成功时直接复用；若前序在途任务失败或被取消，则由当前调用方接力重试。
        /// </summary>
        public async Task<ProcessResult> ProcessItemAsync(
            BaseItem item,
            int delayMs,
            CancellationToken cancellationToken = default)
        {
            if (item == null)
            {
                return ProcessResult.Failed;
            }

            var dedupKey = GetDedupKey(item);
            bool retriedAfterFailure = false;

            while (true)
            {
                cancellationToken.ThrowIfCancellationRequested();

                if (TryAcquireInFlight(dedupKey, out var lease, out var existingTask))
                {
                    var activeLease = lease!;
                    using (activeLease)
                    {
                        try
                        {
                            var (localResult, canFallback) = await ExecuteLocalStageAsync(item, cancellationToken).ConfigureAwait(false);
                            if (localResult.HasValue)
                            {
                                activeLease.Complete(localResult.Value);
                                return localResult.Value;
                            }

                            var remoteResult = await ExecuteRemoteStageAsync(item, delayMs, canFallback, cancellationToken).ConfigureAwait(false);
                            activeLease.Complete(remoteResult);
                            return remoteResult;
                        }
                        catch (OperationCanceledException)
                        {
                            activeLease.Cancel(cancellationToken);
                            throw;
                        }
                        catch (Exception ex)
                        {
                            Common.LogHelper.Error(_logger, $"Error processing {item.Name} ({item.Path}): {ex.Message}");
                            activeLease.Complete(ProcessResult.Failed);
                            return ProcessResult.Failed;
                        }
                    }
                }

                if (existingTask == null)
                {
                    continue;
                }

                Common.LogHelper.Debug(_logger, $"{item.Name} is already in flight, awaiting shared result...");
                ProcessResult sharedResult;
                try
                {
                    sharedResult = await AwaitWithCallerCancellationAsync(existingTask, cancellationToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
                {
                    // 在途任务被其发起方取消，但当前调用方仍在运行：立即接力重试该文件
                    Common.LogHelper.Debug(_logger, $"In-flight task for {item.Name} was cancelled by its owner, retrying...");
                    continue;
                }

                if (sharedResult.IsSuccessOutcome() || sharedResult == ProcessResult.Skipped)
                {
                    return sharedResult;
                }

                if (!retriedAfterFailure)
                {
                    retriedAfterFailure = true;
                    Common.LogHelper.Debug(_logger, $"In-flight task for {item.Name} finished with {sharedResult}, retrying once...");
                    continue;
                }

                return sharedResult;
            }
        }

        /// <summary>
        /// 第一阶段（纯本地操作，零网络请求、不占远程探测槽、不应用延迟）：
        /// 尝试从本地 JSON 恢复或从已完整的数据库补写/升级 JSON。
        /// 返回 LocalResult != null 表示本地已完成终态处理；返回 null 表示必须进入第二阶段远程探测。
        /// </summary>
        internal async Task<(ProcessResult? LocalResult, bool CanFallbackToLegacyJson)> ExecuteLocalStageAsync(
            BaseItem item,
            CancellationToken cancellationToken)
        {
            bool canFallbackToLegacyJson = false;

            if (MediaInfoHelper.HasJsonFile(item, _mediaInfoManager))
            {
                bool isUnlockedMusicNeedingTagProbe =
                    AudioMetadataHelper.IsMusicLibraryAudio(item, _libraryManager) &&
                    !item.IsLocked &&
                    !_mediaInfoManager.HasProbedAudioTagsInJson(item);

                if (!MediaInfoHelper.HasCompleteMediaInfo(item, _mediaInfoManager, _libraryManager) ||
                    isUnlockedMusicNeedingTagProbe)
                {
                    var restoreResult = await TryRestoreFromJsonAsync(item, cancellationToken).ConfigureAwait(false);
                    if (restoreResult.IsTerminalSuccess())
                    {
                        return (restoreResult, false);
                    }

                    if (restoreResult == ProcessResult.NeedsAudioTagProbe)
                    {
                        canFallbackToLegacyJson = true;
                    }
                }
            }

            var ensured = await TryEnsureJsonFromDatabaseAsync(item, cancellationToken).ConfigureAwait(false);
            if (ensured.HasValue)
            {
                return (ensured.Value, false);
            }

            return (null, canFallbackToLegacyJson);
        }

        /// <summary>
        /// 第二阶段（受限远程探测）：占用全局探测槽、应用延迟并探测导出；
        /// 若旧版音乐 JSON 降级探测失败，自动回退用本地旧版 JSON 恢复基础流与目录兜底元数据。
        /// </summary>
        internal async Task<ProcessResult> ExecuteRemoteStageAsync(
            BaseItem item,
            int delayMs,
            bool canFallbackToLegacyJson,
            CancellationToken cancellationToken)
        {
            ProcessResult probeResult;
            var limiter = await ProcessingConcurrencyLimiter.AcquireAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                if (delayMs > 0)
                {
                    Common.LogHelper.Debug(_logger, $"Applying delay: {delayMs}ms before processing {item.Name}");
                    await Task.Delay(delayMs, cancellationToken).ConfigureAwait(false);
                }

                probeResult = await ProbeAndExportAsync(item, cancellationToken).ConfigureAwait(false);
            }
            finally
            {
                ProcessingConcurrencyLimiter.Release(limiter);
            }

            if (canFallbackToLegacyJson &&
                (probeResult == ProcessResult.ExtractionFailed || probeResult == ProcessResult.Failed) &&
                !MediaInfoHelper.HasCompleteMediaInfo(item.GetMediaStreams()))
            {
                var fallbackRestored = await _mediaInfoManager.RestoreItemAsync(item, cancellationToken).ConfigureAwait(false);
                if (fallbackRestored)
                {
                    Common.LogHelper.Info(
                        _logger,
                        $"{item.Name}: Remote probe failed, restored media streams and fallback metadata from legacy JSON");
                    return ProcessResult.RestoredFromJson;
                }
            }

            return probeResult;
        }

        private static async Task<ProcessResult> AwaitWithCallerCancellationAsync(
            Task<ProcessResult> sharedTask,
            CancellationToken callerToken)
        {
            if (!callerToken.CanBeCanceled || sharedTask.IsCompleted)
            {
                return await sharedTask.ConfigureAwait(false);
            }

            var cancelTcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            using (callerToken.Register(s => ((TaskCompletionSource<bool>)s!).TrySetResult(true), cancelTcs))
            {
                var completed = await Task.WhenAny(sharedTask, cancelTcs.Task).ConfigureAwait(false);
                if (completed != sharedTask)
                {
                    callerToken.ThrowIfCancellationRequested();
                }

                return await sharedTask.ConfigureAwait(false);
            }
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
                // 先纯本地合并同目录新增的外挂歌词（.lrc / .elrc），避免仅因缺少本地歌词而触发远程探测，
                // 同时保证即使后续远程源不可用，本地歌词也能先行入库。
                bool lyricsMerged = _mediaInfoManager.TryMergeLocalLyrics(item, cancellationToken);

                bool isMusicAudio = AudioMetadataHelper.IsMusicLibraryAudio(item, _libraryManager);
                bool hasProbedAudioTags = !isMusicAudio || _mediaInfoManager.HasProbedAudioTagsInJson(item);
                if (MediaInfoHelper.HasCompleteMediaInfo(item, _mediaInfoManager, _libraryManager))
                {
                    bool canUpgradeAudioTagsFromDb = !hasProbedAudioTags && !item.IsLocked;
                    if (canUpgradeAudioTagsFromDb || lyricsMerged)
                    {
                        bool markAudioProbed = !(item is Audio lockedAudio && lockedAudio.IsLocked);
                        var upgraded = await _mediaInfoManager.ExportItemAsync(
                            item, cancellationToken, audioTagsProbed: markAudioProbed).ConfigureAwait(false);
                        if (upgraded)
                        {
                            Common.LogHelper.Info(_logger, $"{item.Name}: Upgraded JSON with existing DB metadata and local lyrics");
                            return ProcessResult.UpgradedJsonFromDb;
                        }
                    }

                    Common.LogHelper.Info(_logger, $"{item.Name} already has complete media info, skipping...");
                    return ProcessResult.Skipped;
                }

                if (!hasProbedAudioTags && !item.IsLocked)
                {
                    Common.LogHelper.Debug(_logger, $"JSON lacks probed audio tags for {item.Name}, falling back to remote probing...");
                    return ProcessResult.NeedsAudioTagProbe;
                }

                Common.LogHelper.Debug(_logger, $"Attempting to restore {item.Name} from JSON...");

                var restored = await _mediaInfoManager.RestoreItemAsync(item, cancellationToken).ConfigureAwait(false);
                if (!restored)
                {
                    Common.LogHelper.Warn(_logger, $"JSON restore failed for {item.Name}");
                    return ProcessResult.RestoreFailed;
                }

                // 恢复后重新查询一次媒体流，用于确认结果
                var (hasVideo, hasAudio) = MediaInfoHelper.GetStreamSummary(item.GetMediaStreams());

                Common.LogHelper.Debug(_logger, $"{item.Name}: Restored from JSON. Video:{hasVideo}, Audio:{hasAudio}");
                return ProcessResult.RestoredFromJson;
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                Common.LogHelper.Error(_logger, $"Error restoring {item.Name} ({item.Path}): {ex.Message}");
                return ProcessResult.Failed;
            }
        }

        /// <summary>
        /// 纯本地尝试：合并本地歌词，并在数据库已具备完整媒体信息时补写/升级 JSON 备份。
        /// 返回 null 表示必须发起远程探测；返回其他值表示本地已完成（成功或失败）。
        /// </summary>
        public async Task<ProcessResult?> TryEnsureJsonFromDatabaseAsync(
            BaseItem item,
            CancellationToken cancellationToken = default)
        {
            using var monitor = new PerformanceMonitor(_logger, "Ensure", item.Name);

            try
            {
                bool lyricsMerged = _mediaInfoManager.TryMergeLocalLyrics(item, cancellationToken);
                var streams = item.GetMediaStreams() ?? new List<MediaStream>();
                bool isMusicAudio = AudioMetadataHelper.IsMusicLibraryAudio(item, _libraryManager);
                bool hasProbedAudioTags = !isMusicAudio || _mediaInfoManager.HasProbedAudioTagsInJson(item);

                if (!MediaInfoHelper.HasCompleteMediaInfo(item, streams, _mediaInfoManager, _libraryManager))
                {
                    return null;
                }

                // 数据库已有完整信息时仍需确保本地有 JSON 备份（例如 Emby 自行探测过、上次导出失败或备份被删除）
                bool jsonMissing = !MediaInfoHelper.HasJsonFile(item, _mediaInfoManager);
                bool canUpgradeAudioTagsFromDb = !hasProbedAudioTags && !item.IsLocked;
                if (jsonMissing || canUpgradeAudioTagsFromDb || lyricsMerged)
                {
                    bool markAudioProbed = !(item is Audio lockedAudio && lockedAudio.IsLocked);
                    var exported = await _mediaInfoManager.ExportItemAsync(
                        item, cancellationToken, audioTagsProbed: markAudioProbed).ConfigureAwait(false);
                    if (exported)
                    {
                        Common.LogHelper.Info(_logger, jsonMissing
                            ? $"{item.Name}: Backed up existing DB media info to JSON"
                            : $"{item.Name}: Upgraded JSON with existing DB metadata and local lyrics");
                        return ProcessResult.UpgradedJsonFromDb;
                    }

                    Common.LogHelper.Warn(_logger, $"{item.Name}: Local JSON export failed, will retry on next run");
                    return ProcessResult.ExtractionFailed;
                }

                Common.LogHelper.Info(_logger, $"{item.Name} already has complete media info, skipping...");
                return ProcessResult.Skipped;
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                Common.LogHelper.Error(_logger, $"Error ensuring JSON backup for {item.Name} ({item.Path}): {ex.Message}");
                return ProcessResult.Failed;
            }
        }

        /// <summary>
        /// 探测远程媒体信息并导出到 JSON 文件。
        /// 会访问远程媒体源，调用方负责并发限制与探测前延迟。
        /// </summary>
        public async Task<ProcessResult> ProbeAndExportAsync(
            BaseItem item,
            CancellationToken cancellationToken = default)
        {
            using var monitor = new PerformanceMonitor(_logger, "Processing", item.Name);

            try
            {
                Common.LogHelper.Debug(_logger, $"Probing media info for {item.Name}...");

                var beforeStreams = item.GetMediaStreams() ?? new List<MediaStream>();
                var streams = await _mediaInfoService.ProbeAndSaveMediaStreamsAsync(item, cancellationToken).ConfigureAwait(false);

                // 必须基于本次远程探测实际返回的 streams 判断成败，不能读取 DB 中已有的旧流，
                // 否则已有音频流但缺少标签的项在远程探测失败时会被误判成功并写入 AudioTagsProbed = true。
                var (hasVideo, hasAudio) = MediaInfoHelper.GetStreamSummary(streams);

                Common.LogHelper.Info(_logger, $"{item.Name}: Probed media info. Streams {beforeStreams.Count}→{streams.Count}. Video:{hasVideo}, Audio:{hasAudio}");

                // 本次探测必须返回至少一种视频流或音频流才算探测成功
                if (!hasVideo && !hasAudio)
                {
                    Common.LogHelper.Warn(_logger, $"{item.Name} may still lack full media info");
                    return ProcessResult.ExtractionFailed;
                }

                var exportSucceeded = await _mediaInfoManager.ExportItemAsync(
                    item, cancellationToken, audioTagsProbed: true).ConfigureAwait(false);
                if (!exportSucceeded)
                {
                    Common.LogHelper.Warn(_logger, $"{item.Name}: Media info extraction succeeded but JSON export failed");
                    return ProcessResult.ExtractionFailed;
                }

                Common.LogHelper.Debug(_logger, $"{item.Name}: Media info exported to JSON file");
                return ProcessResult.ExtractedAndExported;
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                Common.LogHelper.Error(_logger, $"Error processing {item.Name} ({item.Path}): {ex.Message}");
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
        /// 本地JSON为旧版（尚未记录探测过的音频标签），且DB缺少音频元数据，需远程探测补全
        /// </summary>
        NeedsAudioTagProbe,

        /// <summary>
        /// DB已具备完整流与音频元数据，已直接从DB导出升级旧版JSON（无需远程探测）
        /// </summary>
        UpgradedJsonFromDb,

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

    /// <summary>
    /// ProcessResult 的终态判定，集中维护「无需继续探测」的语义
    /// </summary>
    public static class ProcessResultExtensions
    {
        public static bool IsTerminalSuccess(this ProcessResult result)
        {
            return result == ProcessResult.RestoredFromJson ||
                   result == ProcessResult.UpgradedJsonFromDb ||
                   result == ProcessResult.Skipped;
        }

        public static bool IsSuccessOutcome(this ProcessResult result)
        {
            return result == ProcessResult.RestoredFromJson ||
                   result == ProcessResult.UpgradedJsonFromDb ||
                   result == ProcessResult.ExtractedAndExported;
        }
    }
}
