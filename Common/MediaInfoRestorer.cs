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

                var config = Plugin.GetSafeConfiguration();
                var customBackupDirectory = config.CustomBackupDirectory;
                var primary = _jsonStore.GetWriteJsonPath(item, customBackupDirectory);
                var sidecar = _jsonStore.GetSidecarJsonPath(item);
                var candidates = MediaInfoJsonStore.PathStringComparer.Equals(primary, sidecar)
                    ? new[] { primary }
                    : new[] { primary, sidecar };

                foreach (var jsonFilePath in candidates)
                {
                    if (!File.Exists(jsonFilePath))
                    {
                        continue;
                    }

                    MediaSourceWithChapters? mediaSourceWithChapters;
                    try
                    {
                        mediaSourceWithChapters = await LoadAndValidateMediaSourceAsync(jsonFilePath, cancellationToken)
                            .ConfigureAwait(false);
                    }
                    catch (OperationCanceledException)
                    {
                        throw;
                    }
                    catch (Exception ex)
                    {
                        Common.LogHelper.ErrorException(_logger, $"Error reading backup {jsonFilePath}", ex);
                        continue;
                    }

                    if (mediaSourceWithChapters == null)
                    {
                        continue;
                    }

                    await RestoreMediaDataAsync(item, mediaSourceWithChapters, config, cancellationToken).ConfigureAwait(false);
                    Common.LogHelper.Info(_logger, $"Restore completed: {item.Name} ← {jsonFilePath}");
                    return true;
                }

                Common.LogHelper.Warn(_logger, $"No usable JSON backup for {item.Name}");
                return false;
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
        /// 在音乐标签探测判定前检查主备份：若其损坏或不可读，立即尝试同目录备份。
        /// </summary>
        internal async Task<bool> TryRestoreFromSidecarIfPrimaryInvalidAsync(
            BaseItem item, CancellationToken cancellationToken)
        {
            var customBackupDirectory = Plugin.GetSafeConfiguration().CustomBackupDirectory;
            var primary = _jsonStore.GetWriteJsonPath(item, customBackupDirectory);
            var sidecar = _jsonStore.GetSidecarJsonPath(item);
            if (MediaInfoJsonStore.PathStringComparer.Equals(primary, sidecar) ||
                !File.Exists(primary) || !File.Exists(sidecar))
            {
                return false;
            }

            try
            {
                if (await LoadAndValidateMediaSourceAsync(primary, cancellationToken).ConfigureAwait(false) != null)
                {
                    return false;
                }
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                Common.LogHelper.ErrorException(_logger, $"Error reading backup {primary}", ex);
            }

            return await RestoreItemAsync(item, cancellationToken).ConfigureAwait(false);
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

            var invalidReason = MediaInfoJsonStore.GetValidationFailure(readResult.Value);
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

        private async Task RestoreMediaDataAsync(
            BaseItem item,
            MediaSourceWithChapters mediaSourceWithChapters,
            PluginConfiguration config,
            CancellationToken cancellationToken)
        {
            RestoreMediaStreams(item, mediaSourceWithChapters, cancellationToken);
            if (config.EnableMusicMetadata)
            {
                await RestoreAudioEmbeddedImageAsync(
                    item, mediaSourceWithChapters, config.CustomBackupDirectory, cancellationToken).ConfigureAwait(false);
            }
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
            BaseItem item,
            MediaSourceWithChapters mediaSourceWithChapters,
            string? customBackupDirectory,
            CancellationToken cancellationToken)
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

                var audioImageDir = _jsonStore.GetAudioMetadataDirectory(item, customBackupDirectory);
                var sidecarImageDir = _jsonStore.GetSidecarAudioMetadataDirectory(item);
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

                BindRestoredPrimaryImageIfNeeded(item, audioImageDir, sidecarImageDir, baseName, imagePath);
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
            BaseItem item,
            string audioImageDir,
            string sidecarImageDir,
            string baseName,
            string restoredImagePath)
        {
            var existingPrimary = item.GetImageInfo(ImageType.Primary, 0);
            bool shouldBind = existingPrimary == null ||
                              string.IsNullOrWhiteSpace(existingPrimary.Path) ||
                              !File.Exists(existingPrimary.Path) ||
                              IsManagedAudioMetadataImage(existingPrimary.Path, audioImageDir, baseName) ||
                              IsManagedAudioMetadataImage(existingPrimary.Path, sidecarImageDir, baseName);
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
