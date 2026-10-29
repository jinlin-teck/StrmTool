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

            var mediaSources = item.GetMediaSources(true, false, libraryOptions);
            var chapters = _itemRepository.GetChapters(item);

            var mediaSourcesWithChapters = mediaSources.Select(mediaSource =>
                    new MediaSourceWithChapters
                    {
                        MediaSourceInfo = mediaSource,
                        Chapters = chapters
                    })
                .ToList();

            // 同一媒体的封面只读取/编码一次，避免多版本场景重复占用内存
            var embeddedImage = await LoadEmbeddedImageAsync(item, cancellationToken).ConfigureAwait(false);

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

                if (embeddedImage != null)
                {
                    jsonItem.EmbeddedImage = embeddedImage.Value.Base64;
                    jsonItem.EmbeddedImageExtension = embeddedImage.Value.Extension;
                }
            }

            return mediaSourcesWithChapters;
        }

        private async Task<(string Base64, string Extension)?> LoadEmbeddedImageAsync(
            BaseItem item, CancellationToken cancellationToken)
        {
            if (!(item is Audio))
            {
                return null;
            }

            var primaryImageInfo = item.GetImageInfo(ImageType.Primary, 0);
            if (primaryImageInfo == null ||
                string.IsNullOrWhiteSpace(primaryImageInfo.Path) ||
                !File.Exists(primaryImageInfo.Path))
            {
                return null;
            }

            var imageBytes = await File.ReadAllBytesAsync(primaryImageInfo.Path, cancellationToken).ConfigureAwait(false);
            if (imageBytes.Length == 0)
            {
                return null;
            }

            var extension = Path.GetExtension(primaryImageInfo.Path);
            if (string.IsNullOrWhiteSpace(extension))
            {
                extension = ".jpg";
            }

            return (Convert.ToBase64String(imageBytes), extension.ToLowerInvariant());
        }

        private void SanitizeMediaSourceInfo(MediaSourceWithChapters jsonItem)
        {
            if (jsonItem.MediaSourceInfo == null)
                return;

            jsonItem.MediaSourceInfo.Id = null;
            jsonItem.MediaSourceInfo.ItemId = null;
            jsonItem.MediaSourceInfo.Path = null;

            if (jsonItem.MediaSourceInfo.MediaStreams == null)
                return;

            foreach (var subtitle in jsonItem.MediaSourceInfo.MediaStreams.Where(m =>
                         m.IsExternal && m.Type == MediaStreamType.Subtitle &&
                         m.Protocol == MediaProtocol.File))
            {
                subtitle.Path = Path.GetFileName(subtitle.Path);
            }
        }

        private void SanitizeChapters(MediaSourceWithChapters jsonItem)
        {
            if (jsonItem.Chapters == null)
                return;

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
    }
}
