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
                var mediaSourcesWithChapters = await PrepareMediaSourcesForExportAsync(
                    item, cancellationToken, audioTagsProbed).ConfigureAwait(false);
                if (mediaSourcesWithChapters.Count == 0)
                {
                    Common.LogHelper.Warn(_logger, $"No media sources available for {item.Name}, skipping export");
                    return false;
                }

                var jsonFilePath = _jsonStore.GetJsonPath(item);
                await _jsonStore.WriteAsync(jsonFilePath, mediaSourcesWithChapters, cancellationToken).ConfigureAwait(false);
                Common.LogHelper.Debug(_logger, $"Exported {item.Name} → {jsonFilePath}");
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

            // 仅导出当前 .strm 条目自身的媒体源，不混入多版本合并中的其他版本，避免恢复时规格错配
            var mediaSources = item.GetMediaSources(false, false, libraryOptions);
            var sanitizedChapters = CreateSanitizedChapters(_itemRepository.GetChapters(item));
            var embeddedImage = await LoadEmbeddedImageAsync(item, cancellationToken).ConfigureAwait(false);
            bool? zeroFingerprintConfidence = item is Episode
                ? !string.IsNullOrEmpty(_itemRepository.GetIntroDetectionFailureResult(item.InternalId))
                : (bool?)null;
            var audioMetadata = item is Audio audio && AudioMetadataHelper.IsMusicLibraryAudio(audio, _libraryManager)
                ? AudioMetadataHelper.BuildAudioMetadataForExport(audio, _libraryManager, audioTagsProbed)
                : null;

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

        private static async Task<(string Base64, string Extension)?> LoadEmbeddedImageAsync(
            BaseItem item, CancellationToken cancellationToken)
        {
            if (!(item is Audio))
            {
                return null;
            }

            var candidatePath = ResolveAudioImageFilePath(item);
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

        private static string? ResolveAudioImageFilePath(BaseItem item)
        {
            var primaryImageInfo = item.GetImageInfo(ImageType.Primary, 0);
            if (primaryImageInfo != null &&
                !string.IsNullOrWhiteSpace(primaryImageInfo.Path) &&
                File.Exists(primaryImageInfo.Path))
            {
                return primaryImageInfo.Path;
            }

            // 兜底查找同目录 .metadata/{baseName}.* 已落盘的封面，防止数据库尚未刷新图片索引时升级 JSON 抹除封面
            var mediaDirectory = !string.IsNullOrWhiteSpace(item.Path) ? Path.GetDirectoryName(item.Path) : null;
            if (string.IsNullOrWhiteSpace(mediaDirectory))
            {
                return null;
            }

            var metadataDir = Path.Combine(mediaDirectory, ".metadata");
            if (!Directory.Exists(metadataDir))
            {
                return null;
            }

            var baseName = MediaInfoRestorer.GetAudioImageBaseName(item);
            foreach (var ext in MediaInfoRestorer.SupportedAudioImageExtensions)
            {
                var fallbackPath = Path.Combine(metadataDir, $"{baseName}{ext}");
                if (File.Exists(fallbackPath))
                {
                    return fallbackPath;
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
