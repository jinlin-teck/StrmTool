using System;
using System.Collections.Generic;
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
    public class Plugin : BasePlugin<PluginConfiguration>, IHasThumbImage, IHasWebPages, IDisposable
    {
        private const string PluginDisplayName = "StrmTool";
        private static readonly object _lock = new object();
        private static readonly PluginConfiguration DefaultFallbackConfiguration = new PluginConfiguration();
        public static Plugin? Instance { get; private set; }

        /// <summary>
        /// 供静态辅助类输出的插件日志器（插件未初始化时为 null）
        /// </summary>
        internal static ILogger? Logger => Instance?._logger;

        public static PluginConfiguration GetSafeConfiguration()
        {
            try
            {
                var config = Instance?.Configuration;
                return config ?? DefaultFallbackConfiguration;
            }
            catch (Exception ex)
            {
                // 插件初始化期间可能无法访问 Configuration，返回默认值（直接调用 ILogger.Debug 防止与 LogHelper 形成递归）
                var logger = Instance?._logger;
                if (logger != null)
                {
                    logger.Debug($"StrmTool - Failed to read plugin configuration, using defaults: {ex.Message}");
                }

                return DefaultFallbackConfiguration;
            }
        }

        private readonly ILogger? _logger;
        private ItemAddedEventHandler? _eventHandler;
        private ILibraryManager? _libraryManager;
        private readonly CancellationTokenSource _cancellationTokenSource;
        private bool _disposed;

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
            }

            _logger = logger;
            _libraryManager = libraryManager;
            _cancellationTokenSource = new CancellationTokenSource();

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
                var mediaInfoManager = new MediaInfoManager(logger, libraryManager, itemRepository, jsonSerializer);
                var strmFileProcessor = new StrmFileProcessor(
                    logger, libraryManager, itemRepository, mediaProbeManager, mediaInfoManager);
                _eventHandler = new ItemAddedEventHandler(
                    logger, libraryManager, mediaInfoManager, strmFileProcessor, _cancellationTokenSource.Token);
                libraryManager.ItemAdded += _eventHandler.OnItemAdded;
                libraryManager.ItemRemoved += OnLibraryItemRemoved;

                Common.LogHelper.Info(logger, "Item added event handler registered at plugin level");
                LogConfiguration(logger);
            }
            catch (Exception ex)
            {
                Common.LogHelper.Error(logger, $"Error registering event handlers at plugin level: {ex.Message}");
            }
        }

        private void OnLibraryItemRemoved(object sender, ItemChangeEventArgs e)
        {
            if (e?.Item != null && MediaInfoHelper.IsStrmFile(e.Item.Path))
            {
                LibraryScopeFilter.InvalidateScopedCountCache();
            }
        }

        public override void UpdateConfiguration(BasePluginConfiguration configuration)
        {
            base.UpdateConfiguration(configuration);
            LibraryScopeFilter.InvalidateScopedCountCache(force: true);
            BackupPathResolver.ClearCache();
        }

        internal (int QueuedCount, int LocalQueueCount, int ProbeQueueCount) GetEventQueueMetrics()
        {
            var handler = _eventHandler;
            return handler != null
                ? (handler.QueuedCount, handler.LocalQueueCount, handler.ProbeQueueCount)
                : (0, 0, 0);
        }

        /// <summary>
        /// 在后台线程延迟打印配置信息，确保配置文件已加载
        /// </summary>
        private void LogConfiguration(ILogger logger)
        {
            var token = _cancellationTokenSource.Token;
            Task.Run(async () =>
            {
                try
                {
                    await Task.Delay(5000, token).ConfigureAwait(false);
                    var config = GetSafeConfiguration();
                    Common.LogHelper.Info(
                        logger,
                        $"Configuration loaded: EnableAutoExtract={config.EnableAutoExtract}, ProcessingDelayMs={config.ProcessingDelayMs}, " +
                        $"ProcessingDelayJitterMs={config.ProcessingDelayJitterMs}, MaxConcurrency={config.MaxConcurrency}, " +
                        $"EnableAllLibraries={config.EnableAllLibraries}, EnabledLibraries={config.EnabledLibraryIds.Length}, " +
                        $"ExtractVideoChapters={config.ExtractVideoChapters}, IncludeExtraVideos={config.IncludeExtraVideos}, " +
                        $"EnableMusicMetadata={config.EnableMusicMetadata}, EnableExternalLyrics={config.EnableExternalLyrics}, " +
                        $"CustomBackupDirectory={(string.IsNullOrEmpty(config.CustomBackupDirectory) ? "<default>" : config.CustomBackupDirectory)}, " +
                        $"EnableDebugLogging={config.EnableDebugLogging}");
                }
                catch (OperationCanceledException)
                {
                    // 插件卸载时取消，无需记录
                }
                catch (Exception ex)
                {
                    Common.LogHelper.Error(logger, $"Failed to log configuration: {ex.Message}");
                }
            }, token);
        }

        /// <summary>
        /// 清理事件处理器，防止内存泄漏
        /// </summary>
        private void UnregisterEventHandlers()
        {
            try
            {
                if (_libraryManager != null)
                {
                    if (_eventHandler != null)
                    {
                        _libraryManager.ItemAdded -= _eventHandler.OnItemAdded;
                    }

                    _libraryManager.ItemRemoved -= OnLibraryItemRemoved;
                }

                if (!_cancellationTokenSource.IsCancellationRequested)
                {
                    _cancellationTokenSource.Cancel();
                }
            }
            catch (Exception ex)
            {
                if (_logger != null)
                {
                    Common.LogHelper.Error(_logger, $"Error unregistering event handlers: {ex.Message}");
                }
            }
            finally
            {
                _eventHandler = null;
                _libraryManager = null;
                MediaInfoManager.ClearCachedState();
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
                _cancellationTokenSource.Dispose();

                lock (_lock)
                {
                    if (ReferenceEquals(Instance, this))
                    {
                        Instance = null;
                    }
                }
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

        public override string Name => PluginDisplayName;

        public override Guid Id => CommonConfiguration.PluginId;

        public IEnumerable<PluginPageInfo> GetPages()
        {
            return new[]
            {
                new PluginPageInfo
                {
                    Name = "strmtoolv3",
                    EmbeddedResourcePath = $"{GetType().Namespace}.Configuration.configPage.html"
                },
                new PluginPageInfo
                {
                    Name = "strmtoolconfigv3js",
                    EmbeddedResourcePath = $"{GetType().Namespace}.Configuration.configPage.js"
                }
            };
        }
    }

    public class PluginConfiguration : BasePluginConfiguration
    {
        // 默认配置与旧版本行为保持完全向后兼容，用户可在插件设置界面按需调整
        private bool _enableAutoExtract = true;
        private int _processingDelayMs = CommonConfiguration.StandardProcessingDelayMs;
        private int _processingDelayJitterMs = CommonConfiguration.DefaultProcessingDelayJitterMs;
        private int _maxConcurrency = CommonConfiguration.MaxConcurrency;
        private bool _enableAllLibraries = true;
        private string[] _enabledLibraryIds = Array.Empty<string>();
        private bool _extractVideoChapters = true;
        private bool _includeExtraVideos = true;
        private bool _enableMusicMetadata = true;
        private bool _enableExternalLyrics = true;
        private string _customBackupDirectory = string.Empty;
        private bool _enableDebugLogging;

        public bool EnableAutoExtract
        {
            get => _enableAutoExtract;
            set => _enableAutoExtract = value;
        }

        public int ProcessingDelayMs
        {
            get => _processingDelayMs;
            set => _processingDelayMs = Math.Clamp(value, 0, CommonConfiguration.MaxProcessingDelayMs);
        }

        public int ProcessingDelayJitterMs
        {
            get => _processingDelayJitterMs;
            set => _processingDelayJitterMs = Math.Clamp(value, 0, CommonConfiguration.MaxProcessingDelayJitterMs);
        }

        public int MaxConcurrency
        {
            get => _maxConcurrency;
            set => _maxConcurrency = Math.Clamp(
                value,
                CommonConfiguration.MinConcurrency,
                CommonConfiguration.MaxConcurrencyLimit);
        }

        public bool EnableAllLibraries
        {
            get => _enableAllLibraries;
            set => _enableAllLibraries = value;
        }

        public string[] EnabledLibraryIds
        {
            get => _enabledLibraryIds;
            set => _enabledLibraryIds = NormalizeStringArray(value);
        }

        public bool ExtractVideoChapters
        {
            get => _extractVideoChapters;
            set => _extractVideoChapters = value;
        }

        public bool IncludeExtraVideos
        {
            get => _includeExtraVideos;
            set => _includeExtraVideos = value;
        }

        public bool EnableMusicMetadata
        {
            get => _enableMusicMetadata;
            set => _enableMusicMetadata = value;
        }

        public bool EnableExternalLyrics
        {
            get => _enableExternalLyrics;
            set => _enableExternalLyrics = value;
        }

        public string CustomBackupDirectory
        {
            get => _customBackupDirectory;
            set => _customBackupDirectory = (value ?? string.Empty).Trim();
        }

        public bool EnableDebugLogging
        {
            get => _enableDebugLogging;
            set => _enableDebugLogging = value;
        }

        private static string[] NormalizeStringArray(string[]? values)
        {
            if (values == null || values.Length == 0)
            {
                return Array.Empty<string>();
            }

            var result = new List<string>(values.Length);
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var value in values)
            {
                if (string.IsNullOrWhiteSpace(value))
                {
                    continue;
                }

                var trimmed = value.Trim();
                if (seen.Add(trimmed))
                {
                    result.Add(trimmed);
                }
            }

            return result.Count == 0 ? Array.Empty<string>() : result.ToArray();
        }
    }
}
