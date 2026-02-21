using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Persistence;
using MediaBrowser.Model.Logging;
using MediaBrowser.Model.Tasks;
using MediaBrowser.Model.Serialization;
using StrmTool.Common;

namespace StrmTool.Tasks
{
    public class ExportStrmInfoTask : IScheduledTask
    {
        private readonly ILogger _logger;
        private readonly ILibraryManager _libraryManager;
        private readonly IItemRepository _itemRepository;
        private readonly IJsonSerializer _jsonSerializer;

        public ExportStrmInfoTask(ILogger logger, ILibraryManager libraryManager, IItemRepository itemRepository, IJsonSerializer jsonSerializer)
        {
            _logger = logger;
            _libraryManager = libraryManager;
            _itemRepository = itemRepository;
            _jsonSerializer = jsonSerializer;
        }

        public string Key => "StrmToolExportTask";
        public string Name => TaskLocalizer.GetExportTaskName();
        public string Description => TaskLocalizer.GetExportTaskDescription();
        public string Category => TaskLocalizer.GetCategory();

        public async Task Execute(CancellationToken cancellationToken, IProgress<double> progress)
        {
            var manager = new MediaInfoManager(_logger, _libraryManager, _itemRepository, _jsonSerializer);
            await manager.ExportAllAsync(progress, cancellationToken).ConfigureAwait(false);
        }

        public IEnumerable<TaskTriggerInfo> GetDefaultTriggers()
        {
            // 默认不自动触发（用户可手动在 Emby 后台运行）
            return Array.Empty<TaskTriggerInfo>();
        }
    }
}
