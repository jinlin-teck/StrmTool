using MediaBrowser.Controller.Library;
using MediaBrowser.Model.IO;
using MediaBrowser.Model.Tasks;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Providers;
using Jellyfin.Data.Enums;
using MediaBrowser.Model.Entities;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Entities.TV;

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

            try
            {
                // 方法1：使用更安全的方式获取所有库根目录
                var rootFolders = _libraryManager.GetVirtualFolders()
                    .SelectMany(vf => vf.Locations)
                    .Distinct()
                    .ToList();

                _logger.LogInformation("StrmTool - Found {Count} library root folders", rootFolders.Count);

                var allStrmFiles = new List<BaseItem>();

                // 遍历每个库目录，手动查找 strm 文件
                foreach (var rootFolder in rootFolders)
                {
                    if (cancellationToken.IsCancellationRequested)
                        break;

                    try
                    {
                        _logger.LogDebug("StrmTool - Scanning library folder: {Folder}", rootFolder);
                        var strmFilesInFolder = FindStrmFilesInDirectory(rootFolder, cancellationToken);
                        allStrmFiles.AddRange(strmFilesInFolder);
                    }
                    catch (Exception ex)
                    {
                        _logger.LogError(ex, "StrmTool - Error scanning folder {Folder}", rootFolder);
                    }
                }

                _logger.LogInformation("StrmTool - Found {Count} strm files in library", allStrmFiles.Count);

                // 过滤需要刷新的文件
                var strmItems = new List<BaseItem>();
                
                foreach (var item in allStrmFiles)
                {
                    if (cancellationToken.IsCancellationRequested)
                        break;

                    try
                    {
                        var mediaStreams = GetItemMediaStreams(item);
                        bool hasVideo = mediaStreams.Any(s => s.Type == MediaStreamType.Video);
                        bool hasAudio = mediaStreams.Any(s => s.Type == MediaStreamType.Audio);
                        
                        if (!hasVideo || !hasAudio)
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
        /// 在目录中递归查找 strm 文件
        /// </summary>
        private List<BaseItem> FindStrmFilesInDirectory(string directoryPath, CancellationToken cancellationToken)
        {
            var strmFiles = new List<BaseItem>();

            try
            {
                // 查找当前目录中的 strm 文件
                var strmPaths = Directory.GetFiles(directoryPath, "*.strm", SearchOption.TopDirectoryOnly);
                
                foreach (var strmPath in strmPaths)
                {
                    if (cancellationToken.IsCancellationRequested)
                        break;

                    try
                    {
                        // 使用更安全的方式获取 BaseItem
                        var item = _libraryManager.FindByPath(strmPath, false);
                        if (item != null)
                        {
                            strmFiles.Add(item);
                        }
                        else
                        {
                            _logger.LogWarning("StrmTool - Could not find library item for path: {Path}", strmPath);
                        }
                    }
                    catch (Exception ex)
                    {
                        _logger.LogError(ex, "StrmTool - Error processing file {Path}", strmPath);
                    }
                }

                // 递归查找子目录
                var subDirectories = Directory.GetDirectories(directoryPath);
                foreach (var subDir in subDirectories)
                {
                    if (cancellationToken.IsCancellationRequested)
                        break;

                    try
                    {
                        var subDirFiles = FindStrmFilesInDirectory(subDir, cancellationToken);
                        strmFiles.AddRange(subDirFiles);
                    }
                    catch (Exception ex)
                    {
                        _logger.LogError(ex, "StrmTool - Error scanning subdirectory {Directory}", subDir);
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "StrmTool - Error scanning directory {Directory}", directoryPath);
            }

            return strmFiles;
        }

        /// <summary>
        /// 处理 strm 文件
        /// </summary>
        private async Task ProcessStrmFiles(List<BaseItem> strmItems, IProgress<double> progress, CancellationToken cancellationToken)
        {
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

                    var beforeStreams = GetItemMediaStreams(item);
                    _logger.LogTrace("StrmTool - Before: {Count} streams", beforeStreams.Count);

                    var result = await item.RefreshMetadata(options, cancellationToken);

                    var afterStreams = GetItemMediaStreams(item);
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
                if (processed < total)
                {
                    await Task.Delay(1000, cancellationToken);
                }
            }

            progress.Report(100);
            _logger.LogInformation("StrmTool - Task complete. Successfully processed {Processed}/{Total} strm files.", 
                processed, total);
        }

        /// <summary>
        /// 兼容的方法来获取媒体流
        /// </summary>
        private List<MediaStream> GetItemMediaStreams(BaseItem item)
        {
            try
            {
                // 尝试直接访问 MediaStreams 属性
                var mediaStreamsProperty = item.GetType().GetProperty("MediaStreams");
                if (mediaStreamsProperty != null)
                {
                    var value = mediaStreamsProperty.GetValue(item);
                    if (value is List<MediaStream> streams)
                    {
                        return streams;
                    }
                    if (value is IEnumerable<MediaStream> enumerable)
                    {
                        return enumerable.ToList();
                    }
                }

                // 尝试使用 GetMediaStreams 方法
                var getMediaStreamsMethod = item.GetType().GetMethod("GetMediaStreams");
                if (getMediaStreamsMethod != null)
                {
                    var result = getMediaStreamsMethod.Invoke(item, null);
                    if (result is List<MediaStream> streams)
                    {
                        return streams;
                    }
                    if (result is IEnumerable<MediaStream> enumerable)
                    {
                        return enumerable.ToList();
                    }
                }

                _logger.LogWarning("StrmTool - Could not get media streams for {ItemType}", item.GetType().Name);
                return new List<MediaStream>();
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "StrmTool - Error getting media streams for {ItemType}", item.GetType().Name);
                return new List<MediaStream>();
            }
        }

        public string Category => "Strm Tool";
        public string Key => "StrmToolTask";
        public string Description => "Extract media technical information (codec, resolution, subtitles) from strm files";
        public string Name => "Extract Strm Media Info";

        public IEnumerable<TaskTriggerInfo> GetDefaultTriggers()
        {
            return Array.Empty<TaskTriggerInfo>();
        }
    }
}