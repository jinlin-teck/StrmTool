using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.MediaEncoding;
using MediaBrowser.Controller.Persistence;
using MediaBrowser.Model.Logging;
using MediaBrowser.Model.Serialization;
using MediaBrowser.Model.Tasks;
using StrmTool.Common;

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

            // 包含三类条目：本地缺少 JSON 备份、缺少完整媒体信息、未锁定的音乐库曲目尚未探测过内嵌标签。
            // 优先将具备本地 JSON 的条目排在队列前端，使本地恢复先行快速完成而不被慢速远程探测阻塞。
            var strmItems = MediaInfoHelper.GetAllStrmFiles(LibraryManager)
                .Where(i => !MediaInfoHelper.HasJsonFile(i, mediaInfoManager) ||
                            !MediaInfoHelper.HasCompleteMediaInfo(i, mediaInfoManager, LibraryManager) ||
                            (AudioMetadataHelper.IsMusicLibraryAudio(i, LibraryManager) &&
                             !i.IsLocked &&
                             !mediaInfoManager.HasProbedAudioTagsInJson(i)))
                .OrderByDescending(i => MediaInfoHelper.HasJsonFile(i, mediaInfoManager))
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
            int succeeded = 0;
            int skipped = 0;
            int failed = 0;
            var progressLock = new object();

            // 用固定数量的 worker 消费队列，而不是为每个条目创建等待者：
            // 这样批量任务在全局探测信号量里最多只有 workerCount 个等待者，
            // 新入库事件不会被数万项队列排到队尾（避免饿死）。
            var queue = new ConcurrentQueue<BaseItem>(strmItems);
            int workerCount = Math.Max(1, Math.Min(total, ProcessingConcurrencyLimiter.MaxConcurrency));

            async Task ProcessOneAsync(BaseItem item)
            {
                cancellationToken.ThrowIfCancellationRequested();

                ProcessResult outcome;
                try
                {
                    var delayMs = Plugin.GetSafeConfiguration().ProcessingDelayMs;
                    outcome = await processor.ProcessItemAsync(item, delayMs, cancellationToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    Common.LogHelper.ErrorException(Logger, $"Unhandled error extracting media info for {item.Name}", ex);
                    outcome = ProcessResult.Failed;
                }

                // 一个项目完成最终处理后才报告一次进度；加锁保证计数与进度单调递增
                lock (progressLock)
                {
                    processed++;
                    if (outcome.IsSuccessOutcome())
                    {
                        succeeded++;
                    }
                    else if (outcome == ProcessResult.Skipped)
                    {
                        skipped++;
                    }
                    else
                    {
                        failed++;
                    }

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
            try
            {
                await Task.WhenAll(workers).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                Common.LogHelper.Info(
                    Logger,
                    $"Extract task cancelled. Processed {processed}/{total} (Succeeded: {succeeded}, Skipped: {skipped}, Failed: {failed}).");
                throw;
            }

            progress.Report(100);
            Common.LogHelper.Info(
                Logger,
                $"Task complete. Total: {total}, Succeeded: {succeeded}, Skipped: {skipped}, Failed: {failed}.");
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
