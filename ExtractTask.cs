using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.MediaEncoding;
using MediaBrowser.Controller.Persistence;
using MediaBrowser.Model.Tasks;
using MediaBrowser.Model.Entities;

namespace StrmTool
{
    public class ExtractTask : IScheduledTask, IDisposable
    {
        private readonly ILogger<ExtractTask> _logger;
        private readonly ILibraryManager _libraryManager;
        private readonly StrmMediaInfoService _mediaInfoService;
        private PluginConfiguration _config;
        private readonly MediaInfoCache _mediaCache;
        private LibraryScanListener _scanListener;
        private SemaphoreSlim _semaphore;
        private bool _disposed = false;

        public ExtractTask(
            ILibraryManager libraryManager,
            IMediaEncoder mediaEncoder,
            IMediaStreamRepository mediaStreamRepository,
            ILogger<ExtractTask> logger)
        {
            _libraryManager = libraryManager;
            _logger = logger;
            _mediaInfoService = new StrmMediaInfoService(libraryManager, mediaEncoder, mediaStreamRepository, logger);
            
            if (Plugin.Instance == null)
            {
                _logger.LogWarning("StrmTool - Plugin instance not found, using default configuration");
                _config = new PluginConfiguration();
            }
            else
            {
                _config = Plugin.Instance.Configuration;
            }
            
            _mediaCache = new MediaInfoCache(_logger);
            
            // 初始化信号量
            _semaphore = new SemaphoreSlim(_config.MaxConcurrentExtract);

            // 始终初始化库监听器，通过配置控制是否处理事件
            try
            {
                _scanListener = new LibraryScanListener(_libraryManager, _logger, _config);
                _scanListener.StrmFileDetected += OnStrmFileDetected;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "StrmTool - Failed to initialize library scan listener");
            }
        }

