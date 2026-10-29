using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.MediaEncoding;
using MediaBrowser.Controller.Persistence;
using MediaBrowser.Model.Logging;
using MediaBrowser.Model.Tasks;
using MediaBrowser.Model.Serialization;
using StrmTool.Common;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace StrmTool.Tasks
{
    public class ExtractTask : StrmTaskBase
    {
        private readonly IMediaProbeManager _mediaProbeManager;

        public ExtractTask(ILibraryManager libraryManager,
            ILogger logger,
            IItemRepository itemRepository,
            IJsonSerializer jsonSerializer,
            IMediaProbeManager mediaProbeManager)
            : base(logger, libraryManager, itemRepository, jsonSerializer)
        {
            _mediaProbeManager = mediaProbeManager ?? throw new ArgumentNullException(nameof(mediaProbeManager));
        }

        public override async Task Execute(CancellationToken cancellationToken, IProgress<double> progress)
        {
            Common.LogHelper.Info(Logger, "Starting strm file scan...");

            var mediaInfoManager = CreateMediaInfoManager();
            var processor = CreateFileProcessor(_mediaProbeManager, mediaInfoManager);

            // 包含三类条目：缺少媒体信息、本地缺少 JSON 备份（含上次导出失败/备份被删除）、音乐库尚未探测过标签
            var strmItems = MediaInfoHelper.GetAllStrmFiles(LibraryManager)
                .Where(i => !MediaInfoHelper.HasJsonFile(i, mediaInfoManager) ||
                            !MediaInfoHelper.HasCompleteMediaInfo(i, mediaInfoManager) ||
                            (AudioMetadataHelper.IsMusicLibraryAudio(i, LibraryManager) && !mediaInfoManager.HasProbedAudioTagsInJson(i)))
                .ToList();
            Common.LogHelper.Info(Logger, $"{strmItems.Count} strm files need processing");

            if (strmItems.Count == 0)
            {
                progress.Report(100);
                Common.LogHelper.Info(Logger, "Nothing to process, task complete.");
                return;
            }

            int total = strmItems.Count;
            int processed = 0;
            var progressLock = new object();
            var config = Plugin.GetSafeConfiguration();
            var delayMs = config.ProcessingDelayMs;

            // 用固定数量的 worker 消费队列，而不是为每个条目创建等待者：
            // 这样批量任务在全局探测信号量里最多只有 workerCount 个等待者，
            // 新入库事件不会被数万项队列排到队尾（避免饿死）。
            var queue = new ConcurrentQueue<BaseItem>(strmItems);
            int workerCount = Math.Max(1, Math.Min(total, ProcessingConcurrencyLimiter.MaxConcurrency));

            async Task ProcessOneAsync(BaseItem item)
            {
                cancellationToken.ThrowIfCancellationRequested();

                // null 表示未尝试恢复（无 JSON）
                ProcessResult? restoreResult = null;

                // JSON 恢复是纯本地操作：不应用延迟
                if (MediaInfoHelper.HasJsonFile(item, mediaInfoManager))
                {
                    restoreResult = await processor.TryRestoreFromJsonAsync(item, cancellationToken).ConfigureAwait(false);
                }

                if (restoreResult == null || !restoreResult.Value.IsTerminalSuccess())
                {
                    // 数据库已完整时只在本地补备份，不延迟、不占远程探测槽
                    var ensured = await processor.TryEnsureJsonFromDatabaseAsync(item, cancellationToken).ConfigureAwait(false);
                    if (ensured == null)
                    {
                        // 只有真正需要远程探测时才进入延迟与全局探测槽
                        var limiter = await ProcessingConcurrencyLimiter.AcquireAsync(cancellationToken).ConfigureAwait(false);
                        try
                        {
                            if (delayMs > 0)
                            {
                                await Task.Delay(delayMs, cancellationToken).ConfigureAwait(false);
                            }

                            await processor.ProbeAndExportAsync(item, cancellationToken).ConfigureAwait(false);
                        }
                        finally
                        {
                            ProcessingConcurrencyLimiter.Release(limiter);
                        }
                    }
                }

                // 一个项目完成最终处理后才报告一次进度；加锁保证单调递增
                lock (progressLock)
                {
                    processed++;
                    progress.Report(processed * 100.0 / total);
                }
            }

            async Task WorkerAsync()
            {
                while (queue.TryDequeue(out var item))
                {
                    await ProcessOneAsync(item).ConfigureAwait(false);
                }
            }

            var workers = Enumerable.Range(0, workerCount).Select(_ => WorkerAsync()).ToArray();
            await Task.WhenAll(workers).ConfigureAwait(false);

            progress.Report(100);
            Common.LogHelper.Info(Logger, $"Task complete. Successfully processed {processed}/{total} strm files.");
        }

        public override string Key => "StrmToolTask";
        public override string Description => TaskLocalizer.GetExtractTaskDescription();
        public override string Name => TaskLocalizer.GetExtractTaskName();

        public override IEnumerable<TaskTriggerInfo> GetDefaultTriggers()
        {
            return new[]
            {
                new TaskTriggerInfo
                {
                    Type = TaskTriggerInfo.TriggerDaily,
                    TimeOfDayTicks = TimeSpan.FromHours(3).Ticks,
                    MaxRuntimeTicks = TimeSpan.FromHours(24).Ticks
                }
            };
        }
    }
}
