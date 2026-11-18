using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Emby.Naming.Common;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.MediaInfo;

namespace StrmTool.Common
{
    /// <summary>
    /// 媒体流合并：外挂字幕/歌词路径还原、目录边界校验、失效文件过滤与同目录歌词补全。
    /// </summary>
    public static class MediaStreamMerger
    {
        private static readonly string[] LyricFileExtensions = new[] { ".lrc", ".elrc" };

        private static readonly HashSet<string> AllowedExternalSubtitleExtensions = BuildAllowedExternalExtensions();

        private static HashSet<string> BuildAllowedExternalExtensions()
        {
            var set = new HashSet<string>(
                new[]
                {
                    ".srt", ".ssa", ".ass", ".sub", ".smi", ".sami", ".vtt", ".mpl", ".sup",
                    ".idx", ".pgs", ".ttml", ".dfxp", ".lrc", ".elrc", ".txt"
                },
                StringComparer.OrdinalIgnoreCase);

            // 动态并入宿主 NamingOptions 支持的全部字幕扩展名，防止遗漏
            try
            {
                var namingExtensions = new NamingOptions().SubtitleFileExtensions;
                if (namingExtensions != null)
                {
                    foreach (var ext in namingExtensions)
                    {
                        if (!string.IsNullOrWhiteSpace(ext))
                        {
                            set.Add(ext.StartsWith(".", StringComparison.Ordinal) ? ext : "." + ext);
                        }
                    }
                }
            }
            catch
            {
                // 保持内置列表兜底
            }

            return set;
        }

