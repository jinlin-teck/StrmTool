using System;
using MediaBrowser.Model.Plugins;

namespace StrmTool
{
    public class PluginConfiguration : BasePluginConfiguration
    {
        private int _refreshDelayMs = 1000;
        private int _maxConcurrentExtract = 5;

        public int RefreshDelayMs
        {
            get => _refreshDelayMs;
            set => _refreshDelayMs = Math.Max(0, value);
        }

        /// <summary>
        /// 是否启用新入库 strm 文件自动提取媒体信息
        /// </summary>
        public bool EnableAutoExtract { get; set; } = true;

        /// <summary>
        /// 是否启用媒体信息缓存
        /// </summary>
        public bool EnableMediaInfoCache { get; set; } = true;

        /// <summary>
        /// 提取任务的并发数限制
        /// </summary>
        public int MaxConcurrentExtract
        {
            get => _maxConcurrentExtract;
            set => _maxConcurrentExtract = Math.Clamp(value, 1, 50);
        }
    }
}
