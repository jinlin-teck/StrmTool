using MediaBrowser.Model.Logging;
using System;
using System.Diagnostics;

namespace StrmTool.Common
{
    /// <summary>
    /// 性能监控类，用于记录操作耗时
    /// </summary>
    public class PerformanceMonitor : IDisposable
    {
        private readonly ILogger _logger;
        private readonly string _operationName;
        private readonly string _itemName;
        private readonly Stopwatch _stopwatch;

        public PerformanceMonitor(ILogger logger, string operationName, string itemName = "")
        {
            _logger = logger;
            _operationName = operationName;
            _itemName = itemName;
            _stopwatch = Stopwatch.StartNew();
        }

        /// <summary>
        /// 停止监控并记录耗时
        /// </summary>
        public void Stop()
        {
            _stopwatch.Stop();
            var elapsed = _stopwatch.ElapsedMilliseconds;
            var message = string.IsNullOrEmpty(_itemName)
                ? $"{_operationName} completed in {elapsed}ms"
                : $"{_operationName} for {_itemName} completed in {elapsed}ms";
            
            if (elapsed > 5000) // 超过5秒记为警告
            {
                LogHelper.Warn(_logger, message);
            }
            else
            {
                LogHelper.Debug(_logger, message);
            }
        }

        /// <summary>
        /// 获取已耗费的毫秒数
        /// </summary>
        public long ElapsedMilliseconds => _stopwatch.ElapsedMilliseconds;

        /// <summary>
        /// 实现 IDisposable，确保资源释放时停止监控
        /// </summary>
        public void Dispose()
        {
            if (_stopwatch.IsRunning)
            {
                Stop();
            }
        }
    }
}
