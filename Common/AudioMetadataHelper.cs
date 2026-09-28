using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Audio;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Configuration;
using MediaBrowser.Model.Dto;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.MediaInfo;

namespace StrmTool.Common
{
    /// <summary>
    /// 随 -mediainfo.json 持久化导出的音频元数据模型
    /// </summary>
    public class AudioMetadataInfo
    {
        public bool? AudioTagsProbed { get; set; }
        public string? Title { get; set; }
        public string? SortName { get; set; }
        public string? Album { get; set; }
        public List<string>? Artists { get; set; }
        public List<string>? AlbumArtists { get; set; }
        public List<string>? Composers { get; set; }
        public List<string>? Genres { get; set; }
        public int? TrackNumber { get; set; }
        public int? DiscNumber { get; set; }
        public int? ProductionYear { get; set; }
        public DateTimeOffset? PremiereDate { get; set; }
        public Dictionary<string, string>? ProviderIds { get; set; }
    }

    /// <summary>
    /// Emby 音频 STRM 元数据提取、标准目录/文件名回退、外挂歌词合并与 JSON 备份恢复辅助类。
    /// </summary>
    public static class AudioMetadataHelper
    {
        private static readonly char[] ArtistSplitDelimiters = new[] { '/', ';', '|', '\\', '、' };
        private static readonly string[] LyricFileExtensions = new[] { ".lrc", ".elrc" };

        private static readonly Regex LeadingTrackRegex = new Regex(
            @"^(?:(?<disc>\d{1,2})\s*-\s*)?(?<track>\d{1,3})(?:\s*[-._]\s*|\s+)(?<rest>.+)$",
            RegexOptions.Compiled);

        private static readonly Regex MultiDiscFolderRegex = new Regex(
            @"^(?:cd|disc|disk)\s*(?<disc>\d{1,2})$",
            RegexOptions.IgnoreCase | RegexOptions.Compiled);

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
        /// 检查音乐库音频项是否缺少核心音乐元数据（专辑、艺术家或专辑艺术家）
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

            return string.IsNullOrWhiteSpace(audio.Album) ||
                   audio.Artists == null || audio.Artists.Length == 0 ||
                   audio.AlbumArtists == null || audio.AlbumArtists.Length == 0;
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

            var localLyrics = FindLocalLyricFiles(audio.Path);
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
                var composers = NormalizeList(mediaInfo.Composers);
                if (mediaInfo.People != null && mediaInfo.People.Length > 0)
                {
                    var writers = mediaInfo.People
                        .Where(p => p != null && p.Type == PersonType.Writer && !string.IsNullOrWhiteSpace(p.Name))
                        .Select(p => p.Name);
                    composers = NormalizeList((composers ?? Enumerable.Empty<string>()).Concat(writers));
                }

                int? year = mediaInfo.ProductionYear ?? mediaInfo.PremiereDate?.Year;
                var providerIds = NormalizeProviderIds(mediaInfo.ProviderIds);

