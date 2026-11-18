using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Audio;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Dto;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.MediaInfo;

namespace StrmTool.Common
{
    /// <summary>
    /// Emby 音频 STRM 元数据提取、标准目录/文件名回退、外挂歌词合并与 JSON 备份恢复辅助类。
    /// 文件名/目录兜底解析由 AudioFallbackMetadataResolver 承担，媒体流合并由 MediaStreamMerger 承担。
    /// </summary>
    public static class AudioMetadataHelper
    {
        private const int MinValidProductionYear = 1800;
        private const int MaxValidProductionYear = 9999;

        /// <summary>
        /// 判断条目是否为音乐媒体库（ContentType == "music"）中的独立音频曲目
        /// （排除电影/剧集文件夹附带的 ThemeSong 等 Extra 音频，以及非音乐媒体库中的音频）
        /// </summary>
        public static bool IsMusicLibraryAudio(BaseItem? item, ILibraryManager? libraryManager = null)
        {
            if (!(item is Audio audio) || audio.ExtraType.HasValue)
            {
                return false;
            }

            var manager = libraryManager ?? BaseItem.LibraryManager;
            var contentType = manager?.GetLibraryOptions(audio)?.ContentType;
            if (string.IsNullOrWhiteSpace(contentType))
            {
                return false;
            }

            return MemoryExtensions.Equals(
                CollectionType.Music.Span,
                contentType.AsSpan(),
                StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>
        /// 纯数据检查：判断音频实体自身的核心音乐字段（专辑、艺术家、专辑艺术家）是否缺失（不考虑 IsLocked）
        /// </summary>
        public static bool HasIncompleteCoreMetadata(Audio? audio)
        {
            if (audio == null)
            {
                return true;
            }

            return string.IsNullOrWhiteSpace(audio.Album) ||
                   audio.Artists == null || audio.Artists.Length == 0 ||
                   audio.AlbumArtists == null || audio.AlbumArtists.Length == 0;
        }

        /// <summary>
        /// 检查音乐库音频项是否需要补充核心音乐元数据（已锁定条目视为无需修改）
        /// </summary>
        public static bool HasMissingAudioMetadata(BaseItem? item, ILibraryManager? libraryManager = null)
        {
            if (!IsMusicLibraryAudio(item, libraryManager))
            {
                return false;
            }

            var audio = (Audio)item!;
            if (audio.IsLocked)
            {
                return false;
            }

            return HasIncompleteCoreMetadata(audio);
        }

        /// <summary>
        /// 检查音乐库音频项同目录是否存在尚未入库的外挂歌词文件（.lrc / .elrc）
        /// </summary>
        public static bool HasMissingLocalLyrics(
            BaseItem? item,
            IEnumerable<MediaStream>? currentStreams = null,
            ILibraryManager? libraryManager = null)
        {
            if (!IsMusicLibraryAudio(item, libraryManager))
            {
                return false;
            }

            var audio = (Audio)item!;
            if (audio.IsLocked || string.IsNullOrWhiteSpace(audio.Path))
            {
                return false;
            }

            var localLyrics = MediaStreamMerger.FindLocalLyricFiles(audio.Path);
            if (localLyrics.Count == 0)
            {
                return false;
            }

            var streams = currentStreams ?? audio.GetMediaStreams();
            if (streams == null)
            {
                return true;
            }

            var existingFileNames = new HashSet<string>(
                streams.Where(s => s != null &&
                                   s.IsExternal &&
                                   s.Type == MediaStreamType.Subtitle &&
                                   !string.IsNullOrWhiteSpace(s.Path))
                       .Select(s => Path.GetFileName(s.Path)),
                StringComparer.OrdinalIgnoreCase);

            return localLyrics.Any(path => !existingFileNames.Contains(Path.GetFileName(path)));
        }

        /// <summary>
        /// 从远程探测结果 MediaInfo 中提取音频标签，结合目录/文件名兜底后应用到 Audio 实体
        /// </summary>
        public static void ApplyAudioMetadataFromProbe(
            Audio audio,
            MediaInfo? mediaInfo,
            ILibraryManager libraryManager)
        {
            if (!IsMusicLibraryAudio(audio, libraryManager))
            {
                return;
            }

            AudioMetadataInfo? probed = null;
            if (mediaInfo != null)
            {
                var composers = MetadataNormalizer.NormalizeList(mediaInfo.Composers);
                if (mediaInfo.People != null && mediaInfo.People.Length > 0)
                {
                    var writers = mediaInfo.People
                        .Where(p => p != null && p.Type == PersonType.Writer && !string.IsNullOrWhiteSpace(p.Name))
                        .Select(p => p.Name);
                    composers = MetadataNormalizer.NormalizeList(
                        (composers ?? Enumerable.Empty<string>()).Concat(writers));
                }

                int? rawYear = mediaInfo.ProductionYear ?? mediaInfo.PremiereDate?.Year;
                var providerIds = MetadataNormalizer.NormalizeProviderIds(mediaInfo.ProviderIds);
                DateTimeOffset? validPremiereDate = mediaInfo.PremiereDate.HasValue &&
                                                    IsValidProductionYear(mediaInfo.PremiereDate.Value.Year)
                    ? mediaInfo.PremiereDate
                    : null;

                probed = new AudioMetadataInfo
                {
                    AudioTagsProbed = true,
                    Title = MetadataNormalizer.NormalizeString(mediaInfo.Name),
                    SortName = MetadataNormalizer.NormalizeString(mediaInfo.SortName),
                    Album = MetadataNormalizer.NormalizeString(mediaInfo.Album),
                    Artists = MetadataNormalizer.NormalizeList(mediaInfo.Artists),
                    AlbumArtists = MetadataNormalizer.NormalizeList(mediaInfo.AlbumArtists),
                    Composers = composers,
                    Genres = MetadataNormalizer.NormalizeList(mediaInfo.Genres),
                    TrackNumber = mediaInfo.IndexNumber > 0 ? mediaInfo.IndexNumber : null,
                    DiscNumber = mediaInfo.ParentIndexNumber > 0 ? mediaInfo.ParentIndexNumber : null,
                    ProductionYear = rawYear.HasValue && IsValidProductionYear(rawYear.Value) ? rawYear : null,
                    PremiereDate = validPremiereDate,
                    ProviderIds = providerIds
                };
            }

            var effective = MergeWithFallback(audio, probed, libraryManager);
            ApplyEffectiveMetadata(audio, effective);
        }

        /// <summary>
        /// 从 JSON 缓存的 AudioMetadataInfo（或旧版 JSON 缺失时的目录/文件名回退）恢复音频元数据到 Audio 实体
        /// </summary>
        public static void ApplyAudioMetadataFromCache(
            Audio audio,
            AudioMetadataInfo? cached,
            ILibraryManager libraryManager)
        {
            if (!IsMusicLibraryAudio(audio, libraryManager))
            {
                return;
            }

            var effective = MergeWithFallback(audio, cached, libraryManager);
            ApplyEffectiveMetadata(audio, effective);
        }

        /// <summary>
        /// 判断结合缓存与目录/文件名兜底后，是否能为当前 Audio 补齐缺失的核心音频元数据。
        /// 用于区分“有元数据可恢复”与“已探测但源文件/目录均无专辑信息（终态收敛）”。
        /// </summary>
        public static bool CanRestoreMissingAudioMetadata(
            Audio audio,
            AudioMetadataInfo? cached,
            ILibraryManager? libraryManager)
        {
            if (!IsMusicLibraryAudio(audio, libraryManager) || audio.IsLocked)
            {
                return false;
            }

            var effective = MergeWithFallback(audio, cached, libraryManager);

            if (string.IsNullOrWhiteSpace(audio.Album) && !string.IsNullOrWhiteSpace(effective.Album))
            {
                return true;
            }

            if ((audio.Artists == null || audio.Artists.Length == 0) &&
                effective.Artists != null && effective.Artists.Count > 0)
            {
                return true;
            }

            if ((audio.AlbumArtists == null || audio.AlbumArtists.Length == 0) &&
                effective.AlbumArtists != null && effective.AlbumArtists.Count > 0)
            {
                return true;
            }

            return false;
        }

        /// <summary>
        /// 从当前 Audio 库条目构建用于写入 -mediainfo.json 的 AudioMetadataInfo
        /// </summary>
        public static AudioMetadataInfo BuildAudioMetadataForExport(
            Audio audio,
            ILibraryManager libraryManager,
            bool audioTagsProbed = false)
        {
            if (audio == null)
            {
                return new AudioMetadataInfo();
            }

            var fallback = AudioFallbackMetadataResolver.Resolve(audio, libraryManager);
            // 被锁定的缺字段曲目不得在未实际探测时误标为 AudioTagsProbed = true，确保解锁后仍可补探
            bool markProbed = audioTagsProbed || (!audio.IsLocked && !HasIncompleteCoreMetadata(audio));

            var fileNameWithoutExt = !string.IsNullOrWhiteSpace(audio.Path)
                ? Path.GetFileNameWithoutExtension(audio.Path)
                : null;
            var title = !string.IsNullOrWhiteSpace(audio.Name) &&
                        !string.Equals(audio.Name, fileNameWithoutExt, StringComparison.Ordinal)
                ? audio.Name
                : fallback.Title;

            var album = MetadataNormalizer.NormalizeString(audio.Album) ?? fallback.Album;
            var artists = MetadataNormalizer.NormalizeList(audio.Artists) ?? fallback.Artists;
            var albumArtists = MetadataNormalizer.NormalizeList(audio.AlbumArtists) ?? fallback.AlbumArtists ?? artists;
            if ((artists == null || artists.Count == 0) && albumArtists != null && albumArtists.Count > 0)
            {
                artists = new List<string>(albumArtists);
            }

            return new AudioMetadataInfo
            {
                AudioTagsProbed = markProbed ? (bool?)true : null,
                Title = title,
                SortName = MetadataNormalizer.NormalizeString(audio.SortName),
                Album = album,
                Artists = artists,
                AlbumArtists = albumArtists,
                Composers = MetadataNormalizer.NormalizeList(audio.Composers?.Select(c => c.Name)),
                Genres = MetadataNormalizer.NormalizeList(audio.Genres),
                TrackNumber = audio.IndexNumber ?? fallback.TrackNumber,
                DiscNumber = audio.ParentIndexNumber ?? fallback.DiscNumber,
                ProductionYear = audio.ProductionYear.HasValue && IsValidProductionYear(audio.ProductionYear.Value)
                    ? audio.ProductionYear
                    : null,
                PremiereDate = audio.PremiereDate.HasValue && IsValidProductionYear(audio.PremiereDate.Value.Year)
                    ? audio.PremiereDate
                    : null,
                ProviderIds = MetadataNormalizer.NormalizeProviderIds(audio.ProviderIds)
            };
        }

        private static AudioMetadataInfo MergeWithFallback(
            Audio audio,
            AudioMetadataInfo? source,
            ILibraryManager? libraryManager)
        {
            var fallback = AudioFallbackMetadataResolver.Resolve(audio, libraryManager);

            var title = MetadataNormalizer.NormalizeString(source?.Title) ?? fallback.Title;
            var sortName = MetadataNormalizer.NormalizeString(source?.SortName);
            var album = MetadataNormalizer.NormalizeString(source?.Album) ?? fallback.Album;
            var sourceArtists = MetadataNormalizer.NormalizeList(source?.Artists);
            var sourceAlbumArtists = MetadataNormalizer.NormalizeList(source?.AlbumArtists);

            var albumArtists = (sourceAlbumArtists != null && sourceAlbumArtists.Count > 0)
                ? sourceAlbumArtists
                : ((fallback.AlbumArtists != null && fallback.AlbumArtists.Count > 0) ? fallback.AlbumArtists : sourceArtists);

            var artists = (sourceArtists != null && sourceArtists.Count > 0)
                ? sourceArtists
                : ((fallback.Artists != null && fallback.Artists.Count > 0) ? fallback.Artists : albumArtists);

            if ((albumArtists == null || albumArtists.Count == 0) && artists != null && artists.Count > 0)
            {
                albumArtists = new List<string>(artists);
            }

            return new AudioMetadataInfo
            {
                AudioTagsProbed = source?.AudioTagsProbed,
                Title = title,
                SortName = sortName,
                Album = album,
                Artists = artists,
                AlbumArtists = albumArtists,
                Composers = MetadataNormalizer.NormalizeList(source?.Composers),
                Genres = MetadataNormalizer.NormalizeList(source?.Genres),
                TrackNumber = source?.TrackNumber ?? fallback.TrackNumber,
                DiscNumber = source?.DiscNumber ?? fallback.DiscNumber,
                ProductionYear = source?.ProductionYear,
                PremiereDate = source?.PremiereDate,
                ProviderIds = MetadataNormalizer.NormalizeProviderIds(source?.ProviderIds)
            };
        }

        private static void ApplyEffectiveMetadata(
            Audio audio,
            AudioMetadataInfo effective)
        {
            if (audio == null || effective == null || audio.IsLocked)
            {
                return;
            }

            if (ShouldUpdateAudioTitle(audio, effective.Title))
            {
                audio.Name = effective.Title;
            }

            if (!audio.IsFieldLocked(MetadataFields.SortName) &&
                !string.IsNullOrWhiteSpace(effective.SortName) &&
                string.IsNullOrWhiteSpace(audio.SortName))
            {
                audio.SortName = effective.SortName;
            }

            if (!string.IsNullOrWhiteSpace(effective.Album) &&
                string.IsNullOrWhiteSpace(audio.Album))
            {
                audio.Album = effective.Album;
            }

            if (effective.Artists != null && effective.Artists.Count > 0 &&
                (audio.Artists == null || audio.Artists.Length == 0))
            {
                audio.SetArtists(effective.Artists);
            }

            if (effective.AlbumArtists != null && effective.AlbumArtists.Count > 0 &&
                (audio.AlbumArtists == null || audio.AlbumArtists.Length == 0))
            {
                audio.SetAlbumArtists(effective.AlbumArtists);
            }

            if (effective.Composers != null && effective.Composers.Count > 0 &&
                (audio.Composers == null || audio.Composers.Length == 0))
            {
                audio.SetComposers(effective.Composers);
            }

            if (!audio.IsFieldLocked(MetadataFields.Genres) &&
                effective.Genres != null && effective.Genres.Count > 0 &&
                (audio.Genres == null || audio.Genres.Length == 0) &&
                (audio.GenreItems == null || audio.GenreItems.Length == 0))
            {
                audio.SetGenres(effective.Genres);
            }

            if (effective.TrackNumber.HasValue && !audio.IndexNumber.HasValue)
            {
                audio.IndexNumber = effective.TrackNumber;
            }

            if (effective.DiscNumber.HasValue && !audio.ParentIndexNumber.HasValue)
            {
                audio.ParentIndexNumber = effective.DiscNumber;
            }

            // Emby 的 MetadataFields 未提供 ProductionYear / PremiereDate 常量，这里保持「只补缺失值」的语义
            if (effective.ProductionYear.HasValue && IsValidProductionYear(effective.ProductionYear.Value))
            {
                int year = effective.ProductionYear.Value;
                if (!audio.ProductionYear.HasValue)
                {
                    audio.ProductionYear = year;
                }

                if (!audio.PremiereDate.HasValue)
                {
                    audio.PremiereDate = effective.PremiereDate.HasValue && IsValidProductionYear(effective.PremiereDate.Value.Year)
                        ? effective.PremiereDate
                        : new DateTimeOffset(new DateTime(year, 1, 1, 0, 0, 0, DateTimeKind.Utc));
                }
            }
            else if (effective.PremiereDate.HasValue &&
                     IsValidProductionYear(effective.PremiereDate.Value.Year) &&
                     !audio.PremiereDate.HasValue)
            {
                audio.PremiereDate = effective.PremiereDate;
                if (!audio.ProductionYear.HasValue)
                {
                    audio.ProductionYear = effective.PremiereDate.Value.Year;
                }
            }

            if (effective.ProviderIds != null && effective.ProviderIds.Count > 0)
            {
                foreach (var kvp in effective.ProviderIds)
                {
                    if (!string.IsNullOrWhiteSpace(kvp.Key) &&
                        !string.IsNullOrWhiteSpace(kvp.Value) &&
                        string.IsNullOrWhiteSpace(audio.GetProviderId(kvp.Key)))
                    {
                        audio.SetProviderId(kvp.Key, kvp.Value);
                    }
                }
            }
        }

        private static bool IsValidProductionYear(int year)
        {
            return year >= MinValidProductionYear && year <= MaxValidProductionYear;
        }

        private static bool ShouldUpdateAudioTitle(Audio audio, string? candidateTitle)
        {
            if (audio == null || string.IsNullOrWhiteSpace(candidateTitle) || audio.IsFieldLocked(MetadataFields.Name))
            {
                return false;
            }

            if (string.Equals(audio.Name, candidateTitle, StringComparison.Ordinal))
            {
                return false;
            }

            if (string.IsNullOrWhiteSpace(audio.Name))
            {
                return true;
            }

            var fileNameWithoutExt = !string.IsNullOrWhiteSpace(audio.Path)
                ? Path.GetFileNameWithoutExtension(audio.Path)
                : null;

            return !string.IsNullOrWhiteSpace(fileNameWithoutExt) &&
                   string.Equals(audio.Name, fileNameWithoutExt, StringComparison.Ordinal);
        }
    }
}