        /// <summary>
        /// 处理监听器触发的 strm 文件检测事件
        /// </summary>
        private async void OnStrmFileDetected(object sender, BaseItem item)
        {
            // 刷新配置以确保获取最新的设置
            RefreshConfig();

            var fileName = System.IO.Path.GetFileNameWithoutExtension(item.Path);

            if (!_config.EnableAutoExtract)
            {
                _logger.LogDebug("StrmTool - Auto-extract is disabled, skipping {Name}", fileName);
                return;
            }

            try
            {
                using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(5));
                await Task.Delay(_config.RefreshDelayMs, cts.Token).ConfigureAwait(false);
                await ExtractSingleItemAsync(item, cts.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                _logger.LogWarning("StrmTool - Extraction cancelled for {Name}", fileName);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "StrmTool - Error in auto-extract background task for {Name}", fileName);
            }
        }

        /// <summary>
        /// 清理监听器（在插件卸载时调用）
        /// </summary>
        public void CleanupListener()
        {
            _scanListener?.Dispose();
            _scanListener = null;
        }

        public async Task ExecuteAsync(IProgress<double> progress, CancellationToken cancellationToken)
        {
            // 刷新配置以确保获取最新的设置
            RefreshConfig();
            _logger.LogInformation("StrmTool - Starting strm file scan...");

            try
            {
                // 方法1：使用更安全的方式获取所有库根目录
                var allStrmItems = _mediaInfoService.GetAllStrmItems(cancellationToken);
                var strmItems = new List<BaseItem>();
                int totalFound = allStrmItems.Count;

                foreach (var item in allStrmItems)
                {
                    if (cancellationToken.IsCancellationRequested)
                    {
                        break;
                    }

                    try
                    {
                        var mediaStreams = _mediaInfoService.GetItemMediaStreams(item);
                        bool hasVideo = mediaStreams.Any(s => s.Type == MediaStreamType.Video);
                        bool hasAudio = mediaStreams.Any(s => s.Type == MediaStreamType.Audio);

                        if (_config.ForceRefreshIgnoreExisting || !(hasVideo || hasAudio))
                        {
                            strmItems.Add(item);
                        }
                    }
                    catch (Exception ex)
                    {
                        _logger.LogError(ex, "StrmTool - Error checking media streams for {Name}", item.Name);
                        strmItems.Add(item);
                    }
                }
                
                _logger.LogInformation("StrmTool - Found {Count} strm files in library, {NeedRefresh} need media info", 
                    totalFound, strmItems.Count);

                _logger.LogInformation("StrmTool - {Count} strm files need metadata refresh", strmItems.Count);

                if (strmItems.Count == 0)
                {
                    progress.Report(100);
                    _logger.LogInformation("StrmTool - Nothing to process, task complete.");
                    return;
                }

                await ProcessStrmFiles(strmItems, progress, cancellationToken);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "StrmTool - Fatal error during strm file scan");
                throw;
            }
        }

        /// <summary>
        /// 处理 strm 文件
        /// </summary>
        private async Task ProcessStrmFiles(List<BaseItem> strmItems, IProgress<double> progress, CancellationToken cancellationToken)
        {
            
            int processed = 0;
            int total = strmItems.Count;

            var tasks = strmItems.Select(async item =>
            {
                await _semaphore.WaitAsync(cancellationToken);
                try
                {
                    if (cancellationToken.IsCancellationRequested)
                    {
                        return;
                    }

                    _logger.LogDebug("StrmTool - Processing {Name}", item.Name);

                    var beforeStreams = _mediaInfoService.GetItemMediaStreams(item);
                    _logger.LogTrace("StrmTool - Before: {Count} streams", beforeStreams.Count);

                    // 检查缓存（如果不禁用缓存且不禁用缓存忽略）
                    if (_config.EnableMediaInfoCache && !_config.ForceRefreshIgnoreCache && _mediaCache.TryGetCachedMediaStreams(item.Path, out var cachedStreams))
                    {
                        _mediaInfoService.SaveMediaStreams(item.Id, cachedStreams, cancellationToken);
                        _logger.LogInformation("StrmTool - {Name}: Used cached media info ({Count} streams)",
                            item.Name, cachedStreams.Count);
                    }
                    else
                    {
                        var probedStreams = await _mediaInfoService.ProbeAndSaveMediaStreamsAsync(item, cancellationToken).ConfigureAwait(false);
                        if (_config.EnableMediaInfoCache && probedStreams.Count > 0)
                        {
                            await _mediaCache.SaveCacheAsync(item.Path, probedStreams, cancellationToken).ConfigureAwait(false);
                        }
                    }

                    var afterStreams = _mediaInfoService.GetItemMediaStreams(item);
                    bool hasVideo = afterStreams.Any(s => s.Type == MediaStreamType.Video);
                    bool hasAudio = afterStreams.Any(s => s.Type == MediaStreamType.Audio);

                    _logger.LogInformation(
                        "StrmTool - {Name}: Probe done. Streams {Before}→{After}. Video:{Video}, Audio:{Audio}",
                        item.Name,
                        beforeStreams.Count,
                        afterStreams.Count,
                        hasVideo,
                        hasAudio
                    );

                    if (!(hasVideo || hasAudio))
                    {
                        _logger.LogWarning("StrmTool - {Name} may still lack media stream info", item.Name);
                    }

                    // 添加延迟，避免对远程服务器造成压力
                    await Task.Delay(_config.RefreshDelayMs, cancellationToken);
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "StrmTool - Error processing {Name} ({Path})", item.Name, item.Path);
                }
                finally
                {
                    _semaphore.Release();
                    int current = Interlocked.Increment(ref processed);
                    double percent = (double)current / total * 100;
                    progress.Report(percent);
                }
            });

            await Task.WhenAll(tasks);

            progress.Report(100);
            _logger.LogInformation("StrmTool - Task complete. Successfully processed {Processed}/{Total} strm files.", 
                processed, total);
        }

        /// <summary>
        /// 提取单个 strm 文件的媒体信息（用于自动提取）
        /// </summary>
        public async Task ExtractSingleItemAsync(BaseItem item, CancellationToken cancellationToken)
        {
            // 刷新配置以确保获取最新的设置
            RefreshConfig();
            // 使用实际文件名而不是 item.Name，因为 item.Name 可能还没有完全解析
            var fileName = System.IO.Path.GetFileNameWithoutExtension(item.Path);
            var lockTaken = false;

            try
            {
                await _semaphore.WaitAsync(cancellationToken).ConfigureAwait(false);
                lockTaken = true;

                _logger.LogInformation("StrmTool - Auto-extracting media info for new strm file: {Name}", fileName);

                var beforeStreams = _mediaInfoService.GetItemMediaStreams(item);
                _logger.LogDebug("StrmTool - Before: {Count} streams", beforeStreams.Count);

                // 检查是否已有完整的媒体流
                bool hasVideo = beforeStreams.Any(s => s.Type == MediaStreamType.Video);
                bool hasAudio = beforeStreams.Any(s => s.Type == MediaStreamType.Audio);

                if (!_config.ForceRefreshIgnoreExisting && (hasVideo || hasAudio))
                {
                    _logger.LogInformation("StrmTool - {Name} already has media stream info, skipping", fileName);
                    return;
                }

                // 检查缓存（如果不禁用缓存且不禁用缓存忽略）
                if (_config.EnableMediaInfoCache && !_config.ForceRefreshIgnoreCache && _mediaCache.TryGetCachedMediaStreams(item.Path, out var cachedStreams))
                {
                    _mediaInfoService.SaveMediaStreams(item.Id, cachedStreams, cancellationToken);
                    _logger.LogInformation("StrmTool - Auto-extract: {Name} using cached media info", fileName);
                }
                else
                {
                    // 执行探测
                    var probedStreams = await _mediaInfoService.ProbeAndSaveMediaStreamsAsync(item, cancellationToken).ConfigureAwait(false);
                    if (_config.EnableMediaInfoCache && probedStreams.Count > 0)
                    {
                        await _mediaCache.SaveCacheAsync(item.Path, probedStreams, cancellationToken).ConfigureAwait(false);
                    }
                }

                var afterStreams = _mediaInfoService.GetItemMediaStreams(item);
                _logger.LogInformation(
                    "StrmTool - Auto-extract complete for {Name}. Streams {Before}→{After}",
                    fileName,
                    beforeStreams.Count,
                    afterStreams.Count
                );
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "StrmTool - Error in auto-extract for {Name}", fileName);
            }
            finally
            {
                if (lockTaken)
                {
                    _semaphore.Release();
                }
            }
        }

        public string Category => "StrmTool";
        public string Key => "StrmToolTask";
        public string Description => Plugin.Instance?.GetLocalizedString("StrmTool.TaskDescription") ?? "Extract media technical information (codec, resolution, subtitles) from strm files";
        public string Name => Plugin.Instance?.GetLocalizedString("StrmTool.TaskName") ?? "Extract Strm Media Info";

        public IEnumerable<TaskTriggerInfo> GetDefaultTriggers()
        {
            return Array.Empty<TaskTriggerInfo>();
        }

        /// <summary>
        /// 刷新配置引用，确保获取最新的配置值
        /// </summary>
        private void RefreshConfig()
        {
            if (Plugin.Instance != null)
            {
                _config = Plugin.Instance.Configuration;
            }
        }

        public void Dispose()
        {
            Dispose(true);
            GC.SuppressFinalize(this);
        }

        protected virtual void Dispose(bool disposing)
        {
            if (_disposed)
                return;

            if (disposing)
            {
                CleanupListener();
                _semaphore?.Dispose();
                _semaphore = null;
            }

            _disposed = true;
        }
    }
}
