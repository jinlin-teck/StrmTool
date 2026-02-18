using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Audio;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Persistence;
using MediaBrowser.Model.Configuration;
using MediaBrowser.Model.Dto;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.IO;
using MediaBrowser.Model.Logging;
using MediaBrowser.Model.MediaInfo;
using MediaBrowser.Model.Serialization;

namespace StrmTool.Common
{
    /// <summary>
    /// 管理媒体信息的导出与保存。
    /// </summary>
    public class MediaInfoManager
    {
        private readonly ILogger _logger;
        private readonly ILibraryManager _libraryManager;
        private readonly IItemRepository _itemRepository;
        private readonly IJsonSerializer _jsonSerializer;

        public MediaInfoManager(ILogger logger, ILibraryManager libraryManager, IItemRepository itemRepository, IJsonSerializer jsonSerializer)
        {
            _logger = logger;
            _libraryManager = libraryManager;
            _itemRepository = itemRepository;
            _jsonSerializer = jsonSerializer;
        }

        /// <summary>
        /// 扫描 Emby 媒体库并导出媒体信息。
        /// </summary>
        public async Task ExportAllAsync(IProgress<double> progress, CancellationToken cancellationToken)
        {
            var allItems = MediaInfoHelper.GetAllStrmFiles(_libraryManager);
            _logger.Info($"StrmTool found {allItems.Count} STRM files");

            var validItems = MediaInfoHelper.GetStrmFilesWithCompleteMediaInfo(_libraryManager).ToArray();

            int total = validItems.Length;
            int current = 0;
            int skipped = 0;
            int exported = 0;

            _logger.Info($"StrmTool starting export of {total} valid STRM files...");

            foreach (var item in validItems)
            {
                if (cancellationToken.IsCancellationRequested)
                    break;

                try
                {
                    string filePath = GetMediaInfoJsonPath(item);
                    if (File.Exists(filePath))
                    {
                        skipped++;
                        _logger.Debug($"StrmTool skipped {item.Name}, JSON already exists: {filePath}");
                    }
                    else
                    {
                        await ExportItemAsync(item);
                        _logger.Info($"StrmTool successfully exported {item.Name} to: {filePath}");
                        exported++;
                    }
                }
                catch (Exception ex)
                {
                    _logger.ErrorException($"StrmTool error exporting {item.Name}", ex);
                }

                current++;
                progress?.Report(current * 100.0 / total);
            }

            _logger.Info($"StrmTool export completed. Skipped {skipped} existing JSON files, saved {exported} new files.");
        }

        /// <summary>
        /// 导出单个媒体项信息为 JSON 文件。
        /// </summary>
        public Task ExportItemAsync(BaseItem item)
        {
            if (item == null)
                return Task.CompletedTask;

            try
            {
                var libraryOptions = _libraryManager.GetLibraryOptions(item);
                var mediaSources = item.GetMediaSources(true, false, libraryOptions);
                var chapters = _itemRepository.GetChapters(item);

                var mediaSourcesWithChapters = mediaSources.Select(mediaSource =>
                        new MediaSourceWithChapters
                        { 
                            MediaSourceInfo = mediaSource, 
                            Chapters = chapters 
                        })
                    .ToList();

                foreach (var jsonItem in mediaSourcesWithChapters)
                {
                    if (jsonItem.MediaSourceInfo != null)
                    {
                        jsonItem.MediaSourceInfo.Id = null;
                        jsonItem.MediaSourceInfo.ItemId = null;
                        jsonItem.MediaSourceInfo.Path = null;

                        foreach (var subtitle in jsonItem.MediaSourceInfo.MediaStreams.Where(m =>
                                     m.IsExternal && m.Type == MediaStreamType.Subtitle &&
                                     m.Protocol == MediaProtocol.File))
                        {
                            subtitle.Path = Path.GetFileName(subtitle.Path);
                        }
                    }

                    foreach (var chapter in jsonItem.Chapters)
                    {
                        chapter.ImageTag = null;
                    }

                    if (item is Episode)
                    {
                        jsonItem.ZeroFingerprintConfidence =
                            !string.IsNullOrEmpty(_itemRepository.GetIntroDetectionFailureResult(item.InternalId));
                    }

                    if (item is Audio)
                    {
                        var primaryImageInfo = item.GetImageInfo(ImageType.Primary, 0);
                        if (primaryImageInfo != null && File.Exists(primaryImageInfo.Path))
                        {
                            var imageBytes = File.ReadAllBytes(primaryImageInfo.Path);
                            var base64String = Convert.ToBase64String(imageBytes);
                            jsonItem.EmbeddedImage = base64String;
                        }
                    }
                }

                string filePath = GetMediaInfoJsonPath(item);

                var json = _jsonSerializer.SerializeToString(mediaSourcesWithChapters);
                File.WriteAllText(filePath, json);

                _logger.Debug($"StrmTool exported {item.Name} → {filePath}");
            }
            catch (Exception ex)
            {
                _logger.ErrorException($"StrmTool error exporting media info for {item.Name}", ex);
            }

            return Task.CompletedTask;
        }

