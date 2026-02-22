using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Common.Plugins;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.MediaEncoding;
using MediaBrowser.Controller.Persistence;
using MediaBrowser.Model.Drawing;
using MediaBrowser.Model.Logging;
using MediaBrowser.Model.Plugins;
using MediaBrowser.Model.Serialization;
using StrmTool.Common;
using StrmTool.Handlers;

namespace StrmTool
{
    public class Plugin : BasePlugin<PluginConfiguration>, IHasThumbImage, IDisposable
    {
        private static readonly object _lock = new object();
        public static Plugin? Instance { get; private set; }
        public static string PluginName => "StrmTool";

        public static Plugin RequireInstance()
        {
            lock (_lock)
            {
                return Instance ?? throw new InvalidOperationException("Plugin is not initialized");
            }
        }

        public static PluginConfiguration GetSafeConfiguration()
        {
            var config = Instance?.Configuration;
            if (config == null || !config.IsValid)
            {
                return new PluginConfiguration();
            }
            return config;
        }

        private ItemAddedEventHandler? _eventHandler;
        private ILibraryManager? _libraryManager;
        private CancellationTokenSource? _cancellationTokenSource;
        private bool _disposed;
        private static IJsonSerializer? _jsonSerializer;
        public static IJsonSerializer? JsonSerializer
        {
            get
            {
                lock (_lock)
                {
                    return _jsonSerializer;
                }
            }
            private set
            {
                lock (_lock)
                {
                    _jsonSerializer = value;
                }
            }
        }
        private EventHandler<UnobservedTaskExceptionEventArgs>? _unobservedTaskExceptionHandler;

        public Plugin(
            IApplicationPaths applicationPaths,
            IXmlSerializer xmlSerializer,
            ILibraryManager libraryManager,
            ILogger logger,
            IItemRepository itemRepository,
            IJsonSerializer jsonSerializer,
            IMediaProbeManager mediaProbeManager
        ) : base(applicationPaths, xmlSerializer)
        {
            lock (_lock)
            {
                Instance = this;
                JsonSerializer = jsonSerializer;
            }
            _libraryManager = libraryManager;
            _cancellationTokenSource = new CancellationTokenSource();
            
            RegisterEventHandlers(libraryManager, logger, itemRepository, jsonSerializer, mediaProbeManager);
            RegisterUnobservedTaskExceptionHandler(logger);
        }

        private void RegisterEventHandlers(
            ILibraryManager libraryManager,
            ILogger logger,
            IItemRepository itemRepository,
            IJsonSerializer jsonSerializer,
            IMediaProbeManager mediaProbeManager)
        {
            try
            {
                var mediaInfoManager = new MediaInfoManager(logger, libraryManager, itemRepository, jsonSerializer);
                var strmFileProcessor = new StrmFileProcessor(
                    logger, libraryManager, itemRepository, mediaProbeManager, jsonSerializer, mediaInfoManager);
                _eventHandler = new ItemAddedEventHandler(
                    logger, libraryManager, itemRepository, jsonSerializer, mediaProbeManager, 
                    _cancellationTokenSource, mediaInfoManager, strmFileProcessor);
                libraryManager.ItemAdded += _eventHandler.OnItemAdded;

                Common.LogHelper.Info(logger, "Item added event handler registered at plugin level");
            }
            catch (Exception ex)
            {
                Common.LogHelper.Error(logger, $"Error registering event handlers at plugin level: {ex.Message}");
            }
        }

        /// <summary>
        /// 注册未观察任务异常处理器，防止未捕获的异常导致应用崩溃
        /// </summary>
        private void RegisterUnobservedTaskExceptionHandler(ILogger logger)
        {
            try
            {
                _unobservedTaskExceptionHandler = (sender, e) =>
                {
                    Common.LogHelper.Error(logger, $"Unobserved task exception: {e.Exception?.Message}");
                    e.SetObserved();
                };
                TaskScheduler.UnobservedTaskException += _unobservedTaskExceptionHandler;
                Common.LogHelper.Info(logger, "Unobserved task exception handler registered");
            }
            catch (Exception ex)
            {
                Common.LogHelper.Error(logger, $"Error registering unobserved task exception handler: {ex.Message}");
            }
        }



        /// <summary>
        /// 清理事件处理器，防止内存泄漏
        /// </summary>
        public void UnregisterEventHandlers()
        {
            try
            {
                if (_libraryManager != null && _eventHandler != null)
                {
                    _libraryManager.ItemAdded -= _eventHandler.OnItemAdded;
                }

                if (_cancellationTokenSource != null && !_cancellationTokenSource.IsCancellationRequested)
                {
                    _cancellationTokenSource.Cancel();
                }

                UnregisterUnobservedTaskExceptionHandler();
            }
            catch (Exception)
            {
                // 忽略卸载时的错误
            }
            finally
            {
                _eventHandler?.Dispose();
                _eventHandler = null;
                _libraryManager = null;
            }
        }

        /// <summary>
        /// 注销未观察任务异常处理器
        /// </summary>
        private void UnregisterUnobservedTaskExceptionHandler()
        {
            try
            {
                if (_unobservedTaskExceptionHandler != null)
                {
                    TaskScheduler.UnobservedTaskException -= _unobservedTaskExceptionHandler;
                }
            }
            catch (Exception)
            {
                // 忽略卸载时的错误
            }
        }

        /// <summary>
        /// 释放资源
        /// </summary>
        public void Dispose()
        {
            Dispose(true);
            GC.SuppressFinalize(this);
        }

        /// <summary>
        /// 释放资源
        /// </summary>
        protected virtual void Dispose(bool disposing)
        {
            if (_disposed)
                return;

            if (disposing)
            {
                UnregisterEventHandlers();
                _cancellationTokenSource?.Dispose();
            }

            _disposed = true;
        }

        public Stream GetThumbImage()
        {
            var type = GetType();
            return type.Assembly.GetManifestResourceStream($"{type.Namespace}.Images.thumb.png")
                   ?? Stream.Null;
        }

        public ImageFormat ThumbImageFormat => ImageFormat.Png;

        public override string Description =>
            "STRM file media info extraction and backup/restore functionality.";

        public override string Name => PluginName;

        public override Guid Id => CommonConfiguration.PluginId;
    }

    public class PluginConfiguration : BasePluginConfiguration
    {
        private bool _enableAutoExtract = true;
        private int _processingDelayMs = 2000;
        private int _maxConcurrency = 3;
        private bool _isValid = true;

        public bool EnableAutoExtract
        {
            get => _enableAutoExtract;
            set => _enableAutoExtract = value;
        }

        public int ProcessingDelayMs
        {
            get => _processingDelayMs;
            set => _processingDelayMs = Math.Clamp(value, 0, 20000);
        }

        public int MaxConcurrency
        {
            get => _maxConcurrency;
            set => _maxConcurrency = Math.Clamp(value, 1, 10);
        }

        public bool IsValid => _isValid && 
            _processingDelayMs >= 0 && _processingDelayMs <= 20000 &&
            _maxConcurrency >= 1 && _maxConcurrency <= 10;
    }
}
