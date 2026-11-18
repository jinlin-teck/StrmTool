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
using MediaBrowser.Model.Dto;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.Logging;
using MediaBrowser.Model.MediaInfo;

namespace StrmTool.Common
{
    /// <summary>
    /// 把单个媒体项的媒体信息导出为 -mediainfo.json。
    /// </summary>
    public class MediaInfoExporter
    {
        private readonly ILogger _logger;
        private readonly ILibraryManager _libraryManager;
        private readonly IItemRepository _itemRepository;
        private readonly MediaInfoJsonStore _jsonStore;

        public MediaInfoExporter(
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
                var config = Plugin.GetSafeConfiguration();
                var customBackupDirectory = config.CustomBackupDirectory;
                var jsonFilePath = _jsonStore.GetWriteJsonPath(item, customBackupDirectory);
                var gate = MediaInfoJsonStore.GetWriteCoordinationLock(jsonFilePath);
                await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
                try
                {
                    var mediaSourcesWithChapters = await PrepareMediaSourcesForExportAsync(
                        item, config, cancellationToken, audioTagsProbed).ConfigureAwait(false);
                    if (mediaSourcesWithChapters.Count == 0)
                    {
                        Common.LogHelper.Warn(_logger, $"No media sources available for {item.Name}, skipping export");
                        return false;
                    }

                    await _jsonStore.WriteAsync(jsonFilePath, mediaSourcesWithChapters, cancellationToken).ConfigureAwait(false);
                    Common.LogHelper.Debug(_logger, $"Exported {item.Name} → {jsonFilePath}");
                    return true;
                }
                finally
                {
                    gate.Release();
                }
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
            PluginConfiguration config,
            CancellationToken cancellationToken,
            bool audioTagsProbed = false)
        {
            var libraryOptions = _libraryManager.GetLibraryOptions(item);
            if (libraryOptions == null)
            {
                Common.LogHelper.Warn(_logger, $"Library options is null for item: {item.Name}, skipping export");
                return new List<MediaSourceWithChapters>();
            }

            // 仅导出当前 .strm 条目自身的媒体源，不混入多版本合并中的其他版本，避免恢复时规格错配
            var mediaSources = item.GetMediaSources(false, false, libraryOptions);
            var sanitizedChapters = CreateSanitizedChapters(_itemRepository.GetChapters(item));
            var embeddedImage = await LoadEmbeddedImageAsync(item, config.CustomBackupDirectory, cancellationToken)
                .ConfigureAwait(false);
            bool? zeroFingerprintConfidence = item is Episode
                ? !string.IsNullOrEmpty(_itemRepository.GetIntroDetectionFailureResult(item.InternalId))
                : (bool?)null;
            bool isMusicAudio = item is Audio && AudioMetadataHelper.IsMusicLibraryAudio(item, _libraryManager);
            bool preserveAudioMetadata = isMusicAudio && !config.EnableMusicMetadata;
            var readJsonPath = _jsonStore.GetJsonPath(item, config.CustomBackupDirectory);
            MediaSourceWithChapters? previous = null;
            if (sanitizedChapters.Count == 0 || (item is Audio && embeddedImage == null) || preserveAudioMetadata)
            {
                var existing = await _jsonStore.ReadDocumentAsync<List<MediaSourceWithChapters>>(
                    readJsonPath, cancellationToken).ConfigureAwait(false);
                if (existing.Found)
                {
                    if (existing.Error != null || existing.Value == null ||
                        existing.Value.Count == 0 || existing.Value[0] == null)
                    {
                        // 先保留损坏备份，再允许从数据库重建；隔离失败时不能覆盖原文件。
                        var state = existing.State!;
                        if (!await _jsonStore.TryQuarantineAsync(
                            readJsonPath, state.Length, state.LastWriteTicks,
                            "Cannot preserve existing media info").ConfigureAwait(false))
                        {
                            throw new InvalidDataException($"Cannot preserve existing media info from '{readJsonPath}'.", existing.Error);
                        }
                    }
                    else
                    {
                        previous = existing.Value[0];
                    }
                }

                if (previous == null)
                {
                    // 主备份损坏时，同目录旧备份可能仍有效：隔离主备份后先尝试从旧备份保留章节/封面/标签，
                    // 避免集中目录重建后旧备份被遮蔽、其独有数据永久失效。
                    var sidecarPath = _jsonStore.GetSidecarJsonPath(item);
                    if (!MediaInfoJsonStore.PathStringComparer.Equals(sidecarPath, readJsonPath))
                    {
                        var sidecar = await _jsonStore.ReadDocumentAsync<List<MediaSourceWithChapters>>(
                            sidecarPath, cancellationToken).ConfigureAwait(false);
                        if (sidecar.Found && sidecar.Error == null &&
                            MediaInfoJsonStore.GetValidationFailure(sidecar.Value) == null)
                        {
                            previous = sidecar.Value![0];
                        }
                    }
                }
            }

            if (sanitizedChapters.Count == 0 && previous?.Chapters != null)
            {
                sanitizedChapters = previous.Chapters;
            }

            if (embeddedImage == null && !string.IsNullOrEmpty(previous?.EmbeddedImage))
            {
                embeddedImage = (previous.EmbeddedImage!, previous.EmbeddedImageExtension!);
            }

            AudioMetadataInfo? audioMetadata = null;
            if (preserveAudioMetadata)
            {
                // 关闭功能时不从可能不完整的数据库重建标签，也不凭空标记为已探测。
                audioMetadata = previous?.AudioMetadata;
            }
            else if (isMusicAudio && item is Audio audio)
            {
                bool effectiveAudioTagsProbed = audioTagsProbed ||
                                                _jsonStore.HasProbedAudioTags(readJsonPath);
                audioMetadata = AudioMetadataHelper.BuildAudioMetadataForExport(
                    audio, _libraryManager, effectiveAudioTagsProbed);
            }

            var mediaSourcesWithChapters = new List<MediaSourceWithChapters>(mediaSources.Count);
            foreach (var mediaSource in mediaSources)
            {
                var jsonItem = new MediaSourceWithChapters
                {
                    MediaSourceInfo = SanitizeMediaSourceInfo(mediaSource),
                    Chapters = sanitizedChapters,
                    ZeroFingerprintConfidence = zeroFingerprintConfidence,
                    AudioMetadata = audioMetadata,
                    EmbeddedImage = embeddedImage?.Base64,
                    EmbeddedImageExtension = embeddedImage?.Extension
                };

                mediaSourcesWithChapters.Add(jsonItem);
            }

            return mediaSourcesWithChapters;
        }