                probed = new AudioMetadataInfo
                {
                    AudioTagsProbed = true,
                    Title = NormalizeString(mediaInfo.Name),
                    SortName = NormalizeString(mediaInfo.SortName),
                    Album = NormalizeString(mediaInfo.Album),
                    Artists = NormalizeList(mediaInfo.Artists),
                    AlbumArtists = NormalizeList(mediaInfo.AlbumArtists),
                    Composers = composers,
                    Genres = NormalizeList(mediaInfo.Genres),
                    TrackNumber = mediaInfo.IndexNumber > 0 ? mediaInfo.IndexNumber : null,
                    DiscNumber = mediaInfo.ParentIndexNumber > 0 ? mediaInfo.ParentIndexNumber : null,
                    ProductionYear = (year.HasValue && year.Value > 0 && year.Value <= 9999) ? year : null,
                    PremiereDate = mediaInfo.PremiereDate,
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

            var fallback = ResolveFallbackMetadata(audio, libraryManager);
            bool markProbed = audioTagsProbed || !HasMissingAudioMetadata(audio);

            var fileNameWithoutExt = !string.IsNullOrWhiteSpace(audio.Path)
                ? Path.GetFileNameWithoutExtension(audio.Path)
                : null;
            var title = !string.IsNullOrWhiteSpace(audio.Name) &&
                        !string.Equals(audio.Name, fileNameWithoutExt, StringComparison.Ordinal)
                ? audio.Name
                : fallback.Title;

            var album = NormalizeString(audio.Album) ?? fallback.Album;
            var artists = NormalizeList(audio.Artists) ?? fallback.Artists;
            var albumArtists = NormalizeList(audio.AlbumArtists) ?? fallback.AlbumArtists ?? artists;
            if ((artists == null || artists.Count == 0) && albumArtists != null && albumArtists.Count > 0)
            {
                artists = new List<string>(albumArtists);
            }

            return new AudioMetadataInfo
            {
                AudioTagsProbed = markProbed ? (bool?)true : null,
                Title = title,
                SortName = NormalizeString(audio.SortName),
                Album = album,
                Artists = artists,
                AlbumArtists = albumArtists,
                Composers = NormalizeList(audio.Composers?.Select(c => c.Name)),
                Genres = NormalizeList(audio.Genres),
                TrackNumber = audio.IndexNumber ?? fallback.TrackNumber,
                DiscNumber = audio.ParentIndexNumber ?? fallback.DiscNumber,
                ProductionYear = audio.ProductionYear,
                PremiereDate = audio.PremiereDate,
                ProviderIds = NormalizeProviderIds(audio.ProviderIds)
            };
        }

        /// <summary>
        /// 整理要保存的媒体流列表：
        /// 1. 将 JSON 恢复出的相对路径外挂字幕/歌词还原为当前媒体所在目录的完整路径；
        /// 2. 保留已存在的有效外挂字幕流；
        /// 3. 对 Audio 自动扫描并补充同目录同名外挂歌词文件（.lrc / .elrc / .txt）。
        /// </summary>
        public static List<MediaStream> PrepareMediaStreamsForSave(
            BaseItem item,
            IEnumerable<MediaStream>? incomingStreams)
        {
            var result = new List<MediaStream>();
            var mediaDir = !string.IsNullOrWhiteSpace(item?.Path) ? Path.GetDirectoryName(item.Path) : null;

            var incomingList = (incomingStreams ?? Enumerable.Empty<MediaStream>())
                .Where(s => s != null)
                .ToList();

            // 1. 先放入所有非本地外挂流
            foreach (var stream in incomingList)
            {
                if (!IsLocalExternalSubtitle(stream))
                {
                    result.Add(stream);
                }
            }

            int nextIndex = result.Select(s => s.Index).DefaultIfEmpty(-1).Max() + 1;
            var seenExternalPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            // 2. 合并已有流与传入流中的本地外挂字幕/歌词流，修正相对路径并过滤已不存在的文件
            var existingStreams = item?.GetMediaStreams() ?? new List<MediaStream>();
            var candidateExternals = incomingList.Where(IsLocalExternalSubtitle)
                .Concat(existingStreams.Where(IsLocalExternalSubtitle));

            foreach (var ext in candidateExternals)
            {
                var fullPath = ResolveExternalStreamPath(ext.Path, mediaDir);
                if (string.IsNullOrWhiteSpace(fullPath) || !File.Exists(fullPath))
                {
                    continue;
                }

                if (seenExternalPaths.Add(fullPath))
                {
                    var codec = !string.IsNullOrWhiteSpace(ext.Codec)
                        ? ext.Codec
                        : (Path.GetExtension(fullPath) ?? string.Empty).TrimStart('.').ToLowerInvariant();

                    result.Add(new MediaStream
                    {
                        Index = nextIndex++,
                        Type = MediaStreamType.Subtitle,
                        IsExternal = true,
                        Path = fullPath,
                        Protocol = MediaProtocol.File,
                        Codec = codec,
                        Language = ext.Language,
                        Title = ext.Title,
                        DisplayTitle = ext.DisplayTitle,
                        IsDefault = ext.IsDefault,
                        IsForced = ext.IsForced,
                        IsHearingImpaired = ext.IsHearingImpaired,
                        SupportsExternalStream = true
                    });
                }
            }

            // 3. 对音乐库 Audio 自动扫描同目录外挂歌词文件（.lrc / .elrc）
            if (item != null && IsMusicLibraryAudio(item) && !string.IsNullOrWhiteSpace(item.Path))
            {
                foreach (var lyricPath in FindLocalLyricFiles(item.Path))
                {
                    if (seenExternalPaths.Add(lyricPath))
                    {
                        var codec = (Path.GetExtension(lyricPath) ?? string.Empty).TrimStart('.').ToLowerInvariant();
                        result.Add(new MediaStream
                        {
                            Index = nextIndex++,
                            Type = MediaStreamType.Subtitle,
                            IsExternal = true,
                            Path = lyricPath,
                            Protocol = MediaProtocol.File,
                            Codec = codec,
                            SupportsExternalStream = true
                        });
                    }
                }
            }

            return result;
        }

