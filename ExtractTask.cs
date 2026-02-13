using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Providers;
using MediaBrowser.Model.IO;
using MediaBrowser.Model.Tasks;
using MediaBrowser.Model.Entities;
using Jellyfin.Data.Enums;

namespace StrmTool
{
    public class ExtractTask : IScheduledTask
    {
        private readonly ILogger<ExtractTask> _logger;
        private readonly ILibraryManager _libraryManager;
        private readonly IFileSystem _fileSystem;
        private readonly PluginConfiguration _config;

        public ExtractTask(
            ILibraryManager libraryManager,
            ILogger<ExtractTask> logger,
            IFileSystem fileSystem)
        {
            _libraryManager = libraryManager;
            _logger = logger;
            _fileSystem = fileSystem;
            _config = Plugin.Instance?.Configuration ?? new PluginConfiguration();
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

            var dirs = new Stack<string>();
            dirs.Push(directoryPath);

            while (dirs.Count > 0)
            {
                if (cancellationToken.IsCancellationRequested)
                    break;

                var current = dirs.Pop();
                try
                {
                    // 使用 EnumerateFiles/EnumerateDirectories 以减少一次性内存占用
                    IEnumerable<string> files = Enumerable.Empty<string>();
                    try
                    {
                        files = Directory.EnumerateFiles(current, "*.strm");
                    }
                    catch (Exception ex)
                    {
                        _logger.LogError(ex, "StrmTool - Error enumerating files in {Directory}", current);
                    }

                    foreach (var strmPath in files)
                    {
                        if (cancellationToken.IsCancellationRequested)
                            break;

                        try
                        {
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

                    IEnumerable<string> subDirs = Enumerable.Empty<string>();
                    try
                    {
                        subDirs = Directory.EnumerateDirectories(current);
                    }
                    catch (Exception ex)
                    {
                        _logger.LogError(ex, "StrmTool - Error enumerating directories in {Directory}", current);
                    }

                    foreach (var sub in subDirs)
                    {
                        if (cancellationToken.IsCancellationRequested)
                            break;
                        dirs.Push(sub);
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "StrmTool - Error scanning directory {Directory}", current);
                }
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
                    await Task.Delay(_config.RefreshDelayMs, cancellationToken);
                }
            }

            progress.Report(100);
            _logger.LogInformation("StrmTool - Task complete. Successfully processed {Processed}/{Total} strm files.", 
                processed, total);
        }

        /// <summary>
        /// 兼容的方法来获取媒体流，带反射结果缓存以减少每次调用的开销
        /// </summary>
        private static readonly ConcurrentDictionary<Type, Func<BaseItem, List<MediaStream>>> _mediaStreamResolvers
            = new ConcurrentDictionary<Type, Func<BaseItem, List<MediaStream>>>();

        private List<MediaStream> GetItemMediaStreams(BaseItem item)
        {
            try
            {
                var type = item.GetType();

                var resolver = _mediaStreamResolvers.GetOrAdd(type, t =>
                {
                    // 尝试属性优先
                    var prop = t.GetProperty("MediaStreams", BindingFlags.Public | BindingFlags.Instance);
                    if (prop != null && typeof(IEnumerable<MediaStream>).IsAssignableFrom(prop.PropertyType))
                    {
                        return new Func<BaseItem, List<MediaStream>>(bi =>
                        {
                            var val = prop.GetValue(bi);
                            if (val is List<MediaStream> list) return list;
                            if (val is IEnumerable<MediaStream> enumv) return enumv.ToList();
                            return new List<MediaStream>();
                        });
                    }

                    // 尝试方法
                    var method = t.GetMethod("GetMediaStreams", BindingFlags.Public | BindingFlags.Instance);
                    if (method != null && typeof(IEnumerable<MediaStream>).IsAssignableFrom(method.ReturnType))
                    {
                        return new Func<BaseItem, List<MediaStream>>(bi =>
                        {
                            var res = method.Invoke(bi, null);
                            if (res is List<MediaStream> list) return list;
                            if (res is IEnumerable<MediaStream> enumv) return enumv.ToList();
                            return new List<MediaStream>();
                        });
                    }

                    // Fallback: empty
                    return new Func<BaseItem, List<MediaStream>>(bi => new List<MediaStream>());
                });

                return resolver(item);
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