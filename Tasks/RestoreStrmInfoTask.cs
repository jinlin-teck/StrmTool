using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Persistence;
using MediaBrowser.Model.Logging;
using MediaBrowser.Model.Tasks;
using StrmTool.Common;

namespace StrmTool.Tasks
{
    public class RestoreStrmInfoTask : IScheduledTask
    {
        private readonly ILogger _logger;
        private readonly ILibraryManager _libraryManager;
        private readonly IItemRepository _itemRepository;

        public RestoreStrmInfoTask(ILogger logger, ILibraryManager libraryManager, IItemRepository itemRepository)
        {
            _logger = logger;
            _libraryManager = libraryManager;
            _itemRepository = itemRepository;
        }

        public string Key => "StrmToolRestoreTask";
        public string Name => TaskLocalizer.GetRestoreTaskName();
        public string Description => TaskLocalizer.GetRestoreTaskDescription();
        public string Category => TaskLocalizer.GetCategory();

        public async Task Execute(CancellationToken cancellationToken, IProgress<double> progress)
        {
            var manager = new MediaInfoManager(_logger, _libraryManager, _itemRepository);
            await manager.RestoreAllAsync(progress, cancellationToken);
        }

        public IEnumerable<TaskTriggerInfo> GetDefaultTriggers()
        {
            // 默认不自动触发（用户可手动在 Emby 后台运行）
            return Array.Empty<TaskTriggerInfo>();
        }
    }
}
