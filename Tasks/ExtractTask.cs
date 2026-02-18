using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Persistence;
using MediaBrowser.Model.IO;
using MediaBrowser.Model.Logging;
using MediaBrowser.Model.Tasks;
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
        private readonly IFileSystem _fileSystem;
        private readonly IItemRepository _itemRepository;

        public ExtractTask(ILibraryManager libraryManager, 
            ILogger logger, 
            IFileSystem fileSystem,
            IItemRepository itemRepository)
        {
            _libraryManager = libraryManager;
            _logger = logger;
            _fileSystem = fileSystem;
            _itemRepository = itemRepository;
        }

        public async Task Execute(CancellationToken cancellationToken, IProgress<double> progress)
        {
            _logger.Info("StrmTool - Starting strm file scan...");

            // 使用MediaInfoHelper的统一方法获取需要恢复的STRM文件
            var strmItems = MediaInfoHelper.GetStrmFilesNeedingRestore(_libraryManager);
            _logger.Info($"StrmTool - {strmItems.Count} strm files need metadata refresh");

            if (strmItems.Count == 0)
            {
                progress.Report(100);
                _logger.Info("StrmTool - Nothing to process, task complete.");
                return;
            }

            // 创建处理器实例
            var processor = new StrmFileProcessor(_logger, _libraryManager, _fileSystem, _itemRepository);

            int processed = 0;
            int total = strmItems.Count;

            // 顺序处理，避免触发远程服务器风控
            foreach (var item in strmItems)
            {
                if (cancellationToken.IsCancellationRequested)
                {
                    _logger.Info("StrmTool - Task was cancelled");
                    break;
                }

                // 使用统一的处理器处理文件
                await processor.ProcessStrmFileAsync(item, cancellationToken);

                processed++;
                progress.Report((double)processed / total * 100);

                // 添加延迟，避免对远程服务器造成压力
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
