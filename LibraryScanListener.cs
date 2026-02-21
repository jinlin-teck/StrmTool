using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;

namespace StrmTool
{
    public class LibraryScanListener : IDisposable
    {
        private readonly ILogger _logger;
        private readonly ILibraryManager _libraryManager;
        private PluginConfiguration _config;
        private volatile bool _isDisposed = false;

        // 使用事件解耦，而不是直接依赖 ExtractTask
        public event EventHandler<BaseItem> StrmFileDetected;

        public LibraryScanListener(
            ILibraryManager libraryManager,
            ILogger logger,
            PluginConfiguration config)
        {
            _logger = logger;
            _libraryManager = libraryManager;
            _config = config;

            // 订阅事件
            _libraryManager.ItemAdded += OnItemAdded;
            _logger.LogInformation("Library scan listener initialized");
        }

        /// <summary>
        /// 刷新配置引用，确保获取最新的配置值
        /// </summary>
        public void RefreshConfig()
        {
            if (Plugin.Instance != null)
            {
                _config = Plugin.Instance.Configuration;
            }
        }

        private void OnItemAdded(object sender, ItemChangeEventArgs e)
        {
            if (_isDisposed)
                return;

            try
            {
                // 刷新配置以确保获取最新的设置
                RefreshConfig();

                if (!_config.EnableAutoExtract)
                    return;

                var item = e.Item;

                // 检查是否是 strm 文件
                if (item.Path?.EndsWith(".strm", StringComparison.OrdinalIgnoreCase) ?? false)
                {
                    // 再次检查是否已释放，防止竞态条件
                    if (_isDisposed)
                        return;

                    // 使用实际文件名而不是 item.Name，因为 item.Name 可能还没有完全解析
                    var fileName = System.IO.Path.GetFileNameWithoutExtension(item.Path);
                    _logger.LogInformation("New strm file detected: {Name} ({Path})", fileName, item.Path);

                    // 新入库文件通过事件通知 ExtractTask 进行处理
                    StrmFileDetected?.Invoke(this, item);
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error in OnItemAdded handler");
            }
        }

        public void Dispose()
        {
            if (_isDisposed)
                return;

            _isDisposed = true;

            try
            {
                _libraryManager.ItemAdded -= OnItemAdded;
            }
            catch (ObjectDisposedException)
            {
            }

            _logger.LogInformation("Library scan listener disposed");
        }
    }
}