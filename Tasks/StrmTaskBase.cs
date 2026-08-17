using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Persistence;
using MediaBrowser.Model.Logging;
using MediaBrowser.Model.Serialization;
using MediaBrowser.Model.Tasks;
using StrmTool.Common;

namespace StrmTool.Tasks
{
    /// <summary>
    /// StrmTool 计划任务基类，封装公共依赖、构造逻辑与默认触发器
    /// </summary>
    public abstract class StrmTaskBase : IScheduledTask
    {
        protected readonly ILogger Logger;
        protected readonly ILibraryManager LibraryManager;
        protected readonly IItemRepository ItemRepository;
        protected readonly IJsonSerializer JsonSerializer;

        protected StrmTaskBase(
            ILogger logger,
            ILibraryManager libraryManager,
            IItemRepository itemRepository,
            IJsonSerializer jsonSerializer)
        {
            Logger = logger ?? throw new ArgumentNullException(nameof(logger));
            LibraryManager = libraryManager ?? throw new ArgumentNullException(nameof(libraryManager));
            ItemRepository = itemRepository ?? throw new ArgumentNullException(nameof(itemRepository));
            JsonSerializer = jsonSerializer ?? throw new ArgumentNullException(nameof(jsonSerializer));
        }

        public string Category => TaskLocalizer.GetCategory();

        public abstract string Key { get; }
        public abstract string Name { get; }
        public abstract string Description { get; }

        public abstract Task Execute(CancellationToken cancellationToken, IProgress<double> progress);

        public virtual IEnumerable<TaskTriggerInfo> GetDefaultTriggers()
        {
            // 默认不自动触发（用户可手动在 Emby 后台运行）
            return Array.Empty<TaskTriggerInfo>();
        }

        /// <summary>
        /// 创建媒体信息管理器实例
        /// </summary>
        protected MediaInfoManager CreateMediaInfoManager()
        {
            return new MediaInfoManager(Logger, LibraryManager, ItemRepository, JsonSerializer);
        }
    }
}
