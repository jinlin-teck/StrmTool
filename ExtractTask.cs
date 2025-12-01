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
using MediaBrowser.Controller.Persistence;
using MediaBrowser.Controller.MediaEncoding;
using MediaBrowser.Model.Dlna;
using MediaBrowser.Model.Dto;

namespace StrmTool
{
    /// <summary>
    /// 扫描并更新 STRM 文件的媒体信息和文件大小
    /// </summary>
    public class ExtractTask : IScheduledTask
    {
        private readonly ILogger<ExtractTask> _logger;
        private readonly ILibraryManager _libraryManager;
        private readonly IFileSystem _fileSystem;
        private readonly IItemRepository _itemRepository;
        private readonly IMediaEncoder _mediaEncoder;

        public ExtractTask(
            ILibraryManager libraryManager,
            ILogger<ExtractTask> logger,
            IFileSystem fileSystem,
            IItemRepository itemRepository,
            IMediaEncoder mediaEncoder)
        {
            _libraryManager = libraryManager;
            _logger = logger;
            _fileSystem = fileSystem;
            _itemRepository = itemRepository;
            _mediaEncoder = mediaEncoder;
        }

        public string Category => "Strm Tool";
        public string Key => "StrmToolTask";
        public string Description => "Extract media info and update file sizes for strm files";
        public string Name => "Extract Strm Media Info";
        public IEnumerable<TaskTriggerInfo> GetDefaultTriggers() => Array.Empty<TaskTriggerInfo>();

        public async Task ExecuteAsync(IProgress<double> progress, CancellationToken cancellationToken)
        {
            var version = System.Reflection.Assembly.GetExecutingAssembly().GetName().Version;
            _logger.LogInformation("StrmTool v{Version} - Starting scan...", version);

            try
            {
                // 1. 获取所有 strm 文件
                var allStrmFiles = GetAllStrmFiles(cancellationToken);
                _logger.LogInformation("Found {Count} strm files", allStrmFiles.Count);

                if (allStrmFiles.Count == 0)
                {
                    progress.Report(100);
                    return;
                }

                // 2. 分类文件
                var (needRefresh, needSizeUpdate) = ClassifyFiles(allStrmFiles, cancellationToken);
                _logger.LogInformation("{Refresh} need metadata refresh, {Size} need size update",
                    needRefresh.Count, needSizeUpdate.Count);

                if (needRefresh.Count == 0 && needSizeUpdate.Count == 0)
                {
                    progress.Report(100);
                    _logger.LogInformation("All files are up to date");
                    return;
                }

                // 3. 处理文件
                await ProcessFiles(needRefresh, needSizeUpdate, progress, cancellationToken);

                _logger.LogInformation("Scan completed successfully");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Fatal error during scan");
                throw;
            }
        }

        /// <summary>
        /// 获取所有 strm 文件
        /// </summary>
        private List<BaseItem> GetAllStrmFiles(CancellationToken cancellationToken)
        {
            var strmFiles = new List<BaseItem>();
            var rootFolders = _libraryManager.GetVirtualFolders()
                .SelectMany(vf => vf.Locations)
                .Distinct()
                .ToList();

            _logger.LogInformation("Scanning {Count} library folders", rootFolders.Count);

            foreach (var folder in rootFolders)
            {
                if (cancellationToken.IsCancellationRequested) break;

                try
                {
                    var files = FindStrmFilesRecursive(folder, cancellationToken);
                    strmFiles.AddRange(files);
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Error scanning folder: {Folder}", folder);
                }
            }

            return strmFiles;
        }

