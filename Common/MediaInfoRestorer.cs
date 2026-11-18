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
        /// 音频内嵌封面允许使用的安全图片扩展名白名单（对齐 Emby 支持的图片扩展名）
        /// </summary>
        internal static readonly string[] SupportedAudioImageExtensions = new[]
        {
            ".jpg", ".jpeg", ".png", ".webp", ".avif", ".gif", ".tbn"
        };

        private static readonly HashSet<string> AllowedImageExtensionSet = new HashSet<string>(
            SupportedAudioImageExtensions,
            StringComparer.OrdinalIgnoreCase);

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
                    stream != null &&
                    (stream.Type == MediaStreamType.Video || stream.Type == MediaStreamType.Audio)))
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
                    item,
                    mediaSourceWithChapters.MediaSourceInfo.MediaStreams,
                    _libraryManager,
                    untrustedIncomingPaths: true);
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
                if (imageBytes.Length == 0)
                {
                    return;
                }

                var mediaDirectory = Path.GetDirectoryName(item.Path);
                var audioImageDir = Path.Combine(
                    string.IsNullOrWhiteSpace(mediaDirectory) ? Path.GetTempPath() : mediaDirectory,
                    ".metadata");
                Directory.CreateDirectory(audioImageDir);

                var baseName = GetAudioImageBaseName(item);
                var extension = NormalizeImageExtension(mediaSourceWithChapters.EmbeddedImageExtension);
                var imagePath = Path.Combine(audioImageDir, $"{baseName}{extension}");

                var gate = MediaInfoJsonStore.GetPathLockForPath(Path.Combine(audioImageDir, baseName));
                await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
                try
                {
                    // 清理同 basename 的其他扩展名残留，避免新旧封面并存导致 Emby 选中旧图
                    CleanupOtherImageExtensions(audioImageDir, baseName, extension);
                    await AtomicFile.WriteAllBytesAsync(imagePath, imageBytes, cancellationToken).ConfigureAwait(false);
                }
                finally
                {
                    gate.Release();
                }

                BindRestoredPrimaryImageIfNeeded(item, audioImageDir, baseName, imagePath);
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

        internal static string GetAudioImageBaseName(BaseItem item)
        {
            var baseName = !string.IsNullOrWhiteSpace(item.Path)
                ? Path.GetFileNameWithoutExtension(item.Path)
                : null;
            return string.IsNullOrWhiteSpace(baseName)
                ? MediaInfoJsonStore.MakeSafeFilename(item.Name)
                : baseName;
        }

        private static void BindRestoredPrimaryImageIfNeeded(
            BaseItem item, string audioImageDir, string baseName, string restoredImagePath)
        {
            var existingPrimary = item.GetImageInfo(ImageType.Primary, 0);
            bool shouldBind = existingPrimary == null ||
                              string.IsNullOrWhiteSpace(existingPrimary.Path) ||
                              !File.Exists(existingPrimary.Path) ||
                              IsManagedAudioMetadataImage(existingPrimary.Path, audioImageDir, baseName);
            if (!shouldBind)
            {
                return;
            }

            var fileInfo = new FileInfo(restoredImagePath);
            var lastModified = fileInfo.Exists
                ? new DateTimeOffset(fileInfo.LastWriteTimeUtc, TimeSpan.Zero)
                : DateTimeOffset.UtcNow;

            item.SetImage(
                new ItemImageInfo
                {
                    Path = restoredImagePath,
                    Type = ImageType.Primary,
                    DateModified = lastModified
                },
                0,
                false);
        }

        private static bool IsManagedAudioMetadataImage(string imagePath, string audioImageDir, string baseName)
        {
            var dir = Path.GetDirectoryName(imagePath);
            var nameNoExt = Path.GetFileNameWithoutExtension(imagePath);
            return !string.IsNullOrEmpty(dir) &&
                   MediaInfoJsonStore.PathStringComparer.Equals(
                       MediaInfoJsonStore.TrimEndingDirectorySeparatorPreservingRoot(dir),
                       MediaInfoJsonStore.TrimEndingDirectorySeparatorPreservingRoot(audioImageDir)) &&
                   MediaInfoJsonStore.PathStringComparer.Equals(nameNoExt, baseName);
        }

        private void CleanupOtherImageExtensions(string directory, string baseName, string extensionToKeep)
        {
            foreach (var extension in SupportedAudioImageExtensions)
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

        internal static string NormalizeImageExtension(string? extension)
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

            var normalized = trimmed.ToLowerInvariant();
            return AllowedImageExtensionSet.Contains(normalized) ? normalized : ".jpg";
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
