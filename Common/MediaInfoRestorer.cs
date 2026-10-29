using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Audio;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Persistence;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.Logging;

namespace StrmTool.Common
{
    /// <summary>
    /// 从 -mediainfo.json 恢复单个媒体项的媒体信息（纯本地操作）。
    /// </summary>
    public class MediaInfoRestorer
    {
        /// <summary>
        /// 音频内嵌封面可能使用的扩展名（恢复时清理旧格式残留）
        /// </summary>
        private static readonly string[] ImageExtensions = new[] { ".jpg", ".jpeg", ".png", ".webp", ".tbn" };

        private readonly ILogger _logger;
        private readonly ILibraryManager _libraryManager;
        private readonly IItemRepository _itemRepository;
        private readonly MediaInfoJsonStore _jsonStore;

        public MediaInfoRestorer(
            ILogger logger,
            ILibraryManager libraryManager,
            IItemRepository itemRepository,
            MediaInfoJsonStore jsonStore)
        {
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
            _libraryManager = libraryManager ?? throw new ArgumentNullException(nameof(libraryManager));
            _itemRepository = itemRepository ?? throw new ArgumentNullException(nameof(itemRepository));
            _jsonStore = jsonStore ?? throw new ArgumentNullException(nameof(jsonStore));
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

                var jsonFilePath = _jsonStore.GetJsonPath(item);
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

        private async Task<MediaSourceWithChapters?> LoadAndValidateMediaSourceAsync(
            string jsonFilePath, CancellationToken cancellationToken)
        {
            var readResult = await _jsonStore
                .ReadDocumentAsync<List<MediaSourceWithChapters>>(jsonFilePath, cancellationToken)
                .ConfigureAwait(false);

            if (!readResult.Found)
            {
                Common.LogHelper.Warn(_logger, $"JSON file not found: {jsonFilePath}");
                return null;
            }

            var state = readResult.State!;
            if (readResult.Error != null)
            {
                Common.LogHelper.ErrorException(
                    _logger,
                    $"JSON deserialization failed for {jsonFilePath}. File may be corrupted.",
                    readResult.Error);
                await _jsonStore.TryQuarantineAsync(
                    jsonFilePath, state.Length, state.LastWriteTicks, "JSON cannot be deserialized").ConfigureAwait(false);
                return null;
            }

            var invalidReason = GetValidationFailure(readResult.Value);
            if (invalidReason != null)
            {
                await _jsonStore.TryQuarantineAsync(
                    jsonFilePath, state.Length, state.LastWriteTicks, invalidReason).ConfigureAwait(false);
                return null;
            }

            var mediaSourceWithChapters = readResult.Value![0];
            _jsonStore.UpdateCache(jsonFilePath, mediaSourceWithChapters.AudioMetadata);
            return mediaSourceWithChapters;
        }

        /// <summary>
        /// 只校验恢复所必需的结构（媒体源与音视频流）。
        /// Size / RunTimeTicks 等字段在部分远程源上本就可能缺失，不能据此判定备份损坏。
        /// </summary>
        private static string? GetValidationFailure(List<MediaSourceWithChapters>? mediaSourcesWithChapters)
        {
            if (mediaSourcesWithChapters == null)
            {
                return "deserialized value is null";
            }

            if (mediaSourcesWithChapters.Count == 0)
            {
                return "media source list is empty";
            }

            var first = mediaSourcesWithChapters[0];
            if (first?.MediaSourceInfo == null)
            {
                return "first media source has no MediaSourceInfo";
            }

            var streams = first.MediaSourceInfo.MediaStreams;
            if (streams == null ||
                !streams.Any(stream =>
                    stream.Type == MediaStreamType.Video || stream.Type == MediaStreamType.Audio))
            {
                return "MediaSourceInfo.MediaStreams has no video or audio stream";
            }

            return null;
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
                var streamsToSave = MediaStreamMerger.PrepareMediaStreamsForSave(
                    item, mediaSourceWithChapters.MediaSourceInfo.MediaStreams);
                _itemRepository.SaveMediaStreams(item.InternalId, streamsToSave, cancellationToken);
            }
        }

        private async Task RestoreAudioEmbeddedImageAsync(
            BaseItem item, MediaSourceWithChapters mediaSourceWithChapters, CancellationToken cancellationToken)
        {
            if (!(item is Audio) || string.IsNullOrEmpty(mediaSourceWithChapters.EmbeddedImage))
            {
                return;
            }

            try
            {
                var imageBytes = Convert.FromBase64String(mediaSourceWithChapters.EmbeddedImage);
                var mediaDirectory = Path.GetDirectoryName(item.Path);
                var audioImageDir = Path.Combine(
                    string.IsNullOrWhiteSpace(mediaDirectory) ? Path.GetTempPath() : mediaDirectory,
                    ".metadata");
                Directory.CreateDirectory(audioImageDir);

                var baseName = Path.GetFileNameWithoutExtension(item.Path);
                var extension = NormalizeImageExtension(mediaSourceWithChapters.EmbeddedImageExtension);
                var imagePath = Path.Combine(audioImageDir, $"{baseName}{extension}");

                // 清理同 basename 的其他扩展名残留，避免新旧封面并存导致 Emby 选中旧图
                CleanupOtherImageExtensions(audioImageDir, baseName, extension);

                await AtomicFile.WriteAllBytesAsync(imagePath, imageBytes, cancellationToken).ConfigureAwait(false);
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

        private void CleanupOtherImageExtensions(string directory, string baseName, string extensionToKeep)
        {
            foreach (var extension in ImageExtensions)
            {
                if (string.Equals(extension, extensionToKeep, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                var stalePath = Path.Combine(directory, $"{baseName}{extension}");
                try
                {
                    if (File.Exists(stalePath))
                    {
                        File.Delete(stalePath);
                        Common.LogHelper.Debug(_logger, $"Removed stale audio image: {stalePath}");
                    }
                }
                catch (Exception ex)
                {
                    Common.LogHelper.Debug(_logger, $"Could not remove stale audio image '{stalePath}': {ex.Message}");
                }
            }
        }

        private static string NormalizeImageExtension(string? extension)
        {
            if (string.IsNullOrWhiteSpace(extension))
            {
                return ".jpg";
            }

            var trimmed = extension.Trim();
            if (!trimmed.StartsWith(".", StringComparison.Ordinal))
            {
                trimmed = "." + trimmed;
            }

            return trimmed.Length > 1 && trimmed.Length <= 6 ? trimmed.ToLowerInvariant() : ".jpg";
        }

        private void RestoreChapters(BaseItem item, MediaSourceWithChapters mediaSourceWithChapters)
        {
            // 旧版备份没有 Chapters 字段，反序列化后为空列表；不能用空列表清空数据库里已有的章节
            if (item is MediaBrowser.Controller.Entities.Video &&
                mediaSourceWithChapters.Chapters != null &&
                mediaSourceWithChapters.Chapters.Count > 0)
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
    }
}
