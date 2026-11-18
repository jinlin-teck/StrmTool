using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using MediaBrowser.Controller.Entities.Audio;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Configuration;

namespace StrmTool.Common
{
    /// <summary>
    /// 基于文件名与目录结构的音频元数据兜底解析（纯本地规则，不访问媒体源）。
    /// </summary>
    internal static class AudioFallbackMetadataResolver
    {
        private static readonly char[] ArtistSplitDelimiters = new[] { ';', '|', '\\', '、' };
        private static readonly string[] SpacedArtistSeparators = new[] { " / " };

        private static readonly Regex LeadingTrackRegex = new Regex(
            @"^(?:(?<disc>\d{1,2})\s*-\s*)?(?<track>\d{1,3})(?:\s*[-._]\s*|\s+)(?<rest>.+)$",
            RegexOptions.Compiled);

        private static readonly Regex MultiDiscFolderRegex = new Regex(
            @"^(?:cd|disc|disk)\s*(?<disc>\d{1,2})$",
            RegexOptions.IgnoreCase | RegexOptions.Compiled);

        /// <summary>
        /// 解析文件名（音轨号/碟号/歌手/歌名）与目录结构，产出可用的兜底元数据。
        /// </summary>
        internal static AudioMetadataInfo Resolve(Audio audio, ILibraryManager? libraryManager)
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
                            MediaInfoJsonStore.TrimEndingDirectorySeparatorPreservingRoot(p.Path),
                            MediaInfoJsonStore.TrimEndingDirectorySeparatorPreservingRoot(parentDir),
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

        /// <summary>
        /// 仅用于文件名解析中的歌手部分拆分（如 "歌手A / 歌手B - 歌名"）。
        /// 单独出现的 "/" 不拆分，避免把 "AC/DC" 之类的乐队名拆坏。
        /// </summary>
        private static List<string>? SplitArtists(string? raw)
        {
            if (string.IsNullOrWhiteSpace(raw))
            {
                return null;
            }

            var parts = raw.Split(SpacedArtistSeparators, StringSplitOptions.RemoveEmptyEntries)
                .SelectMany(part => part.Split(ArtistSplitDelimiters, StringSplitOptions.RemoveEmptyEntries));
            return MetadataNormalizer.NormalizeList(parts);
        }
    }
}
