using MediaBrowser.Common.Configuration;
using MediaBrowser.Common.Plugins;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.MediaEncoding;
using MediaBrowser.Controller.Persistence;
using MediaBrowser.Model.Drawing;
using MediaBrowser.Model.Logging;
using MediaBrowser.Model.Plugins;
using MediaBrowser.Model.Serialization;
using StrmTool.Handlers;
using StrmTool.Common;
using System;
using System.IO;

namespace StrmTool
{
    public class Plugin : BasePlugin<PluginConfiguration>, IHasThumbImage
    {
        public static Plugin Instance { get; private set; } = null!;
        public static string PluginName => "StrmTool";

        private ItemAddedEventHandler? _eventHandler;
        private ILibraryManager? _libraryManager;
        public static IJsonSerializer? JsonSerializer { get; private set; }

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
            RegisterEventHandlers(libraryManager, logger, itemRepository, jsonSerializer, mediaProbeManager);
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
                _eventHandler = new ItemAddedEventHandler(logger, libraryManager, itemRepository, jsonSerializer, mediaProbeManager);
                libraryManager.ItemAdded += _eventHandler.OnItemAdded;

                logger.Info("StrmTool - Item added event handler registered at plugin level");
            }
            catch (Exception ex)
            {
                logger.Error($"StrmTool - Error registering event handlers at plugin level: {ex.Message}");
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
        /// 析构函数，确保资源释放
        /// </summary>
        ~Plugin()
        {
            UnregisterEventHandlers();
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
