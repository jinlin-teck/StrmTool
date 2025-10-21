using MediaBrowser.Common.Configuration;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.MediaEncoding;
using MediaBrowser.Model.IO;
using MediaBrowser.Model.Logging;
using MediaBrowser.Model.Tasks;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using MediaBrowser.Model.MediaInfo;
using MediaBrowser.Model.Dto;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Model.Configuration;
using MediaBrowser.Controller.Providers;
using MediaBrowser.Model.Entities;
using MediaBrowser.Controller.IO;

namespace StrmTool
{
    public class ExtractTask : IScheduledTask
    {
        private readonly ILogger _logger;
        private readonly ILibraryManager _libraryManager;
        private readonly IFileSystem _fileSystem;
        private readonly ILibraryMonitor _libraryMonitor;
        private readonly IMediaProbeManager _mediaProbeManager;

        public ExtractTask(ILibraryManager libraryManager, 
            ILogger logger, 
            IFileSystem fileSystem,
            ILibraryMonitor libraryMonitor,
            IMediaProbeManager prob)
        {
            _libraryManager = libraryManager;
            _logger = logger;
            _fileSystem = fileSystem;
            _libraryMonitor = libraryMonitor;
            _mediaProbeManager = prob;

            // 在任务创建时注册事件处理器
            RegisterEventHandlers();
        }

        public async Task Execute(CancellationToken cancellationToken, IProgress<double> progress)
        {
            _logger.Info("StrmTool - Starting strm file scan...");

            // 使用递归查询获取所有strm文件
            var query = new InternalItemsQuery
            {
                HasPath = true,
                Recursive = true,
                ExcludeItemTypes = new string[] { "Folder", "CollectionFolder", "UserView", "Series", "Season", "Trailer", "Playlist" }
            };

            var allItems = _libraryManager.GetItemList(query)
                .Where(i => !string.IsNullOrEmpty(i.Path) && 
                           i.Path.EndsWith(".strm", StringComparison.InvariantCultureIgnoreCase))
                .ToList();

            _logger.Info($"StrmTool - Found {allItems.Count} strm files in library");

            // 过滤出需要处理的文件：没有视频流或没有音频流的文件
            var strmItems = allItems
                .Where(i =>
                {
                    var streams = i.GetMediaStreams() ?? new List<MediaStream>();
                    bool hasVideo = streams.Any(s => s.Type == MediaStreamType.Video);
                    bool hasAudio = streams.Any(s => s.Type == MediaStreamType.Audio);
                    return !hasVideo || !hasAudio;
                })
                .ToList();

            _logger.Info($"StrmTool - {strmItems.Count} strm files need metadata refresh");

            if (strmItems.Count == 0)
            {
                progress.Report(100);
                _logger.Info("StrmTool - Nothing to process, task complete.");
                return;
            }

            // 使用Emby兼容的MetadataRefreshOptions设置
            var directoryService = new DirectoryService(_fileSystem);
            var options = new MetadataRefreshOptions(directoryService)
            {
                EnableRemoteContentProbe = true,
                ReplaceAllMetadata = true,
                MetadataRefreshMode = MetadataRefreshMode.ValidationOnly,
                EnableThumbnailImageExtraction = false,
                ImageRefreshMode = MetadataRefreshMode.ValidationOnly, // 使用ValidationOnly而不是None
                ReplaceAllImages = false
            };

            int processed = 0;
            int total = strmItems.Count;

            // 顺序处理，避免触发远程服务器风控
            foreach (var item in strmItems)
            {
                if (cancellationToken.IsCancellationRequested)
                {
                    _logger.Info("StrmTool - Task was cancelled");
                    break;
                }

                try
                {
                    _logger.Debug($"StrmTool - Processing {item.Name}");

                    var beforeStreams = item.GetMediaStreams() ?? new List<MediaStream>();
                    _logger.Debug($"StrmTool - Before: {beforeStreams.Count} streams");

                    var result = await item.RefreshMetadata(options, cancellationToken);

                    var afterStreams = item.GetMediaStreams() ?? new List<MediaStream>();
                    bool hasVideo = afterStreams.Any(s => s.Type == MediaStreamType.Video);
                    bool hasAudio = afterStreams.Any(s => s.Type == MediaStreamType.Audio);

                    _logger.Info($"StrmTool - {item.Name}: Refresh done. Streams {beforeStreams.Count}→{afterStreams.Count}. Video:{hasVideo}, Audio:{hasAudio}");

                    if (!hasVideo || !hasAudio)
                    {
                        _logger.Warn($"StrmTool - {item.Name} may still lack full media info");
                    }
                }
                catch (Exception ex)
                {
                    _logger.Error($"StrmTool - Error processing {item.Name} ({item.Path}): {ex.Message}");
                }

                processed++;
                double percent = (double)processed / total * 100;
                progress.Report(percent);

                // 添加延迟，避免对远程服务器造成压力
                if (processed < total)
                {
                    await Task.Delay(1000, cancellationToken);
                }
            }

            progress.Report(100);
            _logger.Info($"StrmTool - Task complete. Successfully processed {processed}/{total} strm files.");
        }

        public string Category => "Strm Tool";
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

        private void RegisterEventHandlers()
        {
            try
            {
                var eventHandler = new ItemAddedEventHandler(_logger, _libraryManager, _fileSystem);
                _libraryManager.ItemAdded += eventHandler.OnItemAdded;
                _logger.Info("StrmTool - Item added event handler registered successfully");
            }
            catch (Exception ex)
            {
                _logger.Error($"StrmTool - Error registering event handlers: {ex.Message}");
            }
        }
    }
}
