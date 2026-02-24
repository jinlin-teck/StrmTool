using System;
using System.Collections.Concurrent;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Persistence;

namespace StrmTool
{
    /// <summary>
    /// 监听Item更新事件，当strm文件的Size等元数据被重置时，从缓存恢复
    /// </summary>
    public class ItemUpdateListener : IDisposable
    {
        private readonly ILogger _logger;
        private readonly ILibraryManager _libraryManager;
        private readonly MediaInfoCache _mediaCache;
        private readonly ConcurrentDictionary<Guid, Task> _runningTasks;
        private PluginConfiguration _config;
        private volatile bool _isDisposed = false;

        public ItemUpdateListener(
            ILibraryManager libraryManager,
            ILogger logger,
            PluginConfiguration config)
        {
            _logger = logger;
            _libraryManager = libraryManager;
            _mediaCache = new MediaInfoCache(logger);
            _runningTasks = new ConcurrentDictionary<Guid, Task>();
            _config = config;

            // 订阅Item更新事件
            _libraryManager.ItemUpdated += OnItemUpdated;
            _logger.LogInformation("Item update listener initialized");
        }

        /// <summary>
        /// 刷新配置引用
        /// </summary>
        public void RefreshConfig()
        {
            if (Plugin.Instance != null)
            {
                _config = Plugin.Instance.Configuration;
            }
        }

        /// <summary>
        /// 清理已完成的任务，防止内存泄漏
        /// </summary>
        private void CleanupCompletedTasks()
        {
            // 仅当任务数量超过阈值时才清理，减少开销
            if (_runningTasks.Count <= 100)
            {
                return;
            }

            try
            {
                int removed = 0;
                foreach (var kvp in _runningTasks)
                {
                    if (kvp.Value.IsCompleted)
                    {
                        // 观察任务异常（防止未观察到的异常）
                        if (kvp.Value.Exception != null)
                        {
                            _logger.LogDebug(kvp.Value.Exception, "Observed completed task exception");
                        }

                        if (_runningTasks.TryRemove(kvp.Key, out _))
                        {
                            removed++;
                        }
                    }
                }

                if (removed > 0)
                {
                    _logger.LogDebug("Cleaned up {Count} completed tasks, remaining: {Remaining}",
                        removed, _runningTasks.Count);
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Error cleaning up completed tasks");
            }
        }

        private void OnItemUpdated(object sender, ItemChangeEventArgs e)
        {
            if (_isDisposed)
                return;

            try
            {
                RefreshConfig();

                // 检查是否是strm文件
                if (!e.Item.Path?.EndsWith(StrmMediaInfoService.StrmFileExtension, StringComparison.OrdinalIgnoreCase) ?? true)
                {
                    return;
                }

                var item = e.Item;
                var fileName = Path.GetFileNameWithoutExtension(item.Path);

                // 检查是否有缓存
                if (!_mediaCache.TryGetFullCache(item.Path, out var cacheData))
                {
                    return;
                }

                // 检查Size是否被重置（当前Size明显小于缓存的Size）
                bool sizeReset = cacheData.Size > 0 && item.Size < cacheData.Size / 10;
                bool needsUpdate = sizeReset;

                if (!needsUpdate)
                {
                    // 检查其他元数据是否丢失
                    if (cacheData.RunTimeTicks.HasValue && !item.RunTimeTicks.HasValue)
                    {
                        needsUpdate = true;
                    }
                }

                if (!needsUpdate)
                {
                    return;
                }

                // 原子性地检查并标记为正在恢复，防止竞态条件
                // 同时作为任务跟踪键，避免同一 item 创建多个任务
                var taskKey = item.Id;

                // 先检查是否已在处理中
                if (_runningTasks.ContainsKey(taskKey))
                {
                    _logger.LogDebug("Item {Name} already being restored, skipping", fileName);
                    return;
                }

                // 创建任务但不立即启动
                var task = new Task(async () =>
                {
                    try
                    {
                        var timeoutMinutes = _config?.MetadataRestoreTimeoutMinutes ?? 5;
                        using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(timeoutMinutes));

                        // 重新获取最新 item，避免使用可能已过时的对象
                        var latestItem = _libraryManager.GetItemById(item.Id);
                        if (latestItem == null)
                        {
                            _logger.LogWarning("Item {Name} not found in library, skipping restore", fileName);
                            return;
                        }

                        await RestoreItemMetadataAsync(latestItem, cacheData, cts.Token).ConfigureAwait(false);
                    }
                    catch (OperationCanceledException)
                    {
                        _logger.LogDebug("Restore operation cancelled for {Name}", fileName);
                    }
                    catch (Exception ex)
                    {
                        _logger.LogError(ex, "Error restoring metadata for {Name}", fileName);
                    }
                    finally
                    {
                        _runningTasks.TryRemove(taskKey, out _);
                    }
                });

                // 原子性添加，只有成功添加才启动任务
                if (!_runningTasks.TryAdd(taskKey, task))
                {
                    _logger.LogDebug("Item {Name} already being restored, skipping", fileName);
                    return;
                }

                // 成功添加后才启动任务
                task.Start(TaskScheduler.Default);

                // 定期清理已完成的任务，防止内存泄漏
                CleanupCompletedTasks();
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error in OnItemUpdated handler");
            }
        }

        private async Task RestoreItemMetadataAsync(BaseItem item, MediaInfoCacheData cacheData, CancellationToken cancellationToken)
        {
            var fileName = Path.GetFileNameWithoutExtension(item.Path);

            try
            {
                _logger.LogInformation("Restoring metadata for {Name} (Size: {OldSize} -> {NewSize})",
                    fileName, item.Size, cacheData.Size);

                // 恢复元数据（只保留前端显示和 Jellyfin 内部需要的字段）
                // 注意：Jellyfin 不会重置媒体流信息，因此不需要恢复 MediaStreams
                item.Size = cacheData.Size;
                item.RunTimeTicks = cacheData.RunTimeTicks;
                item.Container = cacheData.Container;

                // 持久化修改
                await item.UpdateToRepositoryAsync(ItemUpdateType.MetadataImport, cancellationToken).ConfigureAwait(false);

                _logger.LogInformation("Successfully restored metadata for {Name}", fileName);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to restore metadata for {Name}", fileName);
            }
        }

        public void Dispose()
        {
            if (_isDisposed)
                return;

            _isDisposed = true;

            try
            {
                _libraryManager.ItemUpdated -= OnItemUpdated;
            }
            catch (ObjectDisposedException)
            {
            }

            // 使用异步方式等待所有后台任务完成（最多等待30秒）
            // 避免同步阻塞导致的潜在死锁
            try
            {
                var allTasks = _runningTasks.Values;
                var waitTask = Task.WhenAll(allTasks);
                var timeoutTask = Task.Delay(TimeSpan.FromSeconds(30));

                // 使用 Task.WhenAny 实现超时等待
                var completedTask = Task.WhenAny(waitTask, timeoutTask).GetAwaiter().GetResult();

                if (completedTask == timeoutTask)
                {
                    _logger.LogWarning("Timeout waiting for {Count} background tasks to complete", _runningTasks.Count);
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Error waiting for background tasks to complete");
            }

            _logger.LogInformation("Item update listener disposed");
        }
    }
}
