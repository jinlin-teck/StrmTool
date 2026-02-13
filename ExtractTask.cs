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
using MediaBrowser.Controller.MediaEncoding;
using MediaBrowser.Controller.Persistence;
using MediaBrowser.Model.Dto;
using MediaBrowser.Model.Dlna;
using MediaBrowser.Model.IO;
using MediaBrowser.Model.MediaInfo;
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
        private readonly IMediaEncoder _mediaEncoder;
        private readonly IMediaStreamRepository _mediaStreamRepository;
        private readonly PluginConfiguration _config;
        private readonly MediaInfoCache _mediaCache;
        private LibraryScanListener _scanListener;

        public ExtractTask(
            ILibraryManager libraryManager,
            ILogger<ExtractTask> logger,
            IFileSystem fileSystem,
            IMediaEncoder mediaEncoder,
            IMediaStreamRepository mediaStreamRepository)
        {
            _libraryManager = libraryManager;
            _logger = logger;
            _fileSystem = fileSystem;
            _mediaEncoder = mediaEncoder;
            _mediaStreamRepository = mediaStreamRepository;
            
            if (Plugin.Instance == null)
            {
                _logger.LogWarning("StrmTool - Plugin instance not found, using default configuration");
                _config = new PluginConfiguration();
            }
            else
            {
                _config = Plugin.Instance.Configuration;
            }
            
            _mediaCache = new MediaInfoCache(_logger, _config.CacheExpirationDays);

            // 初始化库监听器
            if (_config.EnableAutoExtract)
            {
                try
                {
                    _scanListener = new LibraryScanListener(_libraryManager, _logger, this, _config);
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "StrmTool - Failed to initialize library scan listener");
                }
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

                    // 检查缓存
                    if (_config.EnableMediaInfoCache && _mediaCache.TryGetCachedMediaStreams(item.Path, out var cachedStreams))
                    {
                        _mediaStreamRepository.SaveMediaStreams(item.Id, cachedStreams, cancellationToken);
                        _logger.LogInformation("StrmTool - {Name}: Used cached media info ({Count} streams)", 
                            item.Name, cachedStreams.Count);
                    }
                    else
                    {
                        // 直接探测媒体流，不调用任何提供商或图像更新
                        await ProbeStrmMediaStreams(item, cancellationToken);
                    }

                    var afterStreams = GetItemMediaStreams(item);
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
        /// 直接探测 STRM 文件的媒体流，避免调用元数据提供商
        /// </summary>
        private async Task ProbeStrmMediaStreams(BaseItem item, CancellationToken cancellationToken)
        {
            try
            {
                // 读取 STRM 文件内容（通常是远程媒体的 URL 或路径）
                string strmContent = File.ReadAllText(item.Path).Trim();

                if (string.IsNullOrWhiteSpace(strmContent))
                {
                    _logger.LogWarning("StrmTool - STRM file is empty: {Path}", item.Path);
                    return;
                }

                // 确定媒体类型
                bool isAudio = item.MediaType == MediaType.Audio;
                
                // 使用 MediaEncoder 直接探测远程媒体文件
                // 这会调用 FFprobe 来获取媒体流信息，不涉及任何元数据提供商
                var mediaInfo = await _mediaEncoder.GetMediaInfo(
                    new MediaInfoRequest
                    {
                        MediaSource = new MediaSourceInfo
                        {
                            Path = strmContent,
                            Protocol = GetProtocolFromPath(strmContent),
                        },
                        MediaType = isAudio ? DlnaProfileType.Audio : DlnaProfileType.Video,
                        ExtractChapters = false,
                    },
                    cancellationToken);

                // 将探测到的媒体流保存到数据库
                if (mediaInfo?.MediaStreams != null && mediaInfo.MediaStreams.Count > 0)
                {
                    _mediaStreamRepository.SaveMediaStreams(item.Id, mediaInfo.MediaStreams, cancellationToken);
                    _logger.LogDebug("StrmTool - Successfully saved {Count} media streams for {Name}", 
                        mediaInfo.MediaStreams.Count, item.Name);

                        // 保存缓存
                        if (_config.EnableMediaInfoCache)
                        {
                            await _mediaCache.SaveCacheAsync(item.Path, mediaInfo.MediaStreams, strmContent, cancellationToken);
                        }
                }
                else
                {
                    _logger.LogDebug("StrmTool - No media streams found for {Name}", item.Name);
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "StrmTool - Error probing STRM content for {Name}", item.Name);
                throw;
            }
        }

        /// <summary>
        /// 提取单个 strm 文件的媒体信息（用于自动提取）
        /// </summary>
        public async Task ExtractSingleItemAsync(BaseItem item, CancellationToken cancellationToken)
        {
            try
            {
                _logger.LogInformation("StrmTool - Auto-extracting media info for new strm file: {Name}", item.Name);

                var beforeStreams = GetItemMediaStreams(item);
                _logger.LogDebug("StrmTool - Before: {Count} streams", beforeStreams.Count);

                // 检查是否已有完整的媒体流
                bool hasVideo = beforeStreams.Any(s => s.Type == MediaStreamType.Video);
                bool hasAudio = beforeStreams.Any(s => s.Type == MediaStreamType.Audio);

                if (hasVideo && hasAudio)
                {
                    _logger.LogInformation("StrmTool - {Name} already has complete media info, skipping", item.Name);
                    return;
                }

                // 检查缓存
                if (_config.EnableMediaInfoCache && _mediaCache.TryGetCachedMediaStreams(item.Path, out var cachedStreams))
                {
                    _mediaStreamRepository.SaveMediaStreams(item.Id, cachedStreams, cancellationToken);
                    _logger.LogInformation("StrmTool - Auto-extract: {Name} using cached media info", item.Name);
                }
                else
                {
                    // 执行探测
                    await ProbeStrmMediaStreams(item, cancellationToken);
                }

                var afterStreams = GetItemMediaStreams(item);
                _logger.LogInformation(
                    "StrmTool - Auto-extract complete for {Name}. Streams {Before}→{After}",
                    item.Name,
                    beforeStreams.Count,
                    afterStreams.Count
                );
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "StrmTool - Error in auto-extract for {Name}", item.Name);
            }
        }

        /// <summary>
        /// 根据路径判断协议类型
        /// </summary>
        private MediaProtocol GetProtocolFromPath(string path)
        {
            if (path.StartsWith("http://", StringComparison.OrdinalIgnoreCase) ||
                path.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
            {
                return MediaProtocol.Http;
            }
            else if (path.StartsWith("rtmp://", StringComparison.OrdinalIgnoreCase))
            {
                return MediaProtocol.Rtmp;
            }
            else if (path.StartsWith("rtsp://", StringComparison.OrdinalIgnoreCase))
            {
                return MediaProtocol.Rtsp;
            }
            else if (path.StartsWith("ftp://", StringComparison.OrdinalIgnoreCase))
            {
                return MediaProtocol.Ftp;
            }
            else
            {
                // 本地路径
                return MediaProtocol.File;
            }
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