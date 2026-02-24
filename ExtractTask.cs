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
        protected volatile bool _disposed = false;
        protected readonly ILogger _logger;
        protected readonly StrmMediaInfoService _mediaInfoService;
        protected readonly MediaInfoCache _mediaCache;
        protected volatile PluginConfiguration _config;
        protected readonly SemaphoreSlim _semaphore;

        private readonly ILibraryManager _libraryManager;
        private readonly IMediaStreamRepository _mediaStreamRepository;
        private LibraryScanListener _scanListener;
        private ItemUpdateListener _updateListener;
        private readonly CancellationTokenSource _backgroundTaskCts = new CancellationTokenSource();
        private readonly object _eventLock = new object();

        public ExtractTask(
            ILibraryManager libraryManager,
            IMediaEncoder mediaEncoder,
            IMediaStreamRepository mediaStreamRepository,
            IItemRepository itemRepository,
            ILogger<ExtractTask> logger)
        {
            _logger = logger;
            _mediaInfoService = new StrmMediaInfoService(libraryManager, mediaEncoder, mediaStreamRepository, itemRepository, logger);
            _mediaCache = new MediaInfoCache(logger);

            if (Plugin.Instance == null)
            {
                _logger.LogWarning("Plugin instance not found, using default configuration");
                _config = new PluginConfiguration();
            }
            else
            {
                _config = Plugin.Instance.Configuration;
            }

            _semaphore = new SemaphoreSlim(_config.MaxConcurrentExtract);

            _libraryManager = libraryManager;
            _mediaStreamRepository = mediaStreamRepository;

            try
            {
                _scanListener = new LibraryScanListener(_libraryManager, _logger, _config);
                _scanListener.StrmFileDetected += OnStrmFileDetected;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to initialize library scan listener");
            }

            try
            {
                _updateListener = new ItemUpdateListener(_libraryManager, _logger, _config);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to initialize item update listener");
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

        protected void RefreshConfig()
        {
            if (Plugin.Instance != null)
            {
                // 注意：MaxConcurrentExtract 需要 Jellyfin 重启后生效
                // 避免在运行时替换 semaphore 导致并发控制失效
                _config = Plugin.Instance.Configuration;
            }
        }

        /// <summary>
        /// 处理 strm 文件项的通用方法
        /// </summary>
        protected async Task<int> ProcessStrmItemsAsync(
            List<BaseItem> items,
            Func<BaseItem, CancellationToken, Task> processItemAsync,
            IProgress<double> progress,
            CancellationToken cancellationToken)
        {
            int processed = 0;
            int total = items.Count;

            var tasks = items.Select(async item =>
            {
                await _semaphore.WaitAsync(cancellationToken);
                try
                {
                    if (cancellationToken.IsCancellationRequested)
                    {
                        return;
                    }

                    await processItemAsync(item, cancellationToken).ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Error processing {Name} ({Path})", item.Name, item.Path);
                }
                finally
                {
                    _semaphore.Release();
                    int current = Interlocked.Increment(ref processed);
                    double percent = Math.Min((double)current / total * 100, 100);
                    progress.Report(percent);
                }
            });

            await Task.WhenAll(tasks);
            progress.Report(100);
            return processed;
        }

        /// <summary>
        /// 尝试从缓存加载媒体流，如果成功则直接保存
        /// </summary>
        /// <param name="item">库条目</param>
        /// <param name="cancellationToken">取消令牌</param>
        /// <returns>是否成功从缓存加载</returns>
        private bool TryLoadFromCache(BaseItem item, CancellationToken cancellationToken)
        {
            if (!_config.EnableMediaInfoCache || _config.ForceRefreshIgnoreCache)
            {
                return false;
            }

            if (!_mediaCache.TryGetCachedMediaStreams(item.Path, out var cachedStreams))
            {
                return false;
            }

            try
            {
                _mediaInfoService.SaveMediaStreams(item.Id, cachedStreams, cancellationToken);
                _logger.LogInformation("{Name}: Used cached media info ({Count} streams)",
                    item.Name, cachedStreams.Count);
                return true;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "{Name}: Failed to save cached media streams", item.Name);
                return false;
            }
        }

        /// <summary>
        /// 探测媒体流并保存到缓存
        /// </summary>
        /// <param name="item">库条目</param>
        /// <param name="cancellationToken">取消令牌</param>
        /// <returns>探测结果</returns>
        private async Task<MediaProbeResult> ProbeAndCacheAsync(BaseItem item, CancellationToken cancellationToken)
        {
            var probeResult = await _mediaInfoService.ProbeMediaStreamsAsync(item, cancellationToken).ConfigureAwait(false);

            if (_config.EnableMediaInfoCache && probeResult.Success)
            {
                await _mediaCache.SaveFullCacheAsync(
                    item.Path,
                    probeResult.MediaStreams,
                    probeResult.Size,
                    probeResult.RunTimeTicks,
                    probeResult.Container,
                    cancellationToken).ConfigureAwait(false);
            }

            return probeResult;
        }

        private void OnStrmFileDetected(object sender, BaseItem item)
        {
            if (_disposed)
                return;

            RefreshConfig();

            var fileName = Path.GetFileNameWithoutExtension(item.Path);

            if (!_config.EnableAutoExtract)
            {
                _logger.LogDebug("Auto-extract is disabled, skipping {Name}", fileName);
                return;
            }

            _ = Task.Run(async () =>
            {
                try
                {
                    using var cts = CancellationTokenSource.CreateLinkedTokenSource(_backgroundTaskCts.Token);
                    cts.CancelAfter(TimeSpan.FromMinutes(5));
                    await Task.Delay(_config.RefreshDelayMs, cts.Token).ConfigureAwait(false);

                    if (_disposed)
                        return;

                    await ExtractSingleItemAsync(item, cts.Token).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    _logger.LogDebug("Extraction cancelled for {Name}", fileName);
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Error in auto-extract background task for {Name}", fileName);
                }
            }, _backgroundTaskCts.Token);
        }

        public void CleanupListener()
        {
            lock (_eventLock)
            {
                if (_scanListener != null)
                {
                    _scanListener.StrmFileDetected -= OnStrmFileDetected;
                    _scanListener.Dispose();
                    _scanListener = null;
                }

                if (_updateListener != null)
                {
                    _updateListener.Dispose();
                    _updateListener = null;
                }
            }
        }

        public async Task ExecuteAsync(IProgress<double> progress, CancellationToken cancellationToken)
        {
            RefreshConfig();
            _logger.LogInformation("Starting strm file scan...");

            try
            {
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
                        _logger.LogError(ex, "Error checking media streams for {Name}", item.Name);
                        strmItems.Add(item);
                    }
                }

                _logger.LogInformation("Found {Count} strm files in library, {NeedRefresh} need media info",
                    totalFound, strmItems.Count);

                if (strmItems.Count == 0)
                {
                    progress.Report(100);
                    _logger.LogInformation("Nothing to process, task complete.");
                    return;
                }

                await ProcessStrmFiles(strmItems, progress, cancellationToken);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Fatal error during strm file scan");
                throw;
            }
        }

        private async Task ProcessStrmFiles(List<BaseItem> strmItems, IProgress<double> progress, CancellationToken cancellationToken)
        {
            int processed = await ProcessStrmItemsAsync(strmItems, ProcessSingleItemAsync, progress, cancellationToken);

            _logger.LogInformation("Task complete. Successfully processed {Processed}/{Total} strm files.",
                processed, strmItems.Count);
        }

        /// <summary>
        /// 核心处理逻辑：从缓存加载或探测媒体流
        /// </summary>
        private async Task<(List<MediaStream> before, List<MediaStream> after)> ProcessItemCoreAsync(
            BaseItem item, 
            string logPrefix,
            CancellationToken cancellationToken)
        {
            var beforeStreams = _mediaInfoService.GetItemMediaStreams(item);
            _logger.LogDebug("{Prefix} - Before: {Count} streams", logPrefix, beforeStreams.Count);

            // 首先尝试从缓存加载
            bool loadedFromCache = TryLoadFromCache(item, cancellationToken);

            if (!loadedFromCache)
            {
                // 缓存未命中，执行探测并保存到缓存
                await ProbeAndCacheAsync(item, cancellationToken).ConfigureAwait(false);
            }

            var afterStreams = _mediaInfoService.GetItemMediaStreams(item);
            return (beforeStreams, afterStreams);
        }

        private async Task ProcessSingleItemAsync(BaseItem item, CancellationToken cancellationToken)
        {
            _logger.LogDebug("Processing {Name}", item.Name);

            var (beforeStreams, afterStreams) = await ProcessItemCoreAsync(
                item, 
                "StrmTool", 
                cancellationToken).ConfigureAwait(false);

            bool hasVideo = afterStreams.Any(s => s.Type == MediaStreamType.Video);
            bool hasAudio = afterStreams.Any(s => s.Type == MediaStreamType.Audio);

            _logger.LogInformation(
                "{Name}: Probe done. Streams {Before}→{After}. Video:{Video}, Audio:{Audio}",
                item.Name,
                beforeStreams.Count,
                afterStreams.Count,
                hasVideo,
                hasAudio
            );

            if (!(hasVideo || hasAudio))
            {
                _logger.LogWarning("{Name} may still lack media stream info", item.Name);
            }
        }

        public async Task ExtractSingleItemAsync(BaseItem item, CancellationToken cancellationToken)
        {
            RefreshConfig();
            var fileName = Path.GetFileNameWithoutExtension(item.Path);

            await _semaphore.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                _logger.LogDebug("Auto-extracting media info for new strm file: {Name}", fileName);

                var beforeStreams = _mediaInfoService.GetItemMediaStreams(item);
                _logger.LogDebug("Before: {Count} streams", beforeStreams.Count);

                bool hasVideo = beforeStreams.Any(s => s.Type == MediaStreamType.Video);
                bool hasAudio = beforeStreams.Any(s => s.Type == MediaStreamType.Audio);

                if (!_config.ForceRefreshIgnoreExisting && (hasVideo || hasAudio))
                {
                    _logger.LogInformation("{Name} already has media stream info, skipping", fileName);
                    return;
                }

                // 使用提取的通用方法处理缓存和探测
                var (_, afterStreams) = await ProcessItemCoreAsync(
                    item, 
                    "Auto-extract", 
                    cancellationToken).ConfigureAwait(false);

                _logger.LogInformation(
                    "Auto-extract complete for {Name}. Streams {Before}→{After}",
                    fileName,
                    beforeStreams.Count,
                    afterStreams.Count
                );
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error in auto-extract for {Name}", fileName);
            }
            finally
            {
                _semaphore.Release();
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

            _disposed = true;

            if (disposing)
            {
                try
                {
                    _backgroundTaskCts.Cancel();
                }
                catch (ObjectDisposedException)
                {
                }

                CleanupListener();
                _backgroundTaskCts.Dispose();
                _semaphore.Dispose();
            }
        }
    }
}
