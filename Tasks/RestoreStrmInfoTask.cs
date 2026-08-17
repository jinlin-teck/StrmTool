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
    public class RestoreStrmInfoTask : StrmTaskBase
    {
        public RestoreStrmInfoTask(ILogger logger, ILibraryManager libraryManager, IItemRepository itemRepository, IJsonSerializer jsonSerializer)
            : base(logger, libraryManager, itemRepository, jsonSerializer)
        {
        }

        public override string Key => "StrmToolRestoreTask";
        public override string Name => TaskLocalizer.GetRestoreTaskName();
        public override string Description => TaskLocalizer.GetRestoreTaskDescription();

        public override async Task Execute(CancellationToken cancellationToken, IProgress<double> progress)
        {
            var manager = CreateMediaInfoManager();
            await manager.RestoreAllAsync(progress, cancellationToken).ConfigureAwait(false);
        }
    }
}