        private async Task<(string Base64, string Extension)?> LoadEmbeddedImageAsync(
            BaseItem item, string? customBackupDirectory, CancellationToken cancellationToken)
        {
            if (!(item is Audio))
            {
                return null;
            }

            var candidatePath = ResolveAudioImageFilePath(item, customBackupDirectory);
            if (string.IsNullOrWhiteSpace(candidatePath))
            {
                return null;
            }

            var imageBytes = await File.ReadAllBytesAsync(candidatePath, cancellationToken).ConfigureAwait(false);
            if (imageBytes.Length == 0)
            {
                return null;
            }

            var extension = MediaInfoRestorer.NormalizeImageExtension(Path.GetExtension(candidatePath));
            return (Convert.ToBase64String(imageBytes), extension);
        }

        private string? ResolveAudioImageFilePath(BaseItem item, string? customBackupDirectory)
        {
            var primaryImageInfo = item.GetImageInfo(ImageType.Primary, 0);
            if (primaryImageInfo != null &&
                !string.IsNullOrWhiteSpace(primaryImageInfo.Path) &&
                File.Exists(primaryImageInfo.Path))
            {
                return primaryImageInfo.Path;
            }

            // 兜底查找集中备份或同目录 .metadata/{baseName}.* 已落盘的封面，防止数据库尚未刷新图片索引时升级 JSON 抹除封面
            var baseName = MediaInfoRestorer.GetAudioImageBaseName(item);
            var primaryMetaDir = _jsonStore.GetAudioMetadataDirectory(item, customBackupDirectory);
            var fallbackMetaDir = _jsonStore.GetSidecarAudioMetadataDirectory(item);
            var candidateDirs = MediaInfoJsonStore.PathStringComparer.Equals(primaryMetaDir, fallbackMetaDir)
                ? new[] { primaryMetaDir }
                : new[] { primaryMetaDir, fallbackMetaDir };

            foreach (var metadataDir in candidateDirs)
            {
                if (string.IsNullOrWhiteSpace(metadataDir) || !Directory.Exists(metadataDir))
                {
                    continue;
                }

                foreach (var ext in MediaInfoRestorer.SupportedAudioImageExtensions)
                {
                    var fallbackPath = Path.Combine(metadataDir, $"{baseName}{ext}");
                    if (File.Exists(fallbackPath))
                    {
                        return fallbackPath;
                    }
                }
            }

            return null;
        }

        private static MediaSourceInfo? SanitizeMediaSourceInfo(MediaSourceInfo? source)
        {
            if (source == null)
            {
                return null;
            }

            source.Id = null;
            source.ItemId = null;
            source.Path = null;

            if (source.MediaStreams != null)
            {
                var streamsCopy = new List<MediaStream>(source.MediaStreams.Count);
                foreach (var stream in source.MediaStreams)
                {
                    if (stream == null)
                    {
                        continue;
                    }

                    if (stream.IsExternal &&
                        stream.Type == MediaStreamType.Subtitle &&
                        stream.Protocol == MediaProtocol.File &&
                        !string.IsNullOrWhiteSpace(stream.Path))
                    {
                        streamsCopy.Add(CloneExternalSubtitleWithFileNameOnly(stream));
                    }
                    else
                    {
                        streamsCopy.Add(stream);
                    }
                }

                source.MediaStreams = streamsCopy;
            }

            return source;
        }

        private static MediaStream CloneExternalSubtitleWithFileNameOnly(MediaStream source)
        {
            return new MediaStream
            {
                Index = source.Index,
                Type = source.Type,
                IsExternal = source.IsExternal,
                Path = Path.GetFileName(source.Path),
                Protocol = source.Protocol,
                Codec = source.Codec,
                Language = source.Language,
                Title = source.Title,
                DisplayTitle = source.DisplayTitle,
                DisplayLanguage = source.DisplayLanguage,
                IsDefault = source.IsDefault,
                IsForced = source.IsForced,
                IsHearingImpaired = source.IsHearingImpaired,
                SupportsExternalStream = source.SupportsExternalStream
            };
        }

        private static List<ChapterInfo> CreateSanitizedChapters(List<ChapterInfo>? chapters)
        {
            if (chapters == null || chapters.Count == 0)
            {
                return new List<ChapterInfo>();
            }

            var sanitized = new List<ChapterInfo>(chapters.Count);
            foreach (var chapter in chapters)
            {
                if (chapter == null)
                {
                    continue;
                }

                sanitized.Add(new ChapterInfo
                {
                    StartPositionTicks = chapter.StartPositionTicks,
                    Name = chapter.Name,
                    MarkerType = chapter.MarkerType,
                    ChapterIndex = chapter.ChapterIndex,
                    ImageTag = null,
                    ImagePath = null,
                    ImageDateModified = default
                });
            }

            return sanitized;
        }
    }
}
