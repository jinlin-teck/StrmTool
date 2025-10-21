using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Model.Logging;
using MediaBrowser.Model.IO;
using MediaBrowser.Controller.IO;
using MediaBrowser.Controller.Providers;
using MediaBrowser.Model.Configuration;
using MediaBrowser.Model.Entities;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

namespace StrmTool
{
    public class ItemAddedEventHandler
    {
        private readonly ILogger _logger;
        private readonly ILibraryManager _libraryManager;
        private readonly IFileSystem _fileSystem;

        public ItemAddedEventHandler(ILogger logger, ILibraryManager libraryManager, IFileSystem fileSystem)
        {
            _logger = logger;
            _libraryManager = libraryManager;
            _fileSystem = fileSystem;
        }

        public async void OnItemAdded(object sender, ItemChangeEventArgs e)
        {
            try
            {
                // 只处理strm文件
                if (e.Item == null || string.IsNullOrEmpty(e.Item.Path) || 
                    !e.Item.Path.EndsWith(".strm", StringComparison.InvariantCultureIgnoreCase))
                {
                    return;
                }

                _logger.Info($"StrmTool - New strm file detected: {e.Item.Name}");

                // 检查是否已经有完整的媒体信息
                var streams = e.Item.GetMediaStreams() ?? new List<MediaStream>();
                bool hasVideo = streams.Any(s => s.Type == MediaStreamType.Video);
                bool hasAudio = streams.Any(s => s.Type == MediaStreamType.Audio);

                // 如果已经有完整的媒体信息，则不需要处理
                if (hasVideo && hasAudio)
                {
                    _logger.Debug($"StrmTool - {e.Item.Name} already has complete media info, skipping");
                    return;
                }

                _logger.Info($"StrmTool - Processing new strm file: {e.Item.Name}");

                // 使用Emby兼容的MetadataRefreshOptions设置
                var directoryService = new DirectoryService(_fileSystem);
                var options = new MetadataRefreshOptions(directoryService)
                {
                    EnableRemoteContentProbe = true,
                    ReplaceAllMetadata = true,
                    MetadataRefreshMode = MetadataRefreshMode.ValidationOnly,
                    EnableThumbnailImageExtraction = false,
                    ImageRefreshMode = MetadataRefreshMode.ValidationOnly,
                    ReplaceAllImages = false
                };

                try
                {
                    var beforeStreams = e.Item.GetMediaStreams() ?? new List<MediaStream>();
                    _logger.Debug($"StrmTool - Before refresh: {beforeStreams.Count} streams");

                    // 刷新元数据
                    await e.Item.RefreshMetadata(options, default);

                    var afterStreams = e.Item.GetMediaStreams() ?? new List<MediaStream>();
                    bool hasVideoAfter = afterStreams.Any(s => s.Type == MediaStreamType.Video);
                    bool hasAudioAfter = afterStreams.Any(s => s.Type == MediaStreamType.Audio);

                    _logger.Info($"StrmTool - {e.Item.Name}: Real-time processing done. Streams {beforeStreams.Count}→{afterStreams.Count}. Video:{hasVideoAfter}, Audio:{hasAudioAfter}");

                    if (!hasVideoAfter || !hasAudioAfter)
                    {
                        _logger.Warn($"StrmTool - {e.Item.Name} may still lack full media info, will be processed by scheduled task");
                    }
                }
                catch (Exception ex)
                {
                    _logger.Error($"StrmTool - Error processing new strm file {e.Item.Name} ({e.Item.Path}): {ex.Message}");
                }
            }
            catch (Exception ex)
            {
                _logger.Error($"StrmTool - Error in item added event handler: {ex.Message}");
            }
        }
    }
}
