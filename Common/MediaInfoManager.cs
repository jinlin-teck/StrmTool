using System;
using System.Collections.Concurrent;
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

        private sealed class JsonAudioTagCacheEntry
        {
            public long LastWriteTicks { get; set; }
            public long Length { get; set; }
            public bool HasProbedAudioTags { get; set; }
            public AudioMetadataInfo? AudioMetadata { get; set; }
        }

        private static readonly ConcurrentDictionary<string, JsonAudioTagCacheEntry> AudioTagCache =
            new ConcurrentDictionary<string, JsonAudioTagCacheEntry>(StringComparer.Ordinal);
        private const int TailReadBufferBytes = 16384;

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
            var validItems = MediaInfoHelper.GetStrmFilesWithCompleteMediaInfo(_libraryManager, this);
            Common.LogHelper.Info(_logger, $"Found {validItems.Count} STRM files with complete media info");

            int skipped = 0;
            var maxConcurrency = Plugin.GetSafeConfiguration().MaxConcurrency;

            int exported = await ProcessItemsAsync(
                validItems,
                "export",
                maxConcurrency,
                async (item, ct) =>
                {
                    string filePath = GetMediaInfoJsonPath(item);
                    if (File.Exists(filePath) &&
                        (!AudioMetadataHelper.IsMusicLibraryAudio(item, _libraryManager) || HasProbedAudioTagsInJson(filePath)))
                    {
                        Interlocked.Increment(ref skipped);
                        Common.LogHelper.Debug(_logger, $"Skipped {item.Name}, JSON already exists: {filePath}");
                        return null;
                    }

                    var exportSucceeded = await ExportItemAsync(item, ct).ConfigureAwait(false);
                    if (exportSucceeded)
                    {
                        Common.LogHelper.Info(_logger, $"Successfully exported {item.Name} to: {filePath}");
                        return true;
                    }

                    Common.LogHelper.Warn(_logger, $"Failed to export {item.Name} to: {filePath}");
                    return false;
                },
                progress,
                cancellationToken).ConfigureAwait(false);

            Common.LogHelper.Info(_logger, $"Export completed. Skipped {skipped} existing JSON files, saved {exported} new files.");
        }

        /// <summary>
        /// 从JSON文件恢复媒体信息
        /// </summary>
        public async Task RestoreAllAsync(IProgress<double> progress, CancellationToken cancellationToken)
        {
            var strmItems = MediaInfoHelper.GetStrmFilesNeedingRestoreWithJson(_libraryManager, this);
            Common.LogHelper.Info(_logger, $"Found {strmItems.Count} STRM files requiring restore with JSON");

            var maxConcurrency = Plugin.GetSafeConfiguration().MaxConcurrency;

            int restored = await ProcessItemsAsync(
                strmItems,
                "restore",
                maxConcurrency,
                async (item, ct) =>
                {
                    var ok = await RestoreItemAsync(item, ct).ConfigureAwait(false);
                    if (!ok)
                    {
                        Common.LogHelper.Warn(_logger, $"Failed to restore {item.Name} from JSON");
                    }

                    return ok;
                },
                progress,
                cancellationToken).ConfigureAwait(false);

            Common.LogHelper.Info(_logger, $"Restore operation completed. Restored {restored}/{strmItems.Count} items.");
        }

        /// <summary>
        /// 以受限并发批量处理媒体项的公共骨架：取消检查、异常隔离、进度报告。
        /// action 返回 true 表示成功，false 表示失败，null 表示跳过。
        /// </summary>
        /// <returns>成功处理的项数</returns>
        private async Task<int> ProcessItemsAsync(
            IReadOnlyCollection<BaseItem> items,
            string operationName,
            int maxConcurrency,
            Func<BaseItem, CancellationToken, Task<bool?>> action,
            IProgress<double> progress,
            CancellationToken cancellationToken)
        {
            int total = items.Count;
            if (total == 0)
            {
                progress?.Report(100);
                return 0;
            }

            int processed = 0;
            int succeeded = 0;
            var progressLock = new object();
            var concurrency = Math.Max(1, maxConcurrency);
            using var semaphore = new SemaphoreSlim(concurrency, concurrency);

            var tasks = items.Select(async item =>
            {
                await semaphore.WaitAsync(cancellationToken).ConfigureAwait(false);
                try
                {
                    if (cancellationToken.IsCancellationRequested)
                    {
                        return;
                    }

                    bool? result;
                    try
                    {
                        result = await action(item, cancellationToken).ConfigureAwait(false);
                    }
                    catch (OperationCanceledException)
                    {
                        throw;
                    }
                    catch (Exception ex)
                    {
                        Common.LogHelper.ErrorException(_logger, $"Error during {operationName} for {item.Name}", ex);
                        result = false;
                    }

                    if (result == true)
                    {
                        Interlocked.Increment(ref succeeded);
                    }
                }
                finally
                {
                    semaphore.Release();
                }

                // 加锁保证进度单调递增，不会倒退
                lock (progressLock)
                {
                    processed++;
                    progress?.Report(processed * 100.0 / total);
                }
            });

            try
            {
                await Task.WhenAll(tasks).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                // 记录取消进度后继续向上传播，让 Emby 正确将任务标记为已取消而非成功
                Common.LogHelper.Info(_logger, $"{operationName} operation cancelled. Processed {processed}/{total} items.");
                throw;
            }

            return succeeded;
        }

        /// <summary>
        /// 导出单个媒体项信息为 JSON 文件。
        /// </summary>
        public async Task<bool> ExportItemAsync(
            BaseItem item,
            CancellationToken cancellationToken = default,
            bool audioTagsProbed = false)
        {
            if (item == null)
                return false;

            try
            {
                var mediaSourcesWithChapters = await PrepareMediaSourcesForExportAsync(
                    item, cancellationToken, audioTagsProbed).ConfigureAwait(false);
                if (mediaSourcesWithChapters.Count == 0)
                {
                    Common.LogHelper.Warn(_logger, $"No media sources available for {item.Name}, skipping export");
                    return false;
                }

                await WriteMediaInfoToFileAsync(item, mediaSourcesWithChapters, cancellationToken).ConfigureAwait(false);
                Common.LogHelper.Debug(_logger, $"Exported {item.Name} → {GetMediaInfoJsonPath(item)}");
                return true;
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                Common.LogHelper.ErrorException(_logger, $"Error exporting media info for {item.Name}", ex);
                return false;
            }
        }

        private async Task<List<MediaSourceWithChapters>> PrepareMediaSourcesForExportAsync(
            BaseItem item,
            CancellationToken cancellationToken,
            bool audioTagsProbed = false)
        {
            var libraryOptions = _libraryManager.GetLibraryOptions(item);
            if (libraryOptions == null)
            {
                Common.LogHelper.Warn(_logger, $"Library options is null for item: {item.Name}, skipping export");
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
                if (item is Audio audio && AudioMetadataHelper.IsMusicLibraryAudio(audio, _libraryManager))
                {
                    jsonItem.AudioMetadata = AudioMetadataHelper.BuildAudioMetadataForExport(
                        audio, _libraryManager, audioTagsProbed);
                }

                await SetAudioEmbeddedImageAsync(item, jsonItem, cancellationToken).ConfigureAwait(false);
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
                    var imageBytes = await File.ReadAllBytesAsync(primaryImageInfo.Path, cancellationToken).ConfigureAwait(false);
                    var base64String = Convert.ToBase64String(imageBytes);
                    jsonItem.EmbeddedImage = base64String;
                }
            }
        }

        private async Task WriteMediaInfoToFileAsync(
            BaseItem item, List<MediaSourceWithChapters> mediaSourcesWithChapters, CancellationToken cancellationToken)
        {
            string filePath = GetMediaInfoJsonPath(item);
            var directory = Path.GetDirectoryName(filePath);
            if (!string.IsNullOrEmpty(directory))
            {
                Directory.CreateDirectory(directory);
            }

            var json = _jsonSerializer.SerializeToString(mediaSourcesWithChapters);
            await File.WriteAllTextAsync(filePath, json, cancellationToken).ConfigureAwait(false);
            UpdateAudioTagCache(filePath, mediaSourcesWithChapters.FirstOrDefault()?.AudioMetadata);
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
                return Path.Combine(tempPath, $"{safeName}{CommonConfiguration.MediaInfoFileExtension}");
            }

            var mediaFileName = Path.GetFileNameWithoutExtension(item.Path);
            if (string.IsNullOrEmpty(mediaFileName))
            {
                mediaFileName = MakeSafeFilename(item.Name);
            }

            var jsonFilePath = Path.Combine(mediaDirectory, $"{mediaFileName}{CommonConfiguration.MediaInfoFileExtension}");
            return jsonFilePath;
        }

        /// <summary>
        /// 恢复单个媒体项信息从JSON文件
        /// </summary>
        public async Task<bool> RestoreItemAsync(
            BaseItem item,
            CancellationToken cancellationToken = default)
        {
            if (item == null)
                return false;

            try
            {
                Common.LogHelper.Debug(_logger, $"Restoring media item: {item.Name} (Path: {item.Path})");

                var jsonFilePath = GetMediaInfoJsonPath(item);
                var mediaSourceWithChapters = await LoadAndValidateMediaSourceAsync(jsonFilePath, cancellationToken).ConfigureAwait(false);

                if (mediaSourceWithChapters == null)
                    return false;

                await RestoreMediaDataAsync(item, mediaSourceWithChapters, cancellationToken).ConfigureAwait(false);
                Common.LogHelper.Info(_logger, $"Restore completed: {item.Name} ← {jsonFilePath}");
                return true;
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                Common.LogHelper.ErrorException(_logger, $"Error restoring media information for {item.Name}", ex);
                return false;
            }
        }

        /// <summary>
        /// 当音乐库音频已有媒体流但同目录存在尚未入库的外挂歌词（.lrc / .elrc）时，纯本地合并并保存歌词流。
        /// </summary>
        public bool TryMergeLocalLyrics(BaseItem item, CancellationToken cancellationToken = default)
        {
            if (item == null)
            {
                return false;
            }

            try
            {
                var currentStreams = item.GetMediaStreams();
                if (!MediaInfoHelper.HasCompleteMediaInfo(currentStreams) ||
                    !AudioMetadataHelper.HasMissingLocalLyrics(item, currentStreams, _libraryManager))
                {
                    return false;
                }

                var mergedStreams = AudioMetadataHelper.PrepareMediaStreamsForSave(item, currentStreams);
                _itemRepository.SaveMediaStreams(item.InternalId, mergedStreams, cancellationToken);
                Common.LogHelper.Info(_logger, $"Merged local lyrics for {item.Name}");
                return true;
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                Common.LogHelper.ErrorException(_logger, $"Error merging local lyrics for {item.Name}", ex);
                return false;
            }
        }

        /// <summary>
        /// 检查媒体项对应的 JSON 文件中是否已记录探测过的音频标签（AudioTagsProbed == true）。
        /// 使用基于文件时间戳与长度的内存缓存及尾部轻量解析，避免重复全量读取和反序列化 Base64 封面。
        /// </summary>
        public bool HasProbedAudioTagsInJson(BaseItem item)
        {
            if (item == null)
            {
                return false;
            }

            return HasProbedAudioTagsInJson(GetMediaInfoJsonPath(item));
        }

        /// <summary>
        /// 判断音频项是否可通过本地 JSON（或目录/文件名兜底）补齐当前缺失的核心音频元数据。
        /// </summary>
        public bool CanRestoreAudioMetadataFromJson(Audio audio)
        {
            if (audio == null)
            {
                return false;
            }

            TryGetJsonAudioMetadata(GetMediaInfoJsonPath(audio), out _, out var cachedAudioMetadata);
            return AudioMetadataHelper.CanRestoreMissingAudioMetadata(audio, cachedAudioMetadata, _libraryManager);
        }

        private bool HasProbedAudioTagsInJson(string jsonFilePath)
        {
            return TryGetJsonAudioMetadata(jsonFilePath, out var hasProbed, out _) && hasProbed;
        }

        private bool TryGetJsonAudioMetadata(
            string jsonFilePath,
            out bool hasProbedAudioTags,
            out AudioMetadataInfo? audioMetadata)
        {
            hasProbedAudioTags = false;
            audioMetadata = null;

            try
            {
                var fileInfo = new FileInfo(jsonFilePath);
                if (!fileInfo.Exists)
                {
                    AudioTagCache.TryRemove(jsonFilePath, out _);
                    return false;
                }

                long ticks = fileInfo.LastWriteTimeUtc.Ticks;
                long length = fileInfo.Length;

                if (AudioTagCache.TryGetValue(jsonFilePath, out var cached) &&
                    cached.LastWriteTicks == ticks &&
                    cached.Length == length)
                {
                    hasProbedAudioTags = cached.HasProbedAudioTags;
                    audioMetadata = cached.AudioMetadata;
                    return true;
                }

                // AudioMetadata 位于 JSON 末尾（在可能很大的 EmbeddedImage Base64 之后），优先只读文件尾部
                string tailText = ReadFileTailText(jsonFilePath, length, TailReadBufferBytes);
                if (tailText.IndexOf("\"AudioMetadata\"", StringComparison.Ordinal) < 0 && length > TailReadBufferBytes)
                {
                    tailText = File.ReadAllText(jsonFilePath);
                }

                if (tailText.IndexOf("\"AudioMetadata\"", StringComparison.Ordinal) >= 0)
                {
                    var objectJson = ExtractJsonObjectAfterKey(tailText, "\"AudioMetadata\"");
                    if (!string.IsNullOrEmpty(objectJson))
                    {
                        audioMetadata = _jsonSerializer.DeserializeFromString<AudioMetadataInfo>(objectJson);
                        hasProbedAudioTags = audioMetadata?.AudioTagsProbed == true;
                    }
                }

                AudioTagCache[jsonFilePath] = new JsonAudioTagCacheEntry
                {
                    LastWriteTicks = ticks,
                    Length = length,
                    HasProbedAudioTags = hasProbedAudioTags,
                    AudioMetadata = audioMetadata
                };
                return true;
            }
            catch
            {
                return false;
            }
        }

        private static void UpdateAudioTagCache(string jsonFilePath, AudioMetadataInfo? audioMetadata)
        {
            try
            {
                var fileInfo = new FileInfo(jsonFilePath);
                if (!fileInfo.Exists)
                {
                    AudioTagCache.TryRemove(jsonFilePath, out _);
                    return;
                }

                AudioTagCache[jsonFilePath] = new JsonAudioTagCacheEntry
                {
                    LastWriteTicks = fileInfo.LastWriteTimeUtc.Ticks,
                    Length = fileInfo.Length,
                    HasProbedAudioTags = audioMetadata?.AudioTagsProbed == true,
                    AudioMetadata = audioMetadata
                };
            }
            catch
            {
                // 忽略缓存更新异常
            }
        }

        private static string ReadFileTailText(string filePath, long fileLength, int maxBytes)
        {
            if (fileLength <= maxBytes)
            {
                return File.ReadAllText(filePath);
            }

            using var fs = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            fs.Seek(-maxBytes, SeekOrigin.End);
            var buffer = new byte[maxBytes];
            int totalRead = 0;
            while (totalRead < maxBytes)
            {
                int read = fs.Read(buffer, totalRead, maxBytes - totalRead);
                if (read <= 0)
                {
                    break;
                }

                totalRead += read;
            }

            return Encoding.UTF8.GetString(buffer, 0, totalRead);
        }

        private static string? ExtractJsonObjectAfterKey(string text, string keyToken)
        {
            int keyIndex = text.LastIndexOf(keyToken, StringComparison.Ordinal);
            if (keyIndex < 0)
            {
                return null;
            }

            int colonIndex = text.IndexOf(':', keyIndex + keyToken.Length);
            if (colonIndex < 0)
            {
                return null;
            }

            int startBrace = -1;
            for (int i = colonIndex + 1; i < text.Length; i++)
            {
                char c = text[i];
                if (char.IsWhiteSpace(c))
                {
                    continue;
                }

                if (c == '{')
                {
                    startBrace = i;
                }

                break;
            }

            if (startBrace < 0)
            {
                return null;
            }

            int depth = 0;
            bool inString = false;
            bool escaped = false;

            for (int i = startBrace; i < text.Length; i++)
            {
                char c = text[i];
                if (inString)
                {
                    if (escaped)
                    {
                        escaped = false;
                    }
                    else if (c == '\\')
                    {
                        escaped = true;
                    }
                    else if (c == '"')
                    {
                        inString = false;
                    }

                    continue;
                }

                if (c == '"')
                {
                    inString = true;
                }
                else if (c == '{')
                {
                    depth++;
                }
                else if (c == '}')
                {
                    depth--;
                    if (depth == 0)
                    {
                        return text.Substring(startBrace, i - startBrace + 1);
                    }
                }
            }

            return null;
        }

        private async Task<MediaSourceWithChapters?> LoadAndValidateMediaSourceAsync(
            string jsonFilePath, CancellationToken cancellationToken)
        {
            if (!File.Exists(jsonFilePath))
            {
                Common.LogHelper.Warn(_logger, $"JSON file not found: {jsonFilePath}");
                return null;
            }

            List<MediaSourceWithChapters>? mediaSourcesWithChapters;
            try
            {
                var jsonContent = await File.ReadAllTextAsync(jsonFilePath, cancellationToken).ConfigureAwait(false);
                mediaSourcesWithChapters = _jsonSerializer.DeserializeFromString<List<MediaSourceWithChapters>>(jsonContent);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                Common.LogHelper.ErrorException(_logger, $"JSON deserialization failed for {jsonFilePath}. File may be corrupted.", ex);
                MarkInvalidJsonFile(jsonFilePath, "JSON cannot be deserialized");
                return null;
            }

            if (mediaSourcesWithChapters == null)
            {
                return MarkInvalidJsonFile(jsonFilePath, "deserialized value is null");
            }

            if (mediaSourcesWithChapters.Count == 0)
            {
                return MarkInvalidJsonFile(jsonFilePath, "media source list is empty");
            }

            var mediaSourceWithChapters = mediaSourcesWithChapters[0];
            if (mediaSourceWithChapters?.MediaSourceInfo == null)
            {
                return MarkInvalidJsonFile(jsonFilePath, "first media source has no MediaSourceInfo");
            }

            var mediaSourceInfo = mediaSourceWithChapters.MediaSourceInfo;
            if (!mediaSourceInfo.RunTimeTicks.HasValue)
            {
                return MarkInvalidJsonFile(jsonFilePath, "MediaSourceInfo.RunTimeTicks is missing");
            }

            if (string.IsNullOrWhiteSpace(mediaSourceInfo.Container))
            {
                return MarkInvalidJsonFile(jsonFilePath, "MediaSourceInfo.Container is missing");
            }

            if (mediaSourceInfo.Size.GetValueOrDefault() <= 0)
            {
                return MarkInvalidJsonFile(jsonFilePath, "MediaSourceInfo.Size is missing or not positive");
            }

            if (mediaSourceInfo.MediaStreams == null ||
                !mediaSourceInfo.MediaStreams.Any(stream =>
                    stream.Type == MediaStreamType.Video || stream.Type == MediaStreamType.Audio))
            {
                return MarkInvalidJsonFile(jsonFilePath, "MediaSourceInfo.MediaStreams has no video or audio stream");
            }

            UpdateAudioTagCache(jsonFilePath, mediaSourceWithChapters.AudioMetadata);
            return mediaSourceWithChapters;
        }

        private async Task RestoreMediaDataAsync(
            BaseItem item, MediaSourceWithChapters mediaSourceWithChapters,
            CancellationToken cancellationToken)
        {
            RestoreMediaStreams(item, mediaSourceWithChapters, cancellationToken);
            await RestoreAudioEmbeddedImageAsync(item, mediaSourceWithChapters, cancellationToken).ConfigureAwait(false);
            if (mediaSourceWithChapters.MediaSourceInfo != null)
            {
                MediaInfoHelper.ApplyMediaSourceInfo(
                    item,
                    mediaSourceWithChapters.MediaSourceInfo,
                    _libraryManager,
                    cancellationToken,
                    mediaSourceWithChapters.AudioMetadata);
            }

            RestoreChapters(item, mediaSourceWithChapters);
            RestoreEpisodeInfo(item, mediaSourceWithChapters);
        }

        private void RestoreMediaStreams(
            BaseItem item, MediaSourceWithChapters mediaSourceWithChapters, CancellationToken cancellationToken)
        {
            if (mediaSourceWithChapters.MediaSourceInfo?.MediaStreams != null)
            {
                var streamsToSave = AudioMetadataHelper.PrepareMediaStreamsForSave(
                    item, mediaSourceWithChapters.MediaSourceInfo.MediaStreams);
                _itemRepository.SaveMediaStreams(item.InternalId, streamsToSave, cancellationToken);
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
                            Common.LogHelper.Debug(_logger, $"Could not delete existing image: {ioEx.Message}");
                        }
                        catch (UnauthorizedAccessException authEx)
                        {
                            Common.LogHelper.Warn(_logger, $"Permission denied when deleting image: {authEx.Message}");
                        }
                    }

                    await File.WriteAllBytesAsync(imagePath, imageBytes, cancellationToken).ConfigureAwait(false);
                    Common.LogHelper.Debug(_logger, $"Restored embedded image for audio file {item.Name}");
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    Common.LogHelper.ErrorException(_logger, "Error restoring audio embedded image", ex);
                }
            }
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
        /// 使文件名安全，替换无效字符（单遍扫描）
        /// </summary>
        private static string MakeSafeFilename(string name)
        {
            var invalidChars = new HashSet<char>(Path.GetInvalidFileNameChars());
            var sb = new StringBuilder(name.Length);
            foreach (char c in name)
            {
                sb.Append(invalidChars.Contains(c) ? '_' : c);
            }

            return sb.ToString();
        }

        private MediaSourceWithChapters? MarkInvalidJsonFile(string jsonFilePath, string reason)
        {
            Common.LogHelper.Warn(_logger, $"Invalid media info JSON ({reason}): {jsonFilePath}");

            try
            {
                if (!File.Exists(jsonFilePath))
                {
                    return null;
                }

                var backupPath = $"{jsonFilePath}.bak";
                var suffix = 1;
                while (File.Exists(backupPath))
                {
                    backupPath = $"{jsonFilePath}.{suffix++}.bak";
                }

                File.Move(jsonFilePath, backupPath);
                Common.LogHelper.Warn(_logger, $"Invalid media info JSON moved to: {backupPath}");
            }
            catch (Exception ex)
            {
                Common.LogHelper.Warn(_logger, $"Could not move invalid media info JSON to a .bak file: {ex.Message}");
            }

            return null;
        }
    }

    public class MediaSourceWithChapters
    {
        public MediaSourceInfo? MediaSourceInfo { get; set; }
        public List<ChapterInfo> Chapters { get; set; } = new List<ChapterInfo>();
        public bool? ZeroFingerprintConfidence { get; set; }
        public string? EmbeddedImage { get; set; }
        public AudioMetadataInfo? AudioMetadata { get; set; }
    }
}
