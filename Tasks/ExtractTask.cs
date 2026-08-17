using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.MediaEncoding;
using MediaBrowser.Controller.Persistence;
using MediaBrowser.Model.Logging;
using MediaBrowser.Model.Tasks;
using MediaBrowser.Model.Serialization;
using StrmTool.Common;
using System;
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
            var processor = new StrmFileProcessor(Logger, LibraryManager, ItemRepository, _mediaProbeManager, JsonSerializer, mediaInfoManager);

            var strmItems = MediaInfoHelper.GetAllStrmFiles(LibraryManager)
                .Where(i => !MediaInfoHelper.HasCompleteMediaInfo(i))
                .ToList();
            Common.LogHelper.Info(Logger, $"{strmItems.Count} strm files need media probing");

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
            var maxConcurrency = config.MaxConcurrency;
            var delayMs = config.ProcessingDelayMs;

            using var semaphore = new SemaphoreSlim(maxConcurrency, maxConcurrency);

            var tasks = strmItems.Select(async item =>
            {
                // 单信号量控制所有项目：恢复与探测都受 MaxConcurrency 限制
                await semaphore.WaitAsync(cancellationToken).ConfigureAwait(false);
                try
                {
                    cancellationToken.ThrowIfCancellationRequested();

                    // null 表示未尝试恢复（无 JSON）
                    ProcessResult? restoreResult = null;

                    // JSON 恢复是纯本地操作：不应用延迟
                    if (MediaInfoHelper.HasJsonFile(item, mediaInfoManager))
                    {
                        restoreResult = await processor.TryRestoreFromJsonAsync(item, cancellationToken).ConfigureAwait(false);
                    }

                    // 无 JSON、JSON 损坏或恢复失败时，在同一个槽位内延迟后降级到远程探测
                    if (restoreResult != ProcessResult.RestoredFromJson && restoreResult != ProcessResult.Skipped)
                    {
                        if (delayMs > 0)
                        {
                            await Task.Delay(delayMs, cancellationToken).ConfigureAwait(false);
                        }

                        await processor.ExtractAndExportAsync(item, cancellationToken).ConfigureAwait(false);
                    }
                }
                finally
                {
                    semaphore.Release();
                }

                // 一个项目完成最终处理后才报告一次进度；加锁保证单调递增
                lock (progressLock)
                {
                    processed++;
                    progress.Report(processed * 100.0 / total);
                }
            });

            await Task.WhenAll(tasks).ConfigureAwait(false);

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
