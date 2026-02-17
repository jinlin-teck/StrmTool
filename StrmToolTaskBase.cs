using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.MediaEncoding;
using MediaBrowser.Controller.Persistence;
using MediaBrowser.Model.Tasks;

namespace StrmTool
{
    /// <summary>
    /// StrmTool 任务基类，封装共享逻辑
    /// </summary>
    public abstract class StrmToolTaskBase : IScheduledTask, IDisposable
    {
        protected volatile bool _disposed = false;
        protected readonly ILogger _logger;
        protected readonly StrmMediaInfoService _mediaInfoService;
        protected readonly MediaInfoCache _mediaCache;
        protected PluginConfiguration _config;
        protected SemaphoreSlim _semaphore;

        protected StrmToolTaskBase(
            ILibraryManager libraryManager,
            IMediaEncoder mediaEncoder,
            IMediaStreamRepository mediaStreamRepository,
            ILogger logger)
        {
            _logger = logger;
            _mediaInfoService = new StrmMediaInfoService(libraryManager, mediaEncoder, mediaStreamRepository, logger);
            _mediaCache = new MediaInfoCache(logger);

            if (Plugin.Instance == null)
            {
                _logger.LogWarning("StrmTool - Plugin instance not found, using default configuration");
                _config = new PluginConfiguration();
            }
            else
            {
                _config = Plugin.Instance.Configuration;
            }

            _semaphore = new SemaphoreSlim(_config.MaxConcurrentExtract);
        }

        public abstract string Category { get; }
        public abstract string Key { get; }
        public abstract string Description { get; }
        public abstract string Name { get; }

        public IEnumerable<TaskTriggerInfo> GetDefaultTriggers()
        {
            return Array.Empty<TaskTriggerInfo>();
        }

        /// <summary>
        /// 刷新配置引用，确保获取最新的配置值
        /// </summary>
        protected void RefreshConfig()
        {
            if (Plugin.Instance != null)
            {
                _config = Plugin.Instance.Configuration;
            }
        }

        public abstract Task ExecuteAsync(IProgress<double> progress, CancellationToken cancellationToken);

        /// <summary>
        /// 处理 strm 文件集合的共享框架
        /// </summary>
        protected async Task<int> ProcessStrmItemsAsync(
            List<BaseItem> items,
            Func<BaseItem, CancellationToken, Task> processItemAsync,
            IProgress<double> progress,
            CancellationToken cancellationToken)
        {
            int processed = 0;
            int total = items.Count;

            var tasks = items.Select(async item =>
            {
                await _semaphore.WaitAsync(cancellationToken);
                try
                {
                    if (cancellationToken.IsCancellationRequested)
                    {
                        return;
                    }

                    await processItemAsync(item, cancellationToken).ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "StrmTool - Error processing {Name} ({Path})", item.Name, item.Path);
                }
                finally
                {
                    _semaphore.Release();
                    int current = Interlocked.Increment(ref processed);
                    double percent = (double)current / total * 100;
                    progress.Report(percent);
                }
            });

            await Task.WhenAll(tasks);
            progress.Report(100);
            return processed;
        }

        public void Dispose()
        {
            Dispose(true);
            GC.SuppressFinalize(this);
        }

        protected virtual void Dispose(bool disposing)
        {
            if (_disposed)
                return;

            _disposed = true;

            if (disposing)
            {
                _semaphore?.Dispose();
                _semaphore = null;
            }
        }
    }
}