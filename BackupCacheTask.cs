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
    public class BackupCacheTask : StrmToolTaskBase
    {
        public BackupCacheTask(
            ILibraryManager libraryManager,
            IMediaEncoder mediaEncoder,
            IMediaStreamRepository mediaStreamRepository,
            ILogger<BackupCacheTask> logger)
            : base(libraryManager, mediaEncoder, mediaStreamRepository, logger)
        {
        }

        public override async Task ExecuteAsync(IProgress<double> progress, CancellationToken cancellationToken)
        {
            RefreshConfig();
            _logger.LogInformation("StrmTool - Starting media info backup to cache files...");

            var strmItems = _mediaInfoService.GetAllStrmItems(cancellationToken);

            var total = strmItems.Count;
            if (total == 0)
            {
                progress.Report(100);
                _logger.LogInformation("StrmTool - Backup task complete. No strm files found.");
                return;
            }

            int saved = 0;
            int skippedHasCache = 0;
            int skippedNoStreams = 0;
            int probedAttempted = 0;
            int probedSucceeded = 0;

            await ProcessStrmItemsAsync(strmItems, async (item, ct) =>
            {
                if (_mediaCache.HasCacheFile(item.Path))
                {
                    Interlocked.Increment(ref skippedHasCache);
                    return;
                }

                var mediaStreams = _mediaInfoService.GetItemMediaStreams(item);
                var hasVideo = mediaStreams.Any(s => s.Type == MediaStreamType.Video);
                var hasAudio = mediaStreams.Any(s => s.Type == MediaStreamType.Audio);

                if (!(hasVideo || hasAudio))
                {
                    Interlocked.Increment(ref probedAttempted);
                    var probedStreams = await _mediaInfoService.ProbeAndSaveMediaStreamsAsync(item, ct).ConfigureAwait(false);
                    if (probedStreams.Count > 0)
                    {
                        Interlocked.Increment(ref probedSucceeded);
                        mediaStreams = probedStreams;
                    }
                    else
                    {
                        mediaStreams = _mediaInfoService.GetItemMediaStreams(item);
                    }
                }

                await Task.Delay(_config.RefreshDelayMs, ct).ConfigureAwait(false);

                if (mediaStreams.Count == 0)
                {
                    Interlocked.Increment(ref skippedNoStreams);
                    return;
                }

                await _mediaCache.SaveCacheAsync(item.Path, mediaStreams, ct).ConfigureAwait(false);
                Interlocked.Increment(ref saved);
            }, progress, cancellationToken);

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

        public override string Category => "StrmTool";
        public override string Key => "StrmToolBackupCacheTask";
        public override string Description => Plugin.Instance?.GetLocalizedString("StrmTool.BackupTaskDescription")
            ?? "Backup existing media stream information from strm items to cache files";
        public override string Name => Plugin.Instance?.GetLocalizedString("StrmTool.BackupTaskName")
            ?? "Backup Strm Media Info Cache";
    }
}