        /// <summary>
        /// 整理要保存的媒体流列表：
        /// 1. 将 JSON 恢复出的外挂字幕/歌词严格限制在当前媒体所在目录内并校验合法扩展名；
        /// 2. 保留数据库中已存在的有效外挂字幕流；
        /// 3. 对音乐库 Audio 自动扫描并补充同目录同名外挂歌词文件（.lrc / .elrc）。
        /// </summary>
        public static List<MediaStream> PrepareMediaStreamsForSave(
            BaseItem item,
            IEnumerable<MediaStream>? incomingStreams,
            ILibraryManager? libraryManager = null,
            bool untrustedIncomingPaths = false,
            IEnumerable<MediaStream>? existingStreams = null)
        {
            var result = new List<MediaStream>();
            var mediaDir = !string.IsNullOrWhiteSpace(item?.Path) ? Path.GetDirectoryName(item.Path) : null;

            var incomingList = (incomingStreams ?? Enumerable.Empty<MediaStream>())
                .Where(s => s != null)
                .ToList();

            // 1. 先放入内嵌流（当输入来自外部 JSON 备份时，拒绝任何非本地字幕的外部流并清理路径字段）
            foreach (var stream in incomingList)
            {
                if (IsLocalExternalSubtitle(stream))
                {
                    continue;
                }

                if (untrustedIncomingPaths)
                {
                    if (stream.IsExternal)
                    {
                        continue;
                    }

                    stream.Path = null;
                    stream.DeliveryUrl = null;
                }

                result.Add(stream);
            }

            int nextIndex = result.Select(s => s.Index).DefaultIfEmpty(-1).Max() + 1;
            var seenExternalPaths = new HashSet<string>(MediaInfoJsonStore.PathStringComparer);

            // 2. 合并传入流与数据库已有流中的本地外挂字幕/歌词流，校验目录边界并过滤失效文件
            var dbStreams = existingStreams ??
                            (item != null && BaseItem.MediaSourceManager != null
                                ? item.GetMediaStreams()
                                : null) ??
                            Enumerable.Empty<MediaStream>();

            foreach (var ext in incomingList.Where(IsLocalExternalSubtitle))
            {
                var fullPath = untrustedIncomingPaths
                    ? ResolveUntrustedSubtitleInMediaDir(ext.Path, mediaDir)
                    : ResolveTrustedExternalStreamPath(ext.Path, mediaDir);
                TryAppendExternalSubtitle(result, seenExternalPaths, ref nextIndex, ext, fullPath);
            }

            foreach (var ext in dbStreams.Where(IsLocalExternalSubtitle))
            {
                var fullPath = ResolveTrustedExternalStreamPath(ext.Path, mediaDir);
                TryAppendExternalSubtitle(result, seenExternalPaths, ref nextIndex, ext, fullPath);
            }

            // 3. 对音乐库 Audio 自动扫描同目录外挂歌词文件（.lrc / .elrc）
            if (item != null &&
                AudioMetadataHelper.IsMusicLibraryAudio(item, libraryManager) &&
                !string.IsNullOrWhiteSpace(item.Path))
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

        private static void TryAppendExternalSubtitle(
            List<MediaStream> result,
            HashSet<string> seenExternalPaths,
            ref int nextIndex,
            MediaStream source,
            string? fullPath)
        {
            if (string.IsNullOrWhiteSpace(fullPath) || !File.Exists(fullPath))
            {
                return;
            }

            if (!seenExternalPaths.Add(fullPath))
            {
                return;
            }

            var codec = !string.IsNullOrWhiteSpace(source.Codec)
                ? source.Codec
                : (Path.GetExtension(fullPath) ?? string.Empty).TrimStart('.').ToLowerInvariant();

            result.Add(new MediaStream
            {
                Index = nextIndex++,
                Type = MediaStreamType.Subtitle,
                IsExternal = true,
                Path = fullPath,
                Protocol = MediaProtocol.File,
                Codec = codec,
                Language = source.Language,
                Title = source.Title,
                DisplayTitle = source.DisplayTitle,
                IsDefault = source.IsDefault,
                IsForced = source.IsForced,
                IsHearingImpaired = source.IsHearingImpaired,
                SupportsExternalStream = true
            });
        }

        private static bool IsLocalExternalSubtitle(MediaStream? stream)
        {
            return stream != null &&
                   stream.IsExternal &&
                   stream.Type == MediaStreamType.Subtitle &&
                   stream.Protocol == MediaProtocol.File &&
                   !string.IsNullOrWhiteSpace(stream.Path);
        }

        /// <summary>
        /// 解析来自外部 JSON 备份的外挂字幕路径：
        /// 强制剥离目录仅取文件名，限制合法字幕/歌词扩展名，并验证最终路径严格位于当前媒体目录内。
        /// </summary>
        private static string? ResolveUntrustedSubtitleInMediaDir(string? rawStreamPath, string? mediaDir)
        {
            if (string.IsNullOrWhiteSpace(rawStreamPath) || string.IsNullOrWhiteSpace(mediaDir))
            {
                return null;
            }

            try
            {
                // 兼容跨平台路径分隔符（例如在 Linux 上收到含反斜杠的旧备份路径）
                var normalizedRaw = rawStreamPath.Replace('\\', Path.DirectorySeparatorChar);
                var fileName = Path.GetFileName(normalizedRaw);
                if (string.IsNullOrWhiteSpace(fileName) ||
                    fileName == "." ||
                    fileName == "..")
                {
                    return null;
                }

                var ext = Path.GetExtension(fileName);
                if (string.IsNullOrWhiteSpace(ext) || !AllowedExternalSubtitleExtensions.Contains(ext))
                {
                    return null;
                }

                var fullMediaDir = MediaInfoJsonStore.TrimEndingDirectorySeparatorPreservingRoot(
                    Path.GetFullPath(mediaDir));
                var candidateFullPath = Path.GetFullPath(Path.Combine(fullMediaDir, fileName));
                var candidateParentRaw = Path.GetDirectoryName(candidateFullPath);
                var candidateParent = !string.IsNullOrEmpty(candidateParentRaw)
                    ? MediaInfoJsonStore.TrimEndingDirectorySeparatorPreservingRoot(candidateParentRaw)
                    : null;

                if (string.IsNullOrEmpty(candidateParent) ||
                    !MediaInfoJsonStore.PathStringComparer.Equals(candidateParent, fullMediaDir))
                {
                    return null;
                }

                return candidateFullPath;
            }
            catch
            {
                return null;
            }
        }

        /// <summary>
        /// 解析受信任来源（如数据库已有流）的外挂字幕路径，并在迁移目录失效时回退同目录匹配
        /// </summary>
        private static string? ResolveTrustedExternalStreamPath(string? streamPath, string? mediaDir)
        {
            if (string.IsNullOrWhiteSpace(streamPath))
            {
                return null;
            }

            if (Path.IsPathRooted(streamPath) && File.Exists(streamPath))
            {
                return streamPath;
            }

            return ResolveUntrustedSubtitleInMediaDir(streamPath, mediaDir);
        }
    }
}
