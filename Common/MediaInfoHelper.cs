using System.Threading;
using System.Collections.Generic;
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
        public static bool HasCompleteMediaInfo(BaseItem? item, MediaInfoManager? mediaInfoManager = null)
        {
            if (item == null) return false;
            return HasCompleteMediaInfo(item, item.GetMediaStreams(), mediaInfoManager);
        }

        /// <summary>
        /// 基于已获取的媒体流列表判断媒体项信息是否完整，避免重复查询仓储
        /// </summary>
        public static bool HasCompleteMediaInfo(
            BaseItem? item,
            IEnumerable<MediaStream>? streams,
            MediaInfoManager? mediaInfoManager = null)
        {
            if (item == null || !HasCompleteMediaInfo(streams))
            {
                return false;
            }

            if (item is Audio audio)
            {
                if (AudioMetadataHelper.HasMissingLocalLyrics(audio, streams))
                {
                    return false;
                }

                if (AudioMetadataHelper.HasMissingAudioMetadata(audio))
                {
                    // 若已执行过远程探测 (AudioTagsProbed == true) 且 JSON/目录兜底均无专辑与歌手可恢复，
                    // 说明源文件本身无标签且目录无法推断，视为已收敛终态，避免每日重复远程 ffprobe。
                    if (mediaInfoManager != null &&
                        mediaInfoManager.HasProbedAudioTagsInJson(audio) &&
                        !mediaInfoManager.CanRestoreAudioMetadataFromJson(audio))
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
            return streams.Any(s => s.Type == MediaStreamType.Video || s.Type == MediaStreamType.Audio);
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
            item.Size = mediaSourceInfo.Size.GetValueOrDefault();
            item.RunTimeTicks = mediaSourceInfo.RunTimeTicks;
            item.Container = mediaSourceInfo.Container;
            item.TotalBitrate = mediaSourceInfo.Bitrate.GetValueOrDefault();

            var videoStream = GetHighestResolutionVideoStream(mediaSourceInfo.MediaStreams);
            if (videoStream != null)
            {
                item.Width = videoStream.Width ?? 0;
                item.Height = videoStream.Height ?? 0;
            }

            if (item is Audio audio)
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
        /// <param name="item">媒体项</param>
        /// <param name="mediaInfoManager">媒体信息管理器</param>
        /// <returns>是否存在JSON文件</returns>
        public static bool HasJsonFile(BaseItem item, MediaInfoManager mediaInfoManager)
        {
            var jsonFilePath = mediaInfoManager.GetMediaInfoJsonPath(item);
            return System.IO.File.Exists(jsonFilePath);
        }

        /// <summary>
        /// 检查媒体项是否需要从JSON文件恢复
        /// </summary>
        public static bool ShouldRestoreFromJson(BaseItem item, MediaInfoManager mediaInfoManager)
        {
            return !HasCompleteMediaInfo(item, mediaInfoManager) && HasJsonFile(item, mediaInfoManager);
        }

        /// <summary>
        /// 获取库中所有的STRM文件
        /// </summary>
        public static List<BaseItem> GetAllStrmFiles(ILibraryManager libraryManager)
        {
            var query = new InternalItemsQuery
            {
                HasPath = true,
                Recursive = true,
                IncludeItemTypes = new[] { "Movie", "Episode", "Video", "Audio" }
            };

            return libraryManager.GetItemList(query)
                .Where(i => IsStrmFile(i.Path))
                .ToList();
        }

        /// <summary>
        /// 获取需要恢复且有JSON文件的STRM文件
        /// </summary>
        public static List<BaseItem> GetStrmFilesNeedingRestoreWithJson(ILibraryManager libraryManager, MediaInfoManager mediaInfoManager)
        {
            var allStrmFiles = GetAllStrmFiles(libraryManager);
            return allStrmFiles.Where(item => ShouldRestoreFromJson(item, mediaInfoManager)).ToList();
        }

        /// <summary>
        /// 获取包含完整媒体信息的STRM文件
        /// </summary>
        public static List<BaseItem> GetStrmFilesWithCompleteMediaInfo(
            ILibraryManager libraryManager,
            MediaInfoManager? mediaInfoManager = null)
        {
            var allStrmFiles = GetAllStrmFiles(libraryManager);
            return allStrmFiles.Where(item => HasCompleteMediaInfo(item, mediaInfoManager)).ToList();
        }

        /// <summary>
        /// 从媒体流列表中获取最高分辨率的视频流
        /// </summary>
        public static MediaStream? GetHighestResolutionVideoStream(IEnumerable<MediaStream>? streams)
        {
            if (streams == null)
                return null;

            return streams
                .Where(s => s.Type == MediaStreamType.Video && s.Width.HasValue && s.Height.HasValue)
                .OrderByDescending(s => (long)(s.Width ?? 0) * (s.Height ?? 0))
                .FirstOrDefault();
        }
    }
}