        /// <summary>
        /// 获取媒体信息JSON文件路径，直接保存在strm文件所在目录
        /// </summary>
        public string GetMediaInfoJsonPath(BaseItem item)
        {
            var mediaDirectory = Path.GetDirectoryName(item.Path);
            if (string.IsNullOrEmpty(mediaDirectory))
            {
                string safeName = MakeSafeFilename(item.Name);
                var tempPath = Path.Combine(Path.GetTempPath(), "StrmTool");
                Directory.CreateDirectory(tempPath);
                return Path.Combine(tempPath, $"{safeName}{CommonConfiguration.MediaInfoFileExtension}");
            }

            var mediaFileName = Path.GetFileNameWithoutExtension(item.Path);
            if (string.IsNullOrEmpty(mediaFileName))
            {
                mediaFileName = MakeSafeFilename(item.Name);
            }

            var jsonFilePath = Path.Combine(mediaDirectory, $"{mediaFileName}{CommonConfiguration.MediaInfoFileExtension}");

            Directory.CreateDirectory(mediaDirectory);

            return jsonFilePath;
        }

        /// <summary>
        /// 从JSON文件恢复媒体信息
        /// </summary>
        public async Task RestoreAllAsync(IProgress<double> progress, CancellationToken cancellationToken)
        {
            var strmItems = MediaInfoHelper.GetStrmFilesNeedingRestoreWithJson(_libraryManager, this);
            _logger.Info($"StrmTool found {strmItems.Count} STRM files requiring restore with JSON");

            int total = strmItems.Count;
            int current = 0;

            foreach (var item in strmItems)
            {
                if (cancellationToken.IsCancellationRequested)
                    break;

                try
                {
                    await RestoreItemAsync(item, cancellationToken);
                }
                catch (Exception ex)
                {
                    _logger.ErrorException($"StrmTool error restoring {item.Name}", ex);
                }

                current++;
                progress?.Report(current * 100.0 / total);
            }

            _logger.Info("StrmTool restore operation completed.");
        }