        private static bool IsLocalExternalSubtitle(MediaStream? stream)
        {
            return stream != null &&
                   stream.IsExternal &&
                   stream.Type == MediaStreamType.Subtitle &&
                   stream.Protocol == MediaProtocol.File &&
                   !string.IsNullOrWhiteSpace(stream.Path);
        }

        private static string? ResolveExternalStreamPath(string? streamPath, string? mediaDir)
        {
            if (string.IsNullOrWhiteSpace(streamPath))
            {
                return null;
            }

            if (Path.IsPathRooted(streamPath))
            {
                if (File.Exists(streamPath))
                {
                    return streamPath;
                }

                // 若原绝对路径因迁移目录失效，尝试在当前媒体同目录下按文件名定位
                if (!string.IsNullOrWhiteSpace(mediaDir))
                {
                    var fallbackCandidate = Path.Combine(mediaDir, Path.GetFileName(streamPath));
                    if (File.Exists(fallbackCandidate))
                    {
                        return fallbackCandidate;
                    }
                }

                return streamPath;
            }

            if (!string.IsNullOrWhiteSpace(mediaDir))
            {
                return Path.Combine(mediaDir, Path.GetFileName(streamPath));
            }

            return null;
        }

        private static AudioMetadataInfo MergeWithFallback(
            Audio audio,
            AudioMetadataInfo? source,
            ILibraryManager? libraryManager)
        {
            var fallback = ResolveFallbackMetadata(audio, libraryManager);

            var title = NormalizeString(source?.Title) ?? fallback.Title;
            var sortName = NormalizeString(source?.SortName);
            var album = NormalizeString(source?.Album) ?? fallback.Album;
            var sourceArtists = NormalizeList(source?.Artists);
            var sourceAlbumArtists = NormalizeList(source?.AlbumArtists);

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
                Composers = NormalizeList(source?.Composers),
                Genres = NormalizeList(source?.Genres),
                TrackNumber = source?.TrackNumber ?? fallback.TrackNumber,
                DiscNumber = source?.DiscNumber ?? fallback.DiscNumber,
                ProductionYear = source?.ProductionYear,
                PremiereDate = source?.PremiereDate,
                ProviderIds = NormalizeProviderIds(source?.ProviderIds)
            };
        }

