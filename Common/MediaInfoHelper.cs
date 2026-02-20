using System.Collections.Generic;
using System.Linq;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Entities;

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
        /// 检查媒体项是否包含媒体流信息
        /// 只要有音频流或视频流中的任意一种，就认为媒体信息已存在
        /// </summary>
        public static bool HasCompleteMediaInfo(BaseItem item)
        {
            var streams = item.GetMediaStreams() ?? new List<MediaStream>();
            return streams.Any(s => s.Type == MediaStreamType.Video || s.Type == MediaStreamType.Audio);
        }

        /// <summary>
        /// 检查媒体项是否存在JSON文件
        /// </summary>
        /// <param name="item">媒体项</param>
        /// <param name="mediaInfoManager">媒体信息管理器</param>
        /// <returns>是否存在JSON文件</returns>
        public static bool HasJsonFile(BaseItem item, MediaInfoManager mediaInfoManager)
        {
            if (mediaInfoManager == null)
                return false;

            var jsonFilePath = mediaInfoManager.GetMediaInfoJsonPath(item);
            return System.IO.File.Exists(jsonFilePath);
        }

        /// <summary>
        /// 检查媒体项是否需要从JSON文件恢复
        /// </summary>
        public static bool ShouldRestoreFromJson(BaseItem item, MediaInfoManager mediaInfoManager)
        {
            if (mediaInfoManager == null)
                return false;

            return !HasCompleteMediaInfo(item) && HasJsonFile(item, mediaInfoManager);
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
        public static List<BaseItem> GetStrmFilesWithCompleteMediaInfo(ILibraryManager libraryManager)
        {
            var allStrmFiles = GetAllStrmFiles(libraryManager);
            return allStrmFiles.Where(HasCompleteMediaInfo).ToList();
        }
    }
}
