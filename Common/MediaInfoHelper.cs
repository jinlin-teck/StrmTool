using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Audio;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Dto;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.MediaInfo;

namespace StrmTool.Common
{
    /// <summary>
    /// 媒体信息检查辅助类，提供统一的媒体信息检查逻辑
    /// </summary>
    public static class MediaInfoHelper
    {
        /// <summary>
        /// 检查文件是否为STRM文件
        /// </summary>
        /// <param name="path">文件路径</param>
        /// <returns>是否为STRM文件</returns>
        public static bool IsStrmFile(string? path)
        {
            return !string.IsNullOrEmpty(path) &&
                   path.EndsWith(".strm", System.StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>
        /// 检查媒体项是否包含完整媒体流信息（对音频同时要求具备基础音乐元数据与同目录外挂歌词流，或已远程探测收敛）
        /// </summary>
        public static bool HasCompleteMediaInfo(
            BaseItem? item,
            IMediaInfoBackupInspector? backupInspector = null,
            ILibraryManager? libraryManager = null)
        {
            if (item == null) return false;
            return HasCompleteMediaInfo(item, item.GetMediaStreams(), backupInspector, libraryManager);
        }

        /// <summary>
        /// 基于已获取的媒体流列表判断媒体项信息是否完整，避免重复查询仓储
        /// </summary>
        public static bool HasCompleteMediaInfo(
            BaseItem? item,
            IEnumerable<MediaStream>? streams,
            IMediaInfoBackupInspector? backupInspector = null,
            ILibraryManager? libraryManager = null)
        {
            if (item == null || !HasCompleteMediaInfo(streams))
            {
                return false;
            }

            if (item is Audio audio)
            {
                if (AudioMetadataHelper.HasMissingLocalLyrics(audio, streams, libraryManager))
                {
                    return false;
                }

                if (AudioMetadataHelper.HasMissingAudioMetadata(audio, libraryManager))
                {
                    // 若已执行过远程探测 (AudioTagsProbed == true) 且 JSON/目录兜底均无专辑与歌手可恢复，
                    // 说明源文件本身无标签且目录无法推断，视为已收敛终态，避免每日重复远程 ffprobe。
                    if (backupInspector != null &&
                        backupInspector.HasProbedAudioTagsInJson(audio) &&
                        !backupInspector.CanRestoreAudioMetadataFromJson(audio))
                    {
                        return true;
                    }

                    return false;
                }
            }

            return true;
        }

        /// <summary>
        /// 基于已获取的媒体流列表判断媒体流是否存在
        /// </summary>
        public static bool HasCompleteMediaInfo(IEnumerable<MediaStream>? streams)
        {
            if (streams == null) return false;
            return streams.Any(s => s != null && (s.Type == MediaStreamType.Video || s.Type == MediaStreamType.Audio));
        }

        /// <summary>
        /// 获取媒体流列表中视频流/音频流的存在情况（单次遍历）
        /// </summary>
        public static (bool HasVideo, bool HasAudio) GetStreamSummary(IEnumerable<MediaStream>? streams)
        {
            bool hasVideo = false;
            bool hasAudio = false;

            if (streams != null)
            {
                foreach (var stream in streams)
                {
                    if (stream == null)
                    {
                        continue;
                    }

                    if (stream.Type == MediaStreamType.Video)
                    {
                        hasVideo = true;
                    }
                    else if (stream.Type == MediaStreamType.Audio)
                    {
                        hasAudio = true;
                    }

                    if (hasVideo && hasAudio)
                    {
                        break;
                    }
                }
            }

            return (hasVideo, hasAudio);
        }

        /// <summary>
        /// 将媒体源中的媒体属性（及音频标签元数据）写回项目并持久化。
        /// </summary>
        public static void ApplyMediaSourceInfo(
            BaseItem item,
            MediaSourceInfo mediaSourceInfo,
            ILibraryManager libraryManager,
            CancellationToken cancellationToken,
            AudioMetadataInfo? cachedAudioMetadata = null)
        {
            // 只写入 JSON 中确实存在的值，避免旧版备份缺字段时把数据库里的有效值清零
            if (mediaSourceInfo.Size.HasValue && mediaSourceInfo.Size.Value > 0)
            {
                item.Size = mediaSourceInfo.Size.Value;
            }

            if (mediaSourceInfo.RunTimeTicks.HasValue)
            {
                item.RunTimeTicks = mediaSourceInfo.RunTimeTicks;
            }

            if (!string.IsNullOrWhiteSpace(mediaSourceInfo.Container))
            {
                item.Container = mediaSourceInfo.Container;
            }

            if (mediaSourceInfo.Bitrate.HasValue && mediaSourceInfo.Bitrate.Value > 0)
            {
                item.TotalBitrate = mediaSourceInfo.Bitrate.Value;
            }

            var videoStream = GetHighestResolutionVideoStream(mediaSourceInfo.MediaStreams);
            if (videoStream != null)
            {
                if (videoStream.Width.HasValue)
                {
                    item.Width = videoStream.Width.Value;
                }

                if (videoStream.Height.HasValue)
                {
                    item.Height = videoStream.Height.Value;
                }
            }

            if (item is Audio audio && Plugin.GetSafeConfiguration().EnableMusicMetadata)
            {
                if (mediaSourceInfo is MediaInfo probedMediaInfo)
                {
                    AudioMetadataHelper.ApplyAudioMetadataFromProbe(audio, probedMediaInfo, libraryManager);
                }
                else
                {
                    AudioMetadataHelper.ApplyAudioMetadataFromCache(audio, cachedAudioMetadata, libraryManager);
                }
            }

            libraryManager.UpdateItems(new List<BaseItem> { item }, item.GetParent(),
                ItemUpdateType.MetadataImport, false, false, null, cancellationToken);
        }

        /// <summary>
        /// 检查媒体项是否存在JSON文件
        /// </summary>
        public static bool HasJsonFile(BaseItem item, IMediaInfoBackupInspector backupInspector)
        {
            var jsonFilePath = backupInspector.GetMediaInfoJsonPath(item);
            return System.IO.File.Exists(jsonFilePath);
        }

        /// <summary>
        /// 检查媒体项是否需要从JSON文件恢复
        /// </summary>
        public static bool ShouldRestoreFromJson(
            BaseItem item,
            IMediaInfoBackupInspector backupInspector,
            ILibraryManager? libraryManager = null)
        {
            return !HasCompleteMediaInfo(item, backupInspector, libraryManager) &&
                   HasJsonFile(item, backupInspector);
        }

        /// <summary>
        /// 判断条目是否为附加视频（Trailer / MusicVideo / 带 ExtraType 的视频）
        /// </summary>
        public static bool IsExtraVideoItem(BaseItem? item)
        {
            return LibraryScopeFilter.IsExtraVideoItem(item);
        }

        /// <summary>
        /// 判断媒体项是否位于当前插件配置允许处理的媒体库范围内
        /// </summary>
        public static bool IsItemInConfiguredLibraries(
            BaseItem? item,
            ILibraryManager? libraryManager,
            PluginConfiguration? config = null)
        {
            return LibraryScopeFilter.IsItemInConfiguredLibraries(item, libraryManager, config);
        }

        /// <summary>
        /// 获取当前配置范围下的 STRM 数量（带 15 秒短 TTL 缓存，避免前端轮询反复全表物化）。
        /// </summary>
        public static int GetCachedScopedStrmCount(ILibraryManager libraryManager, PluginConfiguration? config = null)
        {
            if (libraryManager == null)
            {
                return 0;
            }

            var cfg = config ?? Plugin.GetSafeConfiguration();
            return LibraryScopeFilter.GetOrAddScopedCount(cfg, () => GetAllStrmFiles(libraryManager, cfg).Count);
        }

        /// <summary>
        /// 获取库中当前配置允许处理的所有 STRM 文件（按媒体库范围与附加视频开关过滤）。
        /// </summary>
        public static List<BaseItem> GetAllStrmFiles(ILibraryManager libraryManager)
        {
            return GetAllStrmFiles(libraryManager, null);
        }

        /// <summary>
        /// 获取库中指定配置允许处理的所有 STRM 文件（按媒体库范围与附加视频开关过滤）。
        /// </summary>
        public static List<BaseItem> GetAllStrmFiles(ILibraryManager libraryManager, PluginConfiguration? config)
        {
            if (libraryManager == null)
            {
                return new List<BaseItem>();
            }

            var cfg = config ?? Plugin.GetSafeConfiguration();
            if (!cfg.EnableAllLibraries &&
                (cfg.EnabledLibraryIds == null || cfg.EnabledLibraryIds.Length == 0))
            {
                return new List<BaseItem>();
            }

            var includeItemTypes = cfg.IncludeExtraVideos
                ? new[] { "Movie", "Episode", "Video", "Audio", "Trailer", "MusicVideo" }
                : new[] { "Movie", "Episode", "Video", "Audio" };

            var query = new InternalItemsQuery
            {
                HasPath = true,
                Recursive = true,
                IncludeItemTypes = includeItemTypes
            };

            bool filterInMemoryByLibrary = false;
            if (!cfg.EnableAllLibraries)
            {
                LibraryScopeFilter.TryResolveEnabledLibraryAncestorIds(
                    libraryManager,
                    cfg,
                    out var ancestorIds,
                    out var allResolvedToAncestors);

                if (allResolvedToAncestors && ancestorIds.Length == 0)
                {
                    return new List<BaseItem>();
                }

                if (allResolvedToAncestors && ancestorIds.Length > 0)
                {
                    query.AncestorIds = ancestorIds;
                }

                filterInMemoryByLibrary = !allResolvedToAncestors;
            }

            var items = libraryManager.GetItemList(query);
            if (!filterInMemoryByLibrary)
            {
                return items
                    .Where(i => IsStrmFile(i.Path) &&
                                (cfg.IncludeExtraVideos || !LibraryScopeFilter.IsExtraVideoItem(i)))
                    .ToList();
            }

            var matcher = LibraryScopeFilter.CreateMatcher(libraryManager, cfg);
            return items
                .Where(i => IsStrmFile(i.Path) && matcher.IsItemAllowed(i))
                .ToList();
        }

        /// <summary>
        /// 获取需要恢复且有JSON文件的STRM文件
        /// </summary>
        public static List<BaseItem> GetStrmFilesNeedingRestoreWithJson(
            ILibraryManager libraryManager,
            IMediaInfoBackupInspector backupInspector)
        {
            var allStrmFiles = GetAllStrmFiles(libraryManager);
            return allStrmFiles
                .Where(item => ShouldRestoreFromJson(item, backupInspector, libraryManager))
                .ToList();
        }

        /// <summary>
        /// 从媒体流列表中获取最高分辨率的视频流
        /// </summary>
        private static MediaStream? GetHighestResolutionVideoStream(IEnumerable<MediaStream>? streams)
        {
            if (streams == null)
                return null;

            return streams
                .Where(s => s != null && s.Type == MediaStreamType.Video && s.Width.HasValue && s.Height.HasValue)
                .OrderByDescending(s => (long)(s.Width ?? 0) * (s.Height ?? 0))
                .FirstOrDefault();
        }
    }
}
