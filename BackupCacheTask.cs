using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.MediaEncoding;
using MediaBrowser.Controller.Persistence;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.Tasks;

namespace StrmTool
{
    /// <summary>
    /// 备份媒体信息到缓存文件（不覆盖已有缓存）
    /// </summary>
    public class BackupCacheTask : IScheduledTask
    {
        private readonly ILogger<BackupCacheTask> _logger;
        private readonly StrmMediaInfoService _mediaInfoService;
        private readonly MediaInfoCache _mediaCache;

        public BackupCacheTask(
            ILibraryManager libraryManager,
            IMediaEncoder mediaEncoder,
            IMediaStreamRepository mediaStreamRepository,
            ILogger<BackupCacheTask> logger)
        {
            _mediaInfoService = new StrmMediaInfoService(libraryManager, mediaEncoder, mediaStreamRepository, logger);
            _logger = logger;
            _mediaCache = new MediaInfoCache(logger);
        }

        public async Task ExecuteAsync(IProgress<double> progress, CancellationToken cancellationToken)
        {
            _logger.LogInformation("StrmTool - Starting media info backup to cache files...");

            var strmItems = _mediaInfoService.GetAllStrmItems(cancellationToken);

            var total = strmItems.Count;
            if (total == 0)
            {
                progress.Report(100);
                _logger.LogInformation("StrmTool - Backup task complete. No strm files found.");
                return;
            }

            int processed = 0;
            int saved = 0;
            int skippedHasCache = 0;
            int skippedNoStreams = 0;
            int probedAttempted = 0;
            int probedSucceeded = 0;

            foreach (var item in strmItems)
            {
                if (cancellationToken.IsCancellationRequested)
                {
                    break;
                }

                try
                {
                    if (_mediaCache.HasCacheFile(item.Path))
                    {
                        skippedHasCache++;
                        continue;
                    }

                    var mediaStreams = _mediaInfoService.GetItemMediaStreams(item);
                    var hasVideo = mediaStreams.Any(s => s.Type == MediaStreamType.Video);
                    var hasAudio = mediaStreams.Any(s => s.Type == MediaStreamType.Audio);

                    if (!(hasVideo || hasAudio))
                    {
                        probedAttempted++;
                        var probedStreams = await _mediaInfoService.ProbeAndSaveMediaStreamsAsync(item, cancellationToken).ConfigureAwait(false);
                        if (probedStreams.Count > 0)
                        {
                            probedSucceeded++;
                            mediaStreams = probedStreams;
                        }
                        else
                        {
                            mediaStreams = _mediaInfoService.GetItemMediaStreams(item);
                        }
                    }

                    if (mediaStreams.Count == 0)
                    {
                        skippedNoStreams++;
                        continue;
                    }

                    await _mediaCache.SaveCacheAsync(item.Path, mediaStreams, cancellationToken).ConfigureAwait(false);
                    saved++;
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "StrmTool - Error backing up cache for {Name} ({Path})", item.Name, item.Path);
                }
                finally
                {
                    processed++;
                    progress.Report((double)processed / total * 100);
                }
            }

            progress.Report(100);
            _logger.LogInformation(
                "StrmTool - Backup task complete. Total: {Total}, Saved: {Saved}, Skipped(HasCache): {HasCache}, Skipped(NoStreams): {NoStreams}",
                total,
                saved,
                skippedHasCache,
                skippedNoStreams);
            _logger.LogInformation(
                "StrmTool - Backup task probe stats: Attempted {Attempted}, Succeeded {Succeeded}",
                probedAttempted,
                probedSucceeded);
        }

        public string Category => Plugin.Instance?.GetLocalizedString("StrmTool.TaskCategory") ?? "Strm Tool";
        public string Key => "StrmToolBackupCacheTask";
        public string Description => Plugin.Instance?.GetLocalizedString("StrmTool.BackupTaskDescription")
            ?? "Backup existing media stream information from strm items to cache files";
        public string Name => Plugin.Instance?.GetLocalizedString("StrmTool.BackupTaskName")
            ?? "Backup Strm Media Info Cache";

        public IEnumerable<TaskTriggerInfo> GetDefaultTriggers()
        {
            return Array.Empty<TaskTriggerInfo>();
        }
    }
}
