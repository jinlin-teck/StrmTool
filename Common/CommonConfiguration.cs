using MediaBrowser.Controller.IO;
using MediaBrowser.Controller.Providers;
using MediaBrowser.Model.Configuration;
using MediaBrowser.Model.IO;
using System;

namespace StrmTool.Common
{
    /// <summary>
    /// 提供公共配置的类，用于在多个地方重用相同的配置设置
    /// </summary>
    public static class CommonConfiguration
    {
        /// <summary>
        /// 创建标准的元数据刷新选项
        /// </summary>
        /// <param name="fileSystem">文件系统实例</param>
        /// <returns>配置好的元数据刷新选项</returns>
        public static MetadataRefreshOptions CreateStandardMetadataRefreshOptions(IFileSystem fileSystem)
        {
            var directoryService = new DirectoryService(fileSystem);
            return new MetadataRefreshOptions(directoryService)
            {
                EnableRemoteContentProbe = true,
                ReplaceAllMetadata = true,
                MetadataRefreshMode = MetadataRefreshMode.ValidationOnly,
                EnableThumbnailImageExtraction = false,
                ImageRefreshMode = MetadataRefreshMode.ValidationOnly,
                ReplaceAllImages = false
            };
        }
        
        /// <summary>
        /// 标准处理延迟（毫秒）- 增加延迟避免对远程服务器造成压力
        /// </summary>
        public const int StandardProcessingDelayMs = 2000;

        /// <summary>
        /// 媒体信息JSON文件扩展名
        /// </summary>
        public const string MediaInfoFileExtension = "-mediainfo.json";
    }
}