        /// <summary>
        /// 递归查找 strm 文件
        /// </summary>
        private List<BaseItem> FindStrmFilesRecursive(string directory, CancellationToken cancellationToken)
        {
            var result = new List<BaseItem>();

            try
            {
                // 查找当前目录的 strm 文件
                foreach (var file in Directory.GetFiles(directory, "*.strm"))
                {
                    if (cancellationToken.IsCancellationRequested) break;

                    var item = _libraryManager.FindByPath(file, false);
                    if (item != null)
                    {
                        result.Add(item);
                    }
                }

                // 递归子目录
                foreach (var subDir in Directory.GetDirectories(directory))
                {
                    if (cancellationToken.IsCancellationRequested) break;
                    result.AddRange(FindStrmFilesRecursive(subDir, cancellationToken));
                }
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Error scanning directory: {Dir}", directory);
            }

            return result;
        }

        /// <summary>
        /// 直接探测实际媒体文件并更新流信息
        /// </summary>
        private async Task ProbeActualMediaFile(BaseItem item, CancellationToken cancellationToken)
        {
            try
            {
                // 读取 STRM 文件内容获取实际媒体文件路径
                if (!File.Exists(item.Path) || !item.Path.EndsWith(".strm", StringComparison.OrdinalIgnoreCase))
                {
                    _logger.LogWarning("{Name}: Not a valid STRM file", item.Name);
                    return;
                }

                string targetPath = File.ReadAllText(item.Path).Trim();
                if (!File.Exists(targetPath))
                {
                    _logger.LogWarning("{Name}: Target file not found: {Path}", item.Name, targetPath);
                    return;
                }

                _logger.LogDebug("{Name}: Probing actual media file: {Path}", item.Name, targetPath);

                // 使用 MediaEncoder 探测实际媒体文件
                var mediaInfo = await _mediaEncoder.GetMediaInfo(new MediaBrowser.Controller.MediaEncoding.MediaInfoRequest
                {
                    MediaSource = new MediaSourceInfo
                    {
                        Path = targetPath,
                        Protocol = MediaBrowser.Model.MediaInfo.MediaProtocol.File
                    },
                    MediaType = DlnaProfileType.Video,
                    ExtractChapters = false
                }, cancellationToken);

                if (mediaInfo == null)
                {
                    _logger.LogWarning("{Name}: Failed to probe media file", item.Name);
                    return;
                }

                // 更新媒体流信息
                if (mediaInfo.MediaStreams != null && mediaInfo.MediaStreams.Count > 0)
                {
                    // 1. 直接设置 MediaStreams 属性
                    try
                    {
                        var mediaStreamsProperty = item.GetType().GetProperty("MediaStreams",
                            System.Reflection.BindingFlags.Public |
                            System.Reflection.BindingFlags.Instance);

                        if (mediaStreamsProperty != null)
                        {
                            _logger.LogInformation("{Name}: 🔍 MediaStreams property found, CanWrite={CanWrite}",
                                item.Name, mediaStreamsProperty.CanWrite);

                            if (mediaStreamsProperty.CanWrite)
                            {
                                mediaStreamsProperty.SetValue(item, mediaInfo.MediaStreams);
                                _logger.LogInformation("{Name}: ✓ Set MediaStreams property with {Count} streams",
                                    item.Name, mediaInfo.MediaStreams.Count);

                                // 验证设置是否成功
                                var verifyStreams = mediaStreamsProperty.GetValue(item) as IEnumerable<MediaStream>;
                                var verifyCount = verifyStreams?.Count() ?? 0;
                                _logger.LogInformation("{Name}: 🔍 Verification - MediaStreams now has {Count} streams",
                                    item.Name, verifyCount);
                            }
                            else
                            {
                                _logger.LogWarning("{Name}: ⚠️ MediaStreams property is read-only!", item.Name);
                            }
                        }
                        else
                        {
                            _logger.LogWarning("{Name}: ⚠️ MediaStreams property not found!", item.Name);
                        }
                    }
                    catch (Exception ex)
                    {
                        _logger.LogError(ex, "{Name}: ❌ Error setting MediaStreams property", item.Name);
                    }

                    // 2. 更新 MediaSources
                    var mediaSources = item.GetMediaSources(false);
                    MediaSourceInfo mediaSource;

                    if (mediaSources.Count > 0)
                    {
                        mediaSource = mediaSources[0];
                    }
                    else
                    {
                        mediaSource = new MediaSourceInfo
                        {
                            Id = item.Id.ToString(),
                            Path = item.Path,
                            Protocol = MediaBrowser.Model.MediaInfo.MediaProtocol.File,
                            Type = MediaBrowser.Model.Dto.MediaSourceType.Default
                        };
                    }

                    mediaSource.MediaStreams = mediaInfo.MediaStreams;

                    // 3. 设置运行时长
                    if (mediaInfo.RunTimeTicks.HasValue)
                    {
                        mediaSource.RunTimeTicks = mediaInfo.RunTimeTicks;
                        item.RunTimeTicks = mediaInfo.RunTimeTicks;
                    }

                    // 4. 保存 MediaSources
                    try
                    {
                        var mediaSourcesProperty = item.GetType().GetProperty("MediaSources",
                            System.Reflection.BindingFlags.Public |
                            System.Reflection.BindingFlags.Instance);

                        if (mediaSourcesProperty != null && mediaSourcesProperty.CanWrite)
                        {
                            mediaSourcesProperty.SetValue(item, new List<MediaSourceInfo> { mediaSource });
                            _logger.LogDebug("{Name}: Set MediaSources property", item.Name);
                        }
                    }
                    catch (Exception ex)
                    {
                        _logger.LogDebug(ex, "{Name}: Could not set MediaSources property", item.Name);
                    }

                    // 5. 保存到数据库
                    await _libraryManager.UpdateItemAsync(item, item.GetParent(), ItemUpdateType.MetadataEdit, cancellationToken);
                    _itemRepository.SaveItems(new[] { item }, cancellationToken);

                    _logger.LogInformation("{Name}: Probed {VideoCount} video, {AudioCount} audio, {SubCount} subtitle streams",
                        item.Name,
                        mediaInfo.MediaStreams.Count(s => s.Type == MediaStreamType.Video),
                        mediaInfo.MediaStreams.Count(s => s.Type == MediaStreamType.Audio),
                        mediaInfo.MediaStreams.Count(s => s.Type == MediaStreamType.Subtitle));
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "{Name}: Error probing actual media file", item.Name);
            }
        }

