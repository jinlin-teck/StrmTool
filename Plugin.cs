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
        public static Plugin? Instance { get; private set; }
        public static string PluginName => "StrmTool";

        public static Plugin RequireInstance()
        {
            return Instance ?? throw new InvalidOperationException("Plugin is not initialized");
        }

        private ItemAddedEventHandler? _eventHandler;
        private ILibraryManager? _libraryManager;
        private CancellationTokenSource? _cancellationTokenSource;
        private bool _disposed;
        public static IJsonSerializer? JsonSerializer { get; private set; }
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
            Instance = this;
            JsonSerializer = jsonSerializer;
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
                _eventHandler = new ItemAddedEventHandler(logger, libraryManager, itemRepository, jsonSerializer, mediaProbeManager, _cancellationTokenSource);
                libraryManager.ItemAdded += _eventHandler.OnItemAdded;

                logger.Info("StrmTool - Item added event handler registered at plugin level");
            }
            catch (Exception ex)
            {
                logger.Error($"StrmTool - Error registering event handlers at plugin level: {ex.Message}");
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
                    logger.Error($"StrmTool - Unobserved task exception: {e.Exception?.Message}");
                    e.SetObserved();
                };
                TaskScheduler.UnobservedTaskException += _unobservedTaskExceptionHandler;
                logger.Info("StrmTool - Unobserved task exception handler registered");
            }
            catch (Exception ex)
            {
                logger.Error($"StrmTool - Error registering unobserved task exception handler: {ex.Message}");
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
    }
}
