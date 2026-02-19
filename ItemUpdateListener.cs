using System;
using System.Collections.Concurrent;
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
        private readonly IMediaStreamRepository _mediaStreamRepository;
        private readonly MediaInfoCache _mediaCache;
        private readonly ConcurrentDictionary<Guid, byte> _restoringItems;
        private PluginConfiguration _config;
        private volatile bool _isDisposed = false;

        public ItemUpdateListener(
            ILibraryManager libraryManager,
            IMediaStreamRepository mediaStreamRepository,
            ILogger logger,
            PluginConfiguration config)
        {
            _logger = logger;
            _libraryManager = libraryManager;
            _mediaStreamRepository = mediaStreamRepository;
            _mediaCache = new MediaInfoCache(logger);
            _restoringItems = new ConcurrentDictionary<Guid, byte>();
            _config = config;

            // 订阅Item更新事件
            _libraryManager.ItemUpdated += OnItemUpdated;
            _logger.LogInformation("StrmTool - Item update listener initialized");
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

        private void OnItemUpdated(object sender, ItemChangeEventArgs e)
        {
            if (_isDisposed)
                return;

            try
            {
                RefreshConfig();

                // 检查是否是strm文件
                if (!e.Item.Path?.EndsWith(".strm", StringComparison.OrdinalIgnoreCase) ?? true)
                {
                    return;
                }

                var item = e.Item;
                var fileName = System.IO.Path.GetFileNameWithoutExtension(item.Path);

                // 检查是否有缓存
                if (!_mediaCache.TryGetFullCache(item.Path, out var cacheData))
                {
                    return;
                }

                // 检查Size是否被重置（strm文件通常很小，小于1KB）
                bool sizeReset = item.Size < 1024 && cacheData.Size > 1024;
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

                // 检查是否正在恢复中，防止竞态条件
                if (_restoringItems.ContainsKey(item.Id))
                {
                    _logger.LogDebug("StrmTool - Item {Name} already being restored, skipping", fileName);
                    return;
                }

                // 需要在后台线程执行恢复操作
                _ = Task.Run(async () =>
                {
                    // 标记为正在恢复
                    _restoringItems.TryAdd(item.Id, 0);
                    try
                    {
                        await RestoreItemMetadataAsync(item, cacheData, CancellationToken.None).ConfigureAwait(false);
                    }
                    catch (Exception ex)
                    {
                        _logger.LogError(ex, "StrmTool - Error restoring metadata for {Name}", fileName);
                    }
                    finally
                    {
                        // 移除恢复标记
                        _restoringItems.TryRemove(item.Id, out _);
                    }
                });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "StrmTool - Error in OnItemUpdated handler");
            }
        }

        private async Task RestoreItemMetadataAsync(BaseItem item, MediaInfoCacheData cacheData, CancellationToken cancellationToken)
        {
            var fileName = System.IO.Path.GetFileNameWithoutExtension(item.Path);

            try
            {
                _logger.LogInformation("StrmTool - Restoring metadata for {Name} (Size: {OldSize} -> {NewSize})",
                    fileName, item.Size, cacheData.Size);

                // 恢复元数据
                item.Size = cacheData.Size;
                item.RunTimeTicks = cacheData.RunTimeTicks;
                item.Container = cacheData.Container;
                item.TotalBitrate = cacheData.TotalBitrate;
                item.Width = cacheData.Width;
                item.Height = cacheData.Height;

                // 恢复媒体流
                if (cacheData.MediaStreams != null && cacheData.MediaStreams.Count > 0)
                {
                    _mediaStreamRepository.SaveMediaStreams(item.Id, cacheData.MediaStreams, cancellationToken);
                }

                // 持久化修改
                await item.UpdateToRepositoryAsync(ItemUpdateType.MetadataImport, cancellationToken).ConfigureAwait(false);

                _logger.LogInformation("StrmTool - Successfully restored metadata for {Name}", fileName);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "StrmTool - Failed to restore metadata for {Name}", fileName);
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

            _logger.LogInformation("StrmTool - Item update listener disposed");
        }
    }
}