        /// <summary>
        /// 检查文件是否需要处理（基于元数据完整性）
        /// </summary>
        private bool NeedsProcessing(BaseItem item)
        {
            try
            {
                // 简单策略：检查文件是否有正确的元数据
                var streams = GetMediaStreams(item);
                bool hasVideo = streams.Any(s => s.Type == MediaStreamType.Video);
                bool hasAudio = streams.Any(s => s.Type == MediaStreamType.Audio);
                bool hasCorrectSize = item.Size.HasValue && item.Size.Value >= 1024;

                // 如果已经有正确的元数据，跳过
                if (hasVideo && hasAudio && hasCorrectSize)
                {
                    return false;
                }

                return true;
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Error checking if needs processing: {Name}", item.Name);
                return true; // 出错时默认需要处理
            }
        }

        /// <summary>
        /// 分类文件：需要刷新元数据的 vs 只需要更新大小的
        /// </summary>
        private (List<BaseItem> needRefresh, List<BaseItem> needSizeUpdate) ClassifyFiles(
            List<BaseItem> files, CancellationToken cancellationToken)
        {
            var needRefresh = new List<BaseItem>();
            var needSizeUpdate = new List<BaseItem>();
            int correctSize = 0, smallSize = 0, nullSize = 0, skippedCache = 0;

            foreach (var item in files)
            {
                if (cancellationToken.IsCancellationRequested) break;

                try
                {
                    // 检查是否需要处理（基于缓存）
                    if (!NeedsProcessing(item))
                    {
                        skippedCache++;
                        continue;
                    }

                    var streams = GetMediaStreams(item);
                    bool hasVideo = streams.Any(s => s.Type == MediaStreamType.Video);
                    bool hasAudio = streams.Any(s => s.Type == MediaStreamType.Audio);
                    bool hasCorrectSize = item.Size.HasValue && item.Size.Value >= 1024;

                    // 统计
                    if (!item.Size.HasValue) nullSize++;
                    else if (item.Size.Value < 1024) smallSize++;
                    else correctSize++;

                    // 分类
                    if (!hasVideo || !hasAudio)
                    {
                        needRefresh.Add(item);
                    }
                    else if (!hasCorrectSize)
                    {
                        needSizeUpdate.Add(item);
                    }
                    // 文件已经正确，无需处理
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Error checking file: {Name}", item.Name);
                    needRefresh.Add(item);
                }
            }

            _logger.LogInformation("Size stats: {Correct} correct, {Small} small (<1KB), {Null} null, {Skipped} skipped (cached)",
                correctSize, smallSize, nullSize, skippedCache);

            return (needRefresh, needSizeUpdate);
        }

