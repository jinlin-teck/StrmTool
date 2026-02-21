using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
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
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
            _libraryManager = libraryManager ?? throw new ArgumentNullException(nameof(libraryManager));
            _itemRepository = itemRepository ?? throw new ArgumentNullException(nameof(itemRepository));
            _jsonSerializer = jsonSerializer ?? throw new ArgumentNullException(nameof(jsonSerializer));
        }

        /// <summary>
        /// 扫描 Emby 媒体库并导出媒体信息。
        /// </summary>
        public async Task ExportAllAsync(IProgress<double> progress, CancellationToken cancellationToken)
        {
            var allItems = MediaInfoHelper.GetAllStrmFiles(_libraryManager);
            LogHelper.Info(_logger, $"Found {allItems.Count} STRM files");

            var validItems = MediaInfoHelper.GetStrmFilesWithCompleteMediaInfo(_libraryManager).ToArray();

            int total = validItems.Length;
            int current = 0;
            int skipped = 0;
            int exported = 0;

            LogHelper.Info(_logger, $"Starting export of {total} valid STRM files...");

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
                        LogHelper.Debug(_logger, $"Skipped {item.Name}, JSON already exists: {filePath}");
                    }
                    else
                    {
                        await ExportItemAsync(item, cancellationToken);
                        LogHelper.Info(_logger, $"Successfully exported {item.Name} to: {filePath}");
                        exported++;
                    }
                }
                catch (Exception ex)
                {
                    LogHelper.ErrorException(_logger, $"Error exporting {item.Name}", ex);
                }

                current++;
                progress?.Report(current * 100.0 / total);
            }

            LogHelper.Info(_logger, $"Export completed. Skipped {skipped} existing JSON files, saved {exported} new files.");
        }

        /// <summary>
        /// 导出单个媒体项信息为 JSON 文件。
        /// </summary>
        public async Task ExportItemAsync(BaseItem item, CancellationToken cancellationToken = default)
        {
            if (item == null)
                return;

            try
            {
                var mediaSourcesWithChapters = await PrepareMediaSourcesForExportAsync(item, cancellationToken);
                await WriteMediaInfoToFileAsync(item, mediaSourcesWithChapters, cancellationToken);
                LogHelper.Debug(_logger, $"Exported {item.Name} → {GetMediaInfoJsonPath(item)}");
            }
            catch (Exception ex)
            {
                LogHelper.ErrorException(_logger, $"Error exporting media info for {item.Name}", ex);
            }
        }

        private async Task<List<MediaSourceWithChapters>> PrepareMediaSourcesForExportAsync(
            BaseItem item, CancellationToken cancellationToken)
        {
            var libraryOptions = _libraryManager.GetLibraryOptions(item);
            if (libraryOptions == null)
            {
                LogHelper.Warn(_logger, $"Library options is null for item: {item.Name}, skipping export");
                return new List<MediaSourceWithChapters>();
            }

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
                SanitizeMediaSourceInfo(jsonItem);
                SanitizeChapters(jsonItem);
                SetEpisodeSpecificInfo(item, jsonItem);
                await SetAudioEmbeddedImageAsync(item, jsonItem, cancellationToken);
            }

            return mediaSourcesWithChapters;
        }

        private void SanitizeMediaSourceInfo(MediaSourceWithChapters jsonItem)
        {
            if (jsonItem.MediaSourceInfo == null)
                return;

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

        private void SanitizeChapters(MediaSourceWithChapters jsonItem)
        {
            foreach (var chapter in jsonItem.Chapters)
            {
                chapter.ImageTag = null;
            }
        }

        private void SetEpisodeSpecificInfo(BaseItem item, MediaSourceWithChapters jsonItem)
        {
            if (item is Episode)
            {
                jsonItem.ZeroFingerprintConfidence =
                    !string.IsNullOrEmpty(_itemRepository.GetIntroDetectionFailureResult(item.InternalId));
            }
        }

        private async Task SetAudioEmbeddedImageAsync(
            BaseItem item, MediaSourceWithChapters jsonItem, CancellationToken cancellationToken)
        {
            if (item is Audio)
            {
                var primaryImageInfo = item.GetImageInfo(ImageType.Primary, 0);
                if (primaryImageInfo != null && File.Exists(primaryImageInfo.Path))
                {
                    var imageBytes = await File.ReadAllBytesAsync(primaryImageInfo.Path, cancellationToken);
                    var base64String = Convert.ToBase64String(imageBytes);
                    jsonItem.EmbeddedImage = base64String;
                }
            }
        }

        private async Task WriteMediaInfoToFileAsync(
            BaseItem item, List<MediaSourceWithChapters> mediaSourcesWithChapters, CancellationToken cancellationToken)
        {
            string filePath = GetMediaInfoJsonPath(item);
            var json = _jsonSerializer.SerializeToString(mediaSourcesWithChapters);
            await File.WriteAllTextAsync(filePath, json, cancellationToken);
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
            LogHelper.Info(_logger, $"Found {strmItems.Count} STRM files requiring restore with JSON");

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
                    LogHelper.ErrorException(_logger, $"Error restoring {item.Name}", ex);
                }

                current++;
                progress?.Report(current * 100.0 / total);
            }

            LogHelper.Info(_logger, "Restore operation completed.");
        }

        /// <summary>
        /// 恢复单个媒体项信息从JSON文件
        /// </summary>
        public async Task RestoreItemAsync(BaseItem item, CancellationToken cancellationToken = default)
        {
            try
            {
                LogHelper.Debug(_logger, $"Restoring media item: {item.Name} (Path: {item.Path})");

                var jsonFilePath = GetMediaInfoJsonPath(item);
                var mediaSourceWithChapters = await LoadAndValidateMediaSourceAsync(jsonFilePath, cancellationToken);
                
                if (mediaSourceWithChapters == null)
                    return;

                await RestoreMediaDataAsync(item, mediaSourceWithChapters, jsonFilePath, cancellationToken);
                LogHelper.Info(_logger, $"Restore completed: {item.Name} ← {jsonFilePath}");
            }
            catch (Exception ex)
            {
                LogHelper.ErrorException(_logger, $"Error restoring media information for {item.Name}", ex);
            }
        }

        private async Task<MediaSourceWithChapters?> LoadAndValidateMediaSourceAsync(
            string jsonFilePath, CancellationToken cancellationToken)
        {
            if (!File.Exists(jsonFilePath))
            {
                LogHelper.Warn(_logger, $"JSON file not found: {jsonFilePath}");
                return null;
            }

            List<MediaSourceWithChapters>? mediaSourcesWithChapters;
            try
            {
                var jsonContent = await File.ReadAllTextAsync(jsonFilePath, cancellationToken);
                mediaSourcesWithChapters = _jsonSerializer.DeserializeFromString<List<MediaSourceWithChapters>>(jsonContent);
            }
            catch (Exception ex) when (ex is InvalidOperationException)
            {
                LogHelper.ErrorException(_logger, $"JSON deserialization failed for {jsonFilePath}. File may be corrupted.", ex);
                return null;
            }

            if (mediaSourcesWithChapters == null)
            {
                LogHelper.Warn(_logger, "JSON deserialization failed (null)");
                return null;
            }

            if (mediaSourcesWithChapters.Count == 0)
            {
                LogHelper.Warn(_logger, "JSON deserialization succeeded but list is empty");
                return null;
            }

            var mediaSourceWithChapters = mediaSourcesWithChapters[0];
            if (mediaSourceWithChapters?.MediaSourceInfo == null)
            {
                LogHelper.Warn(_logger, "first media source contains null MediaSourceInfo");
                return null;
            }

            if (!mediaSourceWithChapters.MediaSourceInfo.RunTimeTicks.HasValue)
            {
                LogHelper.Warn(_logger, $"JSON file is missing runtime information: {jsonFilePath}");
                return null;
            }

            return mediaSourceWithChapters;
        }

        private async Task RestoreMediaDataAsync(
            BaseItem item, MediaSourceWithChapters mediaSourceWithChapters, 
            string jsonFilePath, CancellationToken cancellationToken)
        {
            RestoreMediaStreams(item, mediaSourceWithChapters, cancellationToken);
            await RestoreAudioEmbeddedImageAsync(item, mediaSourceWithChapters, cancellationToken);
            UpdateItemProperties(item, mediaSourceWithChapters);
            UpdateVideoResolution(item, mediaSourceWithChapters);
            SaveItemToLibrary(item, cancellationToken);
            RestoreChapters(item, mediaSourceWithChapters);
            RestoreEpisodeInfo(item, mediaSourceWithChapters);
        }

        private void RestoreMediaStreams(
            BaseItem item, MediaSourceWithChapters mediaSourceWithChapters, CancellationToken cancellationToken)
        {
            if (mediaSourceWithChapters.MediaSourceInfo!.MediaStreams != null)
            {
                _itemRepository.SaveMediaStreams(item.InternalId, mediaSourceWithChapters.MediaSourceInfo.MediaStreams, cancellationToken);
            }
        }

        private async Task RestoreAudioEmbeddedImageAsync(
            BaseItem item, MediaSourceWithChapters mediaSourceWithChapters, CancellationToken cancellationToken)
        {
            if (item is Audio && !string.IsNullOrEmpty(mediaSourceWithChapters.EmbeddedImage))
            {
                try
                {
                    var imageBytes = Convert.FromBase64String(mediaSourceWithChapters.EmbeddedImage);
                    var audioImageDir = Path.Combine(Path.GetDirectoryName(item.Path) ?? Path.GetTempPath(), ".metadata");
                    Directory.CreateDirectory(audioImageDir);
                    var imagePath = Path.Combine(audioImageDir, $"{Path.GetFileNameWithoutExtension(item.Path)}.jpg");
                    
                    if (File.Exists(imagePath))
                    {
                        try
                        {
                            File.Delete(imagePath);
                        }
                        catch (IOException ioEx)
                        {
                            LogHelper.Debug(_logger, $"Could not delete existing image: {ioEx.Message}");
                        }
                        catch (UnauthorizedAccessException authEx)
                        {
                            LogHelper.Warn(_logger, $"Permission denied when deleting image: {authEx.Message}");
                        }
                    }
                    
                    await File.WriteAllBytesAsync(imagePath, imageBytes);
                    LogHelper.Debug(_logger, $"Restored embedded image for audio file {item.Name}");
                }
                catch (Exception ex)
                {
                    LogHelper.ErrorException(_logger, "Error restoring audio embedded image", ex);
                }
            }
        }

        private void UpdateItemProperties(BaseItem item, MediaSourceWithChapters mediaSourceWithChapters)
        {
            item.Size = mediaSourceWithChapters.MediaSourceInfo!.Size.GetValueOrDefault();
            item.RunTimeTicks = mediaSourceWithChapters.MediaSourceInfo.RunTimeTicks;
            item.Container = mediaSourceWithChapters.MediaSourceInfo.Container;
            item.TotalBitrate = mediaSourceWithChapters.MediaSourceInfo.Bitrate.GetValueOrDefault();
        }

        private void UpdateVideoResolution(BaseItem item, MediaSourceWithChapters mediaSourceWithChapters)
        {
            if (mediaSourceWithChapters.MediaSourceInfo!.MediaStreams != null)
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
        }

        private void SaveItemToLibrary(BaseItem item, CancellationToken cancellationToken)
        {
            _libraryManager.UpdateItems(new List<BaseItem> { item }, null,
                ItemUpdateType.MetadataImport, false, false, null, cancellationToken);
        }

        private void RestoreChapters(BaseItem item, MediaSourceWithChapters mediaSourceWithChapters)
        {
            if (item is MediaBrowser.Controller.Entities.Video video && mediaSourceWithChapters.Chapters != null)
            {
                _itemRepository.SaveChapters(item.InternalId, mediaSourceWithChapters.Chapters);
            }
        }

        private void RestoreEpisodeInfo(BaseItem item, MediaSourceWithChapters mediaSourceWithChapters)
        {
            if (item is Episode && mediaSourceWithChapters.ZeroFingerprintConfidence == true)
            {
                _itemRepository.LogIntroDetectionFailureFailure(item.InternalId,
                    item.DateModified.ToUnixTimeSeconds());
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
