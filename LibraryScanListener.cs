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
    private readonly ExtractTask _extractTask;
    private readonly PluginConfiguration _config;
    private bool _isDisposed = false;

    public LibraryScanListener(
      ILibraryManager libraryManager,
      ILogger<ExtractTask> logger,
      ExtractTask extractTask,
      PluginConfiguration config)
    {
      _logger = logger;
      _libraryManager = libraryManager;
      _extractTask = extractTask;
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
          _logger.LogInformation("StrmTool - New strm file detected: {Name} ({Path})", item.Name, item.Path);

          // 异步执行媒体信息提取，避免阻塞库扫描流程
          _ = Task.Run(async () =>
          {
            using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(5)); // 5分钟超时
            try
            {
              // 延迟确保 Jellyfin 完成初始化
              await Task.Delay(_config.RefreshDelayMs, cts.Token);
              await _extractTask.ExtractSingleItemAsync(item, cts.Token);
            }
            catch (OperationCanceledException)
            {
              _logger.LogWarning("StrmTool - Extraction cancelled for {Name}", item.Name);
            }
            catch (Exception ex)
            {
              _logger.LogError(ex, "StrmTool - Error extracting single item: {Name}", item.Name);
            }
          });
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