        private static bool ApplyEffectiveMetadata(
            Audio audio,
            AudioMetadataInfo effective)
        {
            if (audio == null || effective == null || audio.IsLocked)
            {
                return false;
            }

            bool changed = false;

            if (ShouldUpdateAudioTitle(audio, effective.Title))
            {
                audio.Name = effective.Title;
                changed = true;
            }

            if (!audio.IsFieldLocked(MetadataFields.SortName) &&
                !string.IsNullOrWhiteSpace(effective.SortName) &&
                string.IsNullOrWhiteSpace(audio.SortName))
            {
                audio.SortName = effective.SortName;
                changed = true;
            }

            if (!string.IsNullOrWhiteSpace(effective.Album) &&
                string.IsNullOrWhiteSpace(audio.Album))
            {
                audio.Album = effective.Album;
                changed = true;
            }

            if (effective.Artists != null && effective.Artists.Count > 0 &&
                (audio.Artists == null || audio.Artists.Length == 0))
            {
                audio.SetArtists(effective.Artists);
                changed = true;
            }

            if (effective.AlbumArtists != null && effective.AlbumArtists.Count > 0 &&
                (audio.AlbumArtists == null || audio.AlbumArtists.Length == 0))
            {
                audio.SetAlbumArtists(effective.AlbumArtists);
                changed = true;
            }

            if (effective.Composers != null && effective.Composers.Count > 0 &&
                (audio.Composers == null || audio.Composers.Length == 0))
            {
                audio.SetComposers(effective.Composers);
                changed = true;
            }

            if (!audio.IsFieldLocked(MetadataFields.Genres) &&
                effective.Genres != null && effective.Genres.Count > 0 &&
                (audio.GenreItems == null || audio.GenreItems.Length == 0))
            {
                audio.SetGenres(effective.Genres);
                changed = true;
            }

            if (effective.TrackNumber.HasValue && !audio.IndexNumber.HasValue)
            {
                audio.IndexNumber = effective.TrackNumber;
                changed = true;
            }

            if (effective.DiscNumber.HasValue && !audio.ParentIndexNumber.HasValue)
            {
                audio.ParentIndexNumber = effective.DiscNumber;
                changed = true;
            }

            if (effective.ProductionYear.HasValue && effective.ProductionYear.Value > 0 && effective.ProductionYear.Value <= 9999)
            {
                int year = effective.ProductionYear.Value;
                if (!audio.ProductionYear.HasValue)
                {
                    audio.ProductionYear = year;
                    changed = true;
                }

                if (!audio.PremiereDate.HasValue)
                {
                    audio.PremiereDate = effective.PremiereDate ??
                                         new DateTimeOffset(new DateTime(year, 1, 1, 0, 0, 0, DateTimeKind.Utc));
                    changed = true;
                }
            }
            else if (effective.PremiereDate.HasValue && !audio.PremiereDate.HasValue)
            {
                audio.PremiereDate = effective.PremiereDate;
                if (!audio.ProductionYear.HasValue)
                {
                    audio.ProductionYear = effective.PremiereDate.Value.Year;
                }

                changed = true;
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
                        changed = true;
                    }
                }
            }

            return changed;
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

        private static AudioMetadataInfo ResolveFallbackMetadata(Audio audio, ILibraryManager? libraryManager)
        {
            var result = new AudioMetadataInfo();
            if (audio == null)
            {
                return result;
            }

            // 1. 从文件名解析音轨号、碟片号、歌手、歌名（如 "03 - 陈佳 - 爱的箴言.strm" 或 "陈佳 - 爱的箴言.strm"）
            var fileNameWithoutExt = !string.IsNullOrWhiteSpace(audio.Path)
                ? Path.GetFileNameWithoutExtension(audio.Path)
                : null;
            ParseTrackFileName(fileNameWithoutExt, out var parsedDisc, out var parsedTrack, out var parsedArtists, out var parsedTitle);

            result.DiscNumber = parsedDisc;
            result.TrackNumber = parsedTrack;
            result.Title = parsedTitle;
            result.Artists = parsedArtists;

            // 2. 优先使用 Emby 原生根据 MusicFolderStructure (artist_album_track / album_track) 解析的目录结构
            var libraryOptions = libraryManager != null ? libraryManager.GetLibraryOptions(audio) : null;
            if (libraryOptions != null)
            {
                var albumFolder = audio.GetAlbumFolder(libraryOptions);
                if (!string.IsNullOrWhiteSpace(albumFolder))
                {
                    var folderName = Path.GetFileName(albumFolder)?.Trim();
                    if (!string.IsNullOrWhiteSpace(folderName))
                    {
                        result.Album = folderName;
                    }
                }

                var albumArtistFolder = audio.GetAlbumArtistFolder(libraryOptions);
                if (!string.IsNullOrWhiteSpace(albumArtistFolder))
                {
                    var artistFolderName = Path.GetFileName(albumArtistFolder)?.Trim();
                    if (!string.IsNullOrWhiteSpace(artistFolderName))
                    {
                        result.AlbumArtists = new List<string> { artistFolderName };
                    }
                }
            }

            // 3. 若媒体库未配置目录结构或仍缺失，回退按物理目录层级 (专辑艺术家/专辑/[CDx/]歌曲.strm) 解析
            if ((string.IsNullOrWhiteSpace(result.Album) || result.AlbumArtists == null || result.AlbumArtists.Count == 0 || !result.DiscNumber.HasValue) &&
                !string.IsNullOrWhiteSpace(audio.Path))
            {
                ExtractFolderHierarchyFallback(
                    audio.Path,
                    parsedArtists,
                    libraryOptions,
                    out var pathAlbumArtist,
                    out var pathAlbum,
                    out var pathDiscNumber);

                if (string.IsNullOrWhiteSpace(result.Album) && !string.IsNullOrWhiteSpace(pathAlbum))
                {
                    result.Album = pathAlbum;
                }

                if ((result.AlbumArtists == null || result.AlbumArtists.Count == 0) && !string.IsNullOrWhiteSpace(pathAlbumArtist))
                {
                    result.AlbumArtists = new List<string> { pathAlbumArtist };
                }

                if (!result.DiscNumber.HasValue && pathDiscNumber.HasValue)
                {
                    result.DiscNumber = pathDiscNumber;
                }
            }

            // 4. Artists 与 AlbumArtists 互为兜底
            if ((result.Artists == null || result.Artists.Count == 0) && result.AlbumArtists != null && result.AlbumArtists.Count > 0)
            {
                result.Artists = new List<string>(result.AlbumArtists);
            }
            else if ((result.AlbumArtists == null || result.AlbumArtists.Count == 0) && result.Artists != null && result.Artists.Count > 0)
            {
                result.AlbumArtists = new List<string>(result.Artists);
            }

            return result;
        }

        private static void ParseTrackFileName(
            string? fileNameWithoutExt,
            out int? discNumber,
            out int? trackNumber,
            out List<string>? artists,
            out string? title)
        {
            discNumber = null;
            trackNumber = null;
            artists = null;
            title = null;

            if (string.IsNullOrWhiteSpace(fileNameWithoutExt))
            {
                return;
            }

            string working = fileNameWithoutExt.Trim();
            var match = LeadingTrackRegex.Match(working);
            if (match.Success)
            {
                if (match.Groups["disc"].Success &&
                    int.TryParse(match.Groups["disc"].Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out int disc) &&
                    disc > 0)
                {
                    discNumber = disc;
                }

                if (int.TryParse(match.Groups["track"].Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out int track) &&
                    track > 0)
                {
                    trackNumber = track;
                    working = match.Groups["rest"].Value.Trim();
                }
            }

            int separatorIndex = working.IndexOf(" - ", StringComparison.Ordinal);
            if (separatorIndex > 0)
            {
                var artistPart = working.Substring(0, separatorIndex).Trim();
                var titlePart = working.Substring(separatorIndex + 3).Trim();
                if (!string.IsNullOrWhiteSpace(artistPart) && !string.IsNullOrWhiteSpace(titlePart))
                {
                    artists = SplitArtists(artistPart);
                    title = titlePart;
                    return;
                }
            }

            if (trackNumber.HasValue && !string.IsNullOrWhiteSpace(working))
            {
                title = working;
            }
        }

        private static void ExtractFolderHierarchyFallback(
            string strmPath,
            List<string>? parsedFileArtists,
            LibraryOptions? libraryOptions,
            out string? albumArtist,
            out string? album,
            out int? discNumber)
        {
            albumArtist = null;
            album = null;
            discNumber = null;

            try
            {
                var dir = Path.GetDirectoryName(strmPath);
                if (string.IsNullOrWhiteSpace(dir))
                {
                    return;
                }

                var currentDirName = Path.GetFileName(dir);
                var parentDir = Path.GetDirectoryName(dir);

                var discMatch = !string.IsNullOrWhiteSpace(currentDirName)
                    ? MultiDiscFolderRegex.Match(currentDirName.Trim())
                    : Match.Empty;
                if (discMatch.Success && !string.IsNullOrWhiteSpace(parentDir))
                {
                    if (int.TryParse(discMatch.Groups["disc"].Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out int disc) &&
                        disc > 0)
                    {
                        discNumber = disc;
                    }

                    dir = parentDir;
                    currentDirName = Path.GetFileName(dir);
                    parentDir = Path.GetDirectoryName(dir);
                }

                var grandParentDirName = !string.IsNullOrWhiteSpace(parentDir)
                    ? Path.GetFileName(parentDir)
                    : null;

                if (string.IsNullOrWhiteSpace(currentDirName) || string.IsNullOrWhiteSpace(grandParentDirName))
                {
                    return;
                }

                // 检查祖父目录是否已经是媒体库根路径（若是根路径，则当前目录只是单层目录，不能把根路径当成艺术家）
                bool isParentLibraryRoot = libraryOptions?.PathInfos != null &&
                    libraryOptions.PathInfos.Any(p =>
                        !string.IsNullOrWhiteSpace(p?.Path) &&
                        string.Equals(
                            p.Path.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar),
                            parentDir.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar),
                            StringComparison.OrdinalIgnoreCase));

                if (isParentLibraryRoot)
                {
                    return;
                }

                bool matchesFileArtist = parsedFileArtists != null &&
                    parsedFileArtists.Any(a => string.Equals(a, grandParentDirName.Trim(), StringComparison.OrdinalIgnoreCase));

                if (matchesFileArtist || libraryOptions == null)
                {
                    albumArtist = grandParentDirName.Trim();
                    album = currentDirName.Trim();
                }
            }
            catch
            {
                // 忽略路径解析异常
            }
        }

        internal static List<string> FindLocalLyricFiles(string? strmPath)
        {
            var result = new List<string>();
            if (string.IsNullOrWhiteSpace(strmPath))
            {
                return result;
            }

            try
            {
                var directory = Path.GetDirectoryName(strmPath);
                var baseName = Path.GetFileNameWithoutExtension(strmPath);
                if (string.IsNullOrWhiteSpace(directory) || string.IsNullOrWhiteSpace(baseName) || !Directory.Exists(directory))
                {
                    return result;
                }

                foreach (var ext in LyricFileExtensions)
                {
                    var candidate = Path.Combine(directory, baseName + ext);
                    if (File.Exists(candidate))
                    {
                        result.Add(candidate);
                    }
                }
            }
            catch
            {
                // 忽略磁盘访问异常
            }

            return result;
        }

        private static string? NormalizeString(string? value)
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                return null;
            }

            var trimmed = value.Trim();
            return trimmed.Length > 0 ? trimmed : null;
        }

        /// <summary>
        /// 规范化已拆分的字符串列表（仅做 Trim 与去重，不按分隔符二次切分，避免破坏 "AC/DC" 等合法名称）
        /// </summary>
        private static List<string>? NormalizeList(IEnumerable<string>? values)
        {
            if (values == null)
            {
                return null;
            }

            var result = new List<string>();
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            foreach (var raw in values)
            {
                if (string.IsNullOrWhiteSpace(raw))
                {
                    continue;
                }

                var trimmed = raw.Trim();
                if (trimmed.Length > 0 && seen.Add(trimmed))
                {
                    result.Add(trimmed);
                }
            }

            return result.Count > 0 ? result : null;
        }

        /// <summary>
        /// 仅用于文件名解析中的歌手部分拆分（如 "歌手A / 歌手B - 歌名"）
        /// </summary>
        private static List<string>? SplitArtists(string? raw)
        {
            if (string.IsNullOrWhiteSpace(raw))
            {
                return null;
            }

            var parts = raw.Split(ArtistSplitDelimiters, StringSplitOptions.RemoveEmptyEntries);
            return NormalizeList(parts);
        }

        private static Dictionary<string, string>? NormalizeProviderIds(IDictionary<string, string>? providerIds)
        {
            if (providerIds == null || providerIds.Count == 0)
            {
                return null;
            }

            var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var kvp in providerIds)
            {
                var key = NormalizeString(kvp.Key);
                var val = NormalizeString(kvp.Value);
                if (key != null && val != null)
                {
                    result[key] = val;
                }
            }

            return result.Count > 0 ? result : null;
        }
    }
}
