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
    public class ExtractTask : IScheduledTask
    {
        private readonly ILogger _logger;
        private readonly ILibraryManager _libraryManager;
        private readonly IItemRepository _itemRepository;
        private readonly IJsonSerializer _jsonSerializer;
        private readonly IMediaProbeManager _mediaProbeManager;

        public ExtractTask(ILibraryManager libraryManager, 
            ILogger logger, 
            IItemRepository itemRepository,
            IJsonSerializer jsonSerializer,
            IMediaProbeManager mediaProbeManager)
        {
            _libraryManager = libraryManager;
            _logger = logger;
            _itemRepository = itemRepository;
            _jsonSerializer = jsonSerializer;
            _mediaProbeManager = mediaProbeManager;
        }

        public async Task Execute(CancellationToken cancellationToken, IProgress<double> progress)
        {
            Common.LogHelper.Info(_logger, "Starting strm file scan...");

            var mediaInfoManager = new MediaInfoManager(_logger, _libraryManager, _itemRepository, _jsonSerializer);
            var processor = new StrmFileProcessor(_logger, _libraryManager, _itemRepository, _mediaProbeManager, _jsonSerializer, mediaInfoManager);

            var strmItems = MediaInfoHelper.GetAllStrmFiles(_libraryManager)
                .Where(i => !MediaInfoHelper.HasCompleteMediaInfo(i))
                .ToList();
            Common.LogHelper.Info(_logger, $"{strmItems.Count} strm files need media probing");

            if (strmItems.Count == 0)
            {
                progress.Report(100);
                Common.LogHelper.Info(_logger, "Nothing to process, task complete.");
                return;
            }

            int total = strmItems.Count;
            int processed = 0;
            var config = Plugin.GetSafeConfiguration();
            var maxConcurrency = config.MaxConcurrency;
            var delayMs = config.ProcessingDelayMs;
            
            using var semaphore = new SemaphoreSlim(maxConcurrency, maxConcurrency);

            var tasks = strmItems.Select(async item =>
            {
                await semaphore.WaitAsync(cancellationToken).ConfigureAwait(false);
                try
                {
                    if (cancellationToken.IsCancellationRequested)
                    {
                        return;
                    }

                    await processor.ProcessStrmFileAsync(item, cancellationToken).ConfigureAwait(false);

                    var count = Interlocked.Increment(ref processed);
                    var progressValue = (double)count / total * 100;
                    progress.Report(progressValue);

                    if (count < total)
                    {
                        await Task.Delay(delayMs, cancellationToken).ConfigureAwait(false);
                    }
                }
                finally
                {
                    semaphore.Release();
                }
            });

            await Task.WhenAll(tasks).ConfigureAwait(false);

            progress.Report(100);
            Common.LogHelper.Info(_logger, $"Task complete. Successfully processed {processed}/{total} strm files.");
        }

        public string Category => TaskLocalizer.GetCategory();
        public string Key => "StrmToolTask";
        public string Description => TaskLocalizer.GetExtractTaskDescription();
        public string Name => TaskLocalizer.GetExtractTaskName();

        public IEnumerable<TaskTriggerInfo> GetDefaultTriggers()
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
