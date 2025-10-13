using MediaBrowser.Controller.Library;
using MediaBrowser.Model.IO;
using MediaBrowser.Model.Tasks;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Providers;
using Jellyfin.Data.Enums;
using MediaBrowser.Model.Entities;

namespace StrmTool
{
    public class ExtractTask : IScheduledTask
    {
        private readonly ILogger<ExtractTask> _logger;
        private readonly ILibraryManager _libraryManager;
        private readonly IFileSystem _fileSystem;

        public ExtractTask(
            ILibraryManager libraryManager,
            ILogger<ExtractTask> logger,
            IFileSystem fileSystem)
        {
            _libraryManager = libraryManager;
            _logger = logger;
            _fileSystem = fileSystem;
        }

        public async Task ExecuteAsync(IProgress<double> progress, CancellationToken cancellationToken)
        {
            _logger.LogInformation("StrmTool - Starting strm file scan...");

            var query = new InternalItemsQuery
            {
                Recursive = true
            };

            var allItems = _libraryManager.GetItemList(query)
                .Where(i => i.Path?.EndsWith(".strm", StringComparison.OrdinalIgnoreCase) == true)
                .ToList();

            _logger.LogInformation("StrmTool - Found {Count} strm files in library", allItems.Count);

            var strmItems = allItems
                .Where(i =>
                {
                    var streams = i.GetMediaStreams() ?? new List<MediaStream>();
                    bool hasVideo = streams.Any(s => s.Type == MediaStreamType.Video);
                    bool hasAudio = streams.Any(s => s.Type == MediaStreamType.Audio);
                    return !hasVideo || !hasAudio;
                })
                .ToList();

            _logger.LogInformation("StrmTool - {Count} strm files need metadata refresh", strmItems.Count);

            if (strmItems.Count == 0)
            {
                progress.Report(100);
                _logger.LogInformation("StrmTool - Nothing to process, task complete.");
                return;
            }

            var directoryService = new DirectoryService(_fileSystem);
            var options = new MetadataRefreshOptions(directoryService)
            {
                EnableRemoteContentProbe = true,
                ReplaceAllMetadata = true,
                MetadataRefreshMode = MetadataRefreshMode.ValidationOnly,
                RegenerateTrickplay = false,
                ReplaceAllImages = false,
                ImageRefreshMode = MetadataRefreshMode.None
            };

            int processed = 0;
            int total = strmItems.Count;

            // 顺序处理，避免触发远程服务器风控
            foreach (var item in strmItems)
            {
                if (cancellationToken.IsCancellationRequested)
                {
                    _logger.LogInformation("StrmTool - Task was cancelled");
                    break;
                }

                try
                {
                    _logger.LogDebug("StrmTool - Processing {Name}", item.Name);

                    var beforeStreams = item.GetMediaStreams() ?? new List<MediaStream>();
                    _logger.LogTrace("StrmTool - Before: {Count} streams", beforeStreams.Count);

                    var result = await item.RefreshMetadata(options, cancellationToken);

                    var afterStreams = item.GetMediaStreams() ?? new List<MediaStream>();
                    bool hasVideo = afterStreams.Any(s => s.Type == MediaStreamType.Video);
                    bool hasAudio = afterStreams.Any(s => s.Type == MediaStreamType.Audio);

                    _logger.LogInformation(
                        "StrmTool - {Name}: Refresh done. Streams {Before}→{After}. Video:{Video}, Audio:{Audio}",
                        item.Name,
                        beforeStreams.Count,
                        afterStreams.Count,
                        hasVideo,
                        hasAudio
                    );

                    if (!hasVideo || !hasAudio)
                    {
                        _logger.LogWarning("StrmTool - {Name} may still lack full media info", item.Name);
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "StrmTool - Error processing {Name} ({Path})", item.Name, item.Path);
                }

                processed++;
                double percent = (double)processed / total * 100;
                progress.Report(percent);

                // 添加延迟，避免对远程服务器造成压力
                if (processed < total) // 最后一个文件不需要延迟
                {
                    await Task.Delay(1000, cancellationToken);
                }
            }

            progress.Report(100);
            _logger.LogInformation("StrmTool - Task complete. Successfully processed {Processed}/{Total} strm files.", 
                processed, total);
        }

        public string Category => "Library";
        public string Key => "StrmToolTask";
        public string Description => "Extract media technical information (codec, resolution, subtitles) from strm files";
        public string Name => "Extract Strm Media Info";

        public IEnumerable<TaskTriggerInfo> GetDefaultTriggers()
        {
            return new[]
            {
                new TaskTriggerInfo
                {
                    Type = TaskTriggerInfo.TriggerDaily,
                    TimeOfDayTicks = TimeSpan.FromHours(3).Ticks,
                    MaxRuntimeTicks = TimeSpan.FromHours(24).Ticks
                }
            };
        }
    }
}