        /// <summary>
        /// 处理文件
        /// </summary>
        private async Task ProcessFiles(List<BaseItem> needRefresh, List<BaseItem> needSizeUpdate,
            IProgress<double> progress, CancellationToken cancellationToken)
        {
            int total = needRefresh.Count + needSizeUpdate.Count;
            int processed = 0;

            // 处理需要刷新元数据的文件
            if (needRefresh.Count > 0)
            {
                _logger.LogInformation("Processing {Count} files with metadata refresh", needRefresh.Count);

                foreach (var item in needRefresh)
                {
                    if (cancellationToken.IsCancellationRequested) break;

                    await ProcessSingleFile(item, true, cancellationToken);
                    processed++;
                    progress.Report((double)processed / total * 100);

                    if (processed < total) await Task.Delay(1000, cancellationToken);
                }
            }

            // 处理只需要更新大小的文件
            if (needSizeUpdate.Count > 0)
            {
                _logger.LogInformation("Processing {Count} files with size update only", needSizeUpdate.Count);

                foreach (var item in needSizeUpdate)
                {
                    if (cancellationToken.IsCancellationRequested) break;

                    await ProcessSingleFile(item, false, cancellationToken);
                    processed++;
                    progress.Report((double)processed / total * 100);

                    if (processed < total) await Task.Delay(500, cancellationToken);
                }
            }

            progress.Report(100);
            _logger.LogInformation("Processed {Processed}/{Total} files", processed, total);
        }

