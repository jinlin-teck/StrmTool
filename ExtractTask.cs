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
        protected PluginConfiguration _config;
        protected SemaphoreSlim _semaphore;

        private readonly ILibraryManager _libraryManager;
        private LibraryScanListener _scanListener;
        private readonly CancellationTokenSource _backgroundTaskCts = new CancellationTokenSource();
        private readonly object _eventLock = new object();

        public ExtractTask(
            ILibraryManager libraryManager,
            IMediaEncoder mediaEncoder,
            IMediaStreamRepository mediaStreamRepository,
            ILogger<ExtractTask> logger)
        {
            _logger = logger;
            _mediaInfoService = new StrmMediaInfoService(libraryManager, mediaEncoder, mediaStreamRepository, logger);
            _mediaCache = new MediaInfoCache(logger);

            if (Plugin.Instance == null)
            {
                _logger.LogWarning("StrmTool - Plugin instance not found, using default configuration");
                _config = new PluginConfiguration();
            }
            else
            {
                _config = Plugin.Instance.Configuration;
            }

            _semaphore = new SemaphoreSlim(_config.MaxConcurrentExtract);

            _libraryManager = libraryManager;

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
                _config = Plugin.Instance.Configuration;
            }
        }

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
            return processed;
        }

        private void OnStrmFileDetected(object sender, BaseItem item)
        {
            if (_disposed)
                return;

            RefreshConfig();

            var fileName = System.IO.Path.GetFileNameWithoutExtension(item.Path);

            if (!_config.EnableAutoExtract)
            {
                _logger.LogDebug("StrmTool - Auto-extract is disabled, skipping {Name}", fileName);
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
                    _logger.LogDebug("StrmTool - Extraction cancelled for {Name}", fileName);
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "StrmTool - Error in auto-extract background task for {Name}", fileName);
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
            }
        }

        public async Task ExecuteAsync(IProgress<double> progress, CancellationToken cancellationToken)
        {
            RefreshConfig();
            _logger.LogInformation("StrmTool - Starting strm file scan...");

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

        private async Task ProcessStrmFiles(List<BaseItem> strmItems, IProgress<double> progress, CancellationToken cancellationToken)
        {
            int processed = await ProcessStrmItemsAsync(strmItems, ProcessSingleItemAsync, progress, cancellationToken);

            _logger.LogInformation("StrmTool - Task complete. Successfully processed {Processed}/{Total} strm files.",
                processed, strmItems.Count);
        }

        private async Task ProcessSingleItemAsync(BaseItem item, CancellationToken cancellationToken)
        {
            _logger.LogDebug("StrmTool - Processing {Name}", item.Name);

            var beforeStreams = _mediaInfoService.GetItemMediaStreams(item);
            _logger.LogTrace("StrmTool - Before: {Count} streams", beforeStreams.Count);

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

            await Task.Delay(_config.RefreshDelayMs, cancellationToken);
        }

        public async Task ExtractSingleItemAsync(BaseItem item, CancellationToken cancellationToken)
        {
            RefreshConfig();
            var fileName = System.IO.Path.GetFileNameWithoutExtension(item.Path);
            var lockTaken = false;

            try
            {
                await _semaphore.WaitAsync(cancellationToken).ConfigureAwait(false);
                lockTaken = true;

                _logger.LogDebug("StrmTool - Auto-extracting media info for new strm file: {Name}", fileName);

                var beforeStreams = _mediaInfoService.GetItemMediaStreams(item);
                _logger.LogDebug("StrmTool - Before: {Count} streams", beforeStreams.Count);

                bool hasVideo = beforeStreams.Any(s => s.Type == MediaStreamType.Video);
                bool hasAudio = beforeStreams.Any(s => s.Type == MediaStreamType.Audio);

                if (!_config.ForceRefreshIgnoreExisting && (hasVideo || hasAudio))
                {
                    _logger.LogInformation("StrmTool - {Name} already has media stream info, skipping", fileName);
                    return;
                }

                if (_config.EnableMediaInfoCache && !_config.ForceRefreshIgnoreCache && _mediaCache.TryGetCachedMediaStreams(item.Path, out var cachedStreams))
                {
                    _mediaInfoService.SaveMediaStreams(item.Id, cachedStreams, cancellationToken);
                    _logger.LogInformation("StrmTool - Auto-extract: {Name} using cached media info", fileName);
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
                _semaphore?.Dispose();
                _semaphore = null;
            }
        }
    }
}