        /// <summary>
        /// 恢复单个媒体项信息从JSON文件
        /// </summary>
        public async Task RestoreItemAsync(BaseItem item, CancellationToken cancellationToken = default)
        {
            try
            {
                _logger.Debug($"StrmTool restoring media item: {item.Name} (Path: {item.Path})");

                var jsonFilePath = GetMediaInfoJsonPath(item);

                if (!File.Exists(jsonFilePath))
                {
                    _logger.Warn($"StrmTool JSON file not found: {jsonFilePath}");
                    return;
                }

                var jsonContent = await File.ReadAllTextAsync(jsonFilePath);
                var mediaSourcesWithChapters = _jsonSerializer.DeserializeFromString<List<MediaSourceWithChapters>>(jsonContent);

                if (mediaSourcesWithChapters == null)
                {
                    _logger.Warn("StrmTool JSON deserialization failed (null)");
                    return;
                }

                if (mediaSourcesWithChapters.Count == 0)
                {
                    _logger.Warn("StrmTool JSON deserialization succeeded but list is empty");
                    return;
                }

                var mediaSourceWithChapters = mediaSourcesWithChapters[0];
                if (mediaSourceWithChapters?.MediaSourceInfo == null)
                {
                    _logger.Warn("StrmTool first media source contains null MediaSourceInfo");
                    return;
                }

                if (!mediaSourceWithChapters.MediaSourceInfo.RunTimeTicks.HasValue)
                {
                    _logger.Warn($"StrmTool JSON file is missing runtime information: {jsonFilePath}");
                    return;
                }

                if (mediaSourceWithChapters.MediaSourceInfo.MediaStreams != null)
                {
                    _itemRepository.SaveMediaStreams(item.InternalId, mediaSourceWithChapters.MediaSourceInfo.MediaStreams, cancellationToken);
                }

                if (item is Audio && !string.IsNullOrEmpty(mediaSourceWithChapters.EmbeddedImage))
                {
                    try
                    {
                        var imageBytes = Convert.FromBase64String(mediaSourceWithChapters.EmbeddedImage);
                        var audioImageDir = Path.Combine(Path.GetDirectoryName(item.Path) ?? Path.GetTempPath(), ".metadata");
                        Directory.CreateDirectory(audioImageDir);
                        var imagePath = Path.Combine(audioImageDir, $"{Path.GetFileNameWithoutExtension(item.Path)}.jpg");
                        
                        // 避免重复写入，如果文件已存在则删除后重写
                        if (File.Exists(imagePath))
                        {
                            try { File.Delete(imagePath); } catch { }
                        }
                        
                        await File.WriteAllBytesAsync(imagePath, imageBytes);
                        _logger.Debug($"StrmTool restored embedded image for audio file {item.Name}");
                    }
                    catch (Exception ex)
                    {
                        _logger.ErrorException("StrmTool error restoring audio embedded image", ex);
                    }
                }

                item.Size = mediaSourceWithChapters.MediaSourceInfo.Size.GetValueOrDefault();
                item.RunTimeTicks = mediaSourceWithChapters.MediaSourceInfo.RunTimeTicks;
                item.Container = mediaSourceWithChapters.MediaSourceInfo.Container;
                item.TotalBitrate = mediaSourceWithChapters.MediaSourceInfo.Bitrate.GetValueOrDefault();

                if (mediaSourceWithChapters.MediaSourceInfo.MediaStreams != null)
                {
                    var videoStream = mediaSourceWithChapters.MediaSourceInfo.MediaStreams
                        .Where(s => s.Type == MediaStreamType.Video && s.Width.HasValue && s.Height.HasValue)
                        .OrderByDescending(s => (long)(s.Width ?? 0) * (s.Height ?? 0))
                        .FirstOrDefault();

                    if (videoStream != null)
                    {
                        item.Width = videoStream.Width ?? 0;
                        item.Height = videoStream.Height ?? 0;
                    }
                }

                _libraryManager.UpdateItems(new List<BaseItem> { item }, null,
                    ItemUpdateType.MetadataImport, false, false, null, cancellationToken);

                if (item is MediaBrowser.Controller.Entities.Video video && mediaSourceWithChapters.Chapters != null)
                {
                    _itemRepository.SaveChapters(item.InternalId, mediaSourceWithChapters.Chapters);
                }

                if (item is Episode && mediaSourceWithChapters.ZeroFingerprintConfidence == true)
                {
                    _itemRepository.LogIntroDetectionFailureFailure(item.InternalId,
                        item.DateModified.ToUnixTimeSeconds());
                }

                _logger.Info($"StrmTool restore completed: {item.Name} ← {jsonFilePath}");
            }
            catch (Exception ex)
            {
                _logger.ErrorException($"StrmTool error restoring media information for {item.Name}", ex);
            }
        }

        /// <summary>
        /// 使文件名安全，替换无效字符
        /// </summary>
        private string MakeSafeFilename(string name)
        {
            var invalidChars = Path.GetInvalidFileNameChars();
            var sb = new StringBuilder(name);
            foreach (char c in invalidChars)
            {
                sb.Replace(c, '_');
            }
            return sb.ToString();
        }
    }

    public class MediaSourceWithChapters
    {
        public MediaSourceInfo? MediaSourceInfo { get; set; }
        public List<ChapterInfo> Chapters { get; set; } = new List<ChapterInfo>();
        public bool? ZeroFingerprintConfidence { get; set; }
        public string? EmbeddedImage { get; set; }
    }
}