        /// <summary>
        /// 处理单个文件
        /// </summary>
        private async Task ProcessSingleFile(BaseItem item, bool refreshMetadata, CancellationToken cancellationToken)
        {
            try
            {
                long? oldSize = item.Size;

                // 直接探测实际媒体文件（如果需要）
                if (refreshMetadata)
                {
                    await ProbeActualMediaFile(item, cancellationToken);
                }

                // 更新文件大小
                bool sizeUpdated = await UpdateFileSize(item, oldSize, cancellationToken);

                // 日志
                var streams = GetMediaStreams(item);
                string sizeInfo = FormatSize(item.Size);

                if (refreshMetadata)
                {
                    _logger.LogInformation("{Name}: 🔍 After processing - GetMediaStreams() returned {Count} streams", item.Name, streams.Count);

                    // 尝试直接读取属性看看
                    try
                    {
                        var mediaStreamsProperty = item.GetType().GetProperty("MediaStreams");
                        if (mediaStreamsProperty != null)
                        {
                            var directStreams = mediaStreamsProperty.GetValue(item) as IEnumerable<MediaStream>;
                            var directCount = directStreams?.Count() ?? 0;
                            _logger.LogInformation("{Name}: 🔍 Direct property read returned {Count} streams", item.Name, directCount);
                        }
                    }
                    catch (Exception ex)
                    {
                        _logger.LogWarning(ex, "{Name}: ⚠️ Could not read MediaStreams property directly", item.Name);
                    }

                    _logger.LogInformation("{Name}: Streams={Count}, Video={Video}, Audio={Audio}, Size={Size}{Updated}",
                        item.Name, streams.Count,
                        streams.Any(s => s.Type == MediaStreamType.Video),
                        streams.Any(s => s.Type == MediaStreamType.Audio),
                        sizeInfo,
                        sizeUpdated ? " [Updated]" : "");
                }
                else
                {
                    _logger.LogInformation("{Name}: Size {Status} {Size}",
                        item.Name,
                        sizeUpdated ? "updated to" : "unchanged",
                        sizeInfo);
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error processing: {Name}", item.Name);
            }
        }



        /// <summary>
        /// 更新文件大小
        /// </summary>
        private async Task<bool> UpdateFileSize(BaseItem item, long? oldSize, CancellationToken cancellationToken)
        {
            try
            {
                long actualSize = 0;

                // 方法1: 从 strm 文件内容读取实际文件路径并获取大小
                if (File.Exists(item.Path) && item.Path.EndsWith(".strm", StringComparison.OrdinalIgnoreCase))
                {
                    string targetPath = File.ReadAllText(item.Path).Trim();
                    _logger.LogDebug("STRM {Name} points to: {Path}", item.Name, targetPath);

                    // 检查目标文件是否存在
                    if (File.Exists(targetPath))
                    {
                        var fileInfo = new FileInfo(targetPath);
                        actualSize = fileInfo.Length;
                        _logger.LogDebug("Target file size: {Size} bytes", actualSize);
                    }
                    else
                    {
                        _logger.LogDebug("Target file does not exist: {Path}", targetPath);
                    }
                }

                // 方法2: 如果方法1失败，尝试从媒体源获取
                if (actualSize == 0)
                {
                    var mediaSources = GetMediaSources(item);
                    if (mediaSources != null && mediaSources.Count > 0)
                    {
                        var primarySource = mediaSources[0];
                        if (primarySource.Size.HasValue && primarySource.Size.Value > 0)
                        {
                            actualSize = primarySource.Size.Value;
                            _logger.LogDebug("Got size from MediaSource: {Size} bytes", actualSize);
                        }
                    }
                }

                // 如果还是没有获取到大小
                if (actualSize == 0)
                {
                    _logger.LogDebug("Could not determine actual file size for {Name}", item.Name);
                    return false;
                }

                // 检查是否需要更新
                if (item.Size.HasValue && item.Size.Value == actualSize)
                {
                    return false;
                }

                // 更新大小到 item（使用多种方法确保持久化）
                item.Size = actualSize;

                // 尝试通过反射设置私有字段（如果存在）
                try
                {
                    var sizeField = item.GetType().GetField("_size",
                        System.Reflection.BindingFlags.NonPublic |
                        System.Reflection.BindingFlags.Instance);

                    if (sizeField != null)
                    {
                        sizeField.SetValue(item, actualSize);
                        _logger.LogDebug("Set _size field via reflection");
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogDebug(ex, "Could not set _size field via reflection");
                }

                // 让 Jellyfin 认为这是真正的媒体文件而不是 STRM
                try
                {
                    // 尝试设置 VideoType 为 VideoFile（而不是 Iso 或其他）
                    var videoTypeProperty = item.GetType().GetProperty("VideoType",
                        System.Reflection.BindingFlags.Public |
                        System.Reflection.BindingFlags.Instance);

                    if (videoTypeProperty != null && videoTypeProperty.CanWrite)
                    {
                        // VideoType.VideoFile = 0
                        videoTypeProperty.SetValue(item, 0);
                        _logger.LogDebug("Set VideoType to VideoFile");
                    }

                    // 尝试设置 IsShortcut 为 false
                    var isShortcutProperty = item.GetType().GetProperty("IsShortcut",
                        System.Reflection.BindingFlags.Public |
                        System.Reflection.BindingFlags.Instance);

                    if (isShortcutProperty != null && isShortcutProperty.CanWrite)
                    {
                        isShortcutProperty.SetValue(item, false);
                        _logger.LogDebug("Set IsShortcut to false");
                    }

                    // 尝试设置 LocationType 为 FileSystem
                    var locationTypeProperty = item.GetType().GetProperty("LocationType",
                        System.Reflection.BindingFlags.Public |
                        System.Reflection.BindingFlags.Instance);

                    if (locationTypeProperty != null && locationTypeProperty.CanWrite)
                    {
                        // LocationType.FileSystem = 0
                        locationTypeProperty.SetValue(item, 0);
                        _logger.LogDebug("Set LocationType to FileSystem");
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogDebug(ex, "Could not set video type properties");
                }

                // 同时更新媒体源的大小（如果存在）
                UpdateMediaSourceSize(item, actualSize);

                // 方法1: 使用 LibraryManager 更新（标准方式）
                await _libraryManager.UpdateItemAsync(
                    item,
                    item.GetParent(),
                    ItemUpdateType.MetadataEdit,
                    cancellationToken);

                // 方法2: 直接使用 ItemRepository 保存到数据库（确保持久化）
                try
                {
                    _itemRepository.SaveItems(new[] { item }, cancellationToken);
                    _logger.LogDebug("Saved item to database via ItemRepository");
                }
                catch (Exception ex)
                {
                    _logger.LogDebug(ex, "Could not save via ItemRepository, but LibraryManager update succeeded");
                }

                _logger.LogInformation("✓ Updated size: {Name} from {Old} to {New} ({NewFormatted})",
                    item.Name,
                    oldSize?.ToString() ?? "null",
                    actualSize,
                    FormatSize(actualSize));

                return true;
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Could not update size for: {Name}", item.Name);
                return false;
            }
        }

        /// <summary>
        /// 更新媒体源的大小
        /// </summary>
        private void UpdateMediaSourceSize(BaseItem item, long size)
        {
            try
            {
                var mediaSources = GetMediaSources(item);
                if (mediaSources != null && mediaSources.Count > 0)
                {
                    foreach (var source in mediaSources)
                    {
                        source.Size = size;
                    }
                    _logger.LogDebug("Updated MediaSource size to {Size}", size);
                }
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Could not update MediaSource size");
            }
        }

        /// <summary>
        /// 获取媒体流
        /// </summary>
        private List<MediaStream> GetMediaStreams(BaseItem item)
        {
            try
            {
                var property = item.GetType().GetProperty("MediaStreams");
                if (property != null)
                {
                    var streams = property.GetValue(item) as IEnumerable<MediaStream>;
                    return streams?.ToList() ?? new List<MediaStream>();
                }
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Could not get media streams for: {Name}", item.Name);
            }
            return new List<MediaStream>();
        }

        /// <summary>
        /// 获取媒体源
        /// </summary>
        private List<MediaBrowser.Model.Dto.MediaSourceInfo> GetMediaSources(BaseItem item)
        {
            try
            {
                var method = item.GetType().GetMethod("GetMediaSources");
                if (method != null)
                {
                    var sources = method.Invoke(item, new object[] { false }) as List<MediaBrowser.Model.Dto.MediaSourceInfo>;
                    return sources ?? new List<MediaBrowser.Model.Dto.MediaSourceInfo>();
                }
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Could not get media sources for: {Name}", item.Name);
            }
            return new List<MediaBrowser.Model.Dto.MediaSourceInfo>();
        }

        /// <summary>
        /// 格式化文件大小
        /// </summary>
        private string FormatSize(long? size)
        {
            if (!size.HasValue || size.Value <= 0)
            {
                return "Unknown";
            }

            double bytes = size.Value;
            if (bytes >= 1073741824) // >= 1 GB
            {
                return $"{bytes / 1073741824:F2} GB";
            }
            else if (bytes >= 1048576) // >= 1 MB
            {
                return $"{bytes / 1048576:F2} MB";
            }
            else if (bytes >= 1024) // >= 1 KB
            {
                return $"{bytes / 1024:F2} KB";
            }
            else
            {
                return $"{bytes} Bytes";
            }
        }
    }
}