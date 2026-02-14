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
    private readonly ILogger<ExtractTask> _logger;
    private readonly ILibraryManager _libraryManager;
    private readonly PluginConfiguration _config;
    private bool _isDisposed = false;
    
    // 使用事件解耦，而不是直接依赖 ExtractTask
    public event EventHandler<BaseItem> StrmFileDetected;

    public LibraryScanListener(
      ILibraryManager libraryManager,
      ILogger<ExtractTask> logger,
      PluginConfiguration config)
    {
      _logger = logger;
      _libraryManager = libraryManager;
      _config = config;

      // 订阅事件
      _libraryManager.ItemAdded += OnItemAdded;
      _logger.LogInformation("StrmTool - Library scan listener initialized");
    }

    private void OnItemAdded(object sender, ItemChangeEventArgs e)
    {
      if (_isDisposed || !_config.EnableAutoExtract)
        return;

      try
      {
        var item = e.Item;

        // 检查是否是 strm 文件
        if (item.Path?.EndsWith(".strm", StringComparison.OrdinalIgnoreCase) ?? false)
        {
          // 使用实际文件名而不是 item.Name，因为 item.Name 可能还没有完全解析
          var fileName = System.IO.Path.GetFileNameWithoutExtension(item.Path);
          _logger.LogInformation("StrmTool - New strm file detected: {Name} ({Path})", fileName, item.Path);

          // 触发事件通知，而不是直接调用 ExtractTask 方法
          StrmFileDetected?.Invoke(this, item);
        }
      }
      catch (Exception ex)
      {
        _logger.LogError(ex, "StrmTool - Error in OnItemAdded handler");
      }
    }

    public void Dispose()
    {
      if (_isDisposed)
        return;

      if (_libraryManager != null)
      {
        _libraryManager.ItemAdded -= OnItemAdded;
      }

      _isDisposed = true;
      _logger.LogInformation("StrmTool - Library scan listener disposed");
    }
  }
}