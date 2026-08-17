using System;
using System.Threading;
using System.Threading.Tasks;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Persistence;
using MediaBrowser.Model.Logging;
using MediaBrowser.Model.Serialization;
using StrmTool.Common;

namespace StrmTool.Tasks
{
    public class ExportStrmInfoTask : StrmTaskBase
    {
        public ExportStrmInfoTask(ILogger logger, ILibraryManager libraryManager, IItemRepository itemRepository, IJsonSerializer jsonSerializer)
            : base(logger, libraryManager, itemRepository, jsonSerializer)
        {
        }

        public override string Key => "StrmToolExportTask";
        public override string Name => TaskLocalizer.GetExportTaskName();
        public override string Description => TaskLocalizer.GetExportTaskDescription();

        public override async Task Execute(CancellationToken cancellationToken, IProgress<double> progress)
        {
            var manager = CreateMediaInfoManager();
            await manager.ExportAllAsync(progress, cancellationToken).ConfigureAwait(false);
        }
    }
}
