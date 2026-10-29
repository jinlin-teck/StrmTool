using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.MediaInfo;

namespace StrmTool.Common
{
    /// <summary>
    /// 媒体流合并：外挂字幕/歌词路径还原、失效文件过滤与同目录歌词补全。
    /// </summary>
    public static class MediaStreamMerger
    {
        private static readonly string[] LyricFileExtensions = new[] { ".lrc", ".elrc" };

        /// <summary>
        /// 整理要保存的媒体流列表：
        /// 1. 将 JSON 恢复出的相对路径外挂字幕/歌词还原为当前媒体所在目录的完整路径；
        /// 2. 保留已存在的有效外挂字幕流；
        /// 3. 对 Audio 自动扫描并补充同目录同名外挂歌词文件（.lrc / .elrc）。
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
            if (item != null && AudioMetadataHelper.IsMusicLibraryAudio(item) && !string.IsNullOrWhiteSpace(item.Path))
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
    }
}
