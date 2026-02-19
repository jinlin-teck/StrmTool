using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.MediaEncoding;
using MediaBrowser.Controller.Persistence;
using MediaBrowser.Model.Logging;
using MediaBrowser.Model.Tasks;
using MediaBrowser.Model.Serialization;
using StrmTool.Common;
using System;
using System.Collections.Generic;
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
            _logger.Info("StrmTool - Starting strm file scan...");

            var mediaInfoManager = new MediaInfoManager(_logger, _libraryManager, _itemRepository, _jsonSerializer);
            var processor = new StrmFileProcessor(_logger, _libraryManager, _itemRepository, _mediaProbeManager, mediaInfoManager);

            var strmItems = MediaInfoHelper.GetStrmFilesNeedingRestore(_libraryManager);
            _logger.Info($"StrmTool - {strmItems.Count} strm files need media probing");

            if (strmItems.Count == 0)
            {
                progress.Report(100);
                _logger.Info("StrmTool - Nothing to process, task complete.");
                return;
            }

            int processed = 0;
            int total = strmItems.Count;

            foreach (var item in strmItems)
            {
                if (cancellationToken.IsCancellationRequested)
                {
                    _logger.Info("StrmTool - Task was cancelled");
                    break;
                }

                await processor.ProcessStrmFileAsync(item, cancellationToken);

                processed++;
                progress.Report((double)processed / total * 100);

                if (processed < total)
                {
                    await Task.Delay(CommonConfiguration.StandardProcessingDelayMs, cancellationToken);
                }
            }

            progress.Report(100);
            _logger.Info($"StrmTool - Task complete. Successfully processed {processed}/{total} strm files.");
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
