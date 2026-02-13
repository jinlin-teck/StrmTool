using MediaBrowser.Model.Plugins;

namespace StrmTool
{
    public class PluginConfiguration : BasePluginConfiguration
    {
        public int RefreshDelayMs { get; set; } = 1000;

        /// <summary>
        /// 是否启用新入库 strm 文件自动提取媒体信息
        /// </summary>
        public bool EnableAutoExtract { get; set; } = true;

        /// <summary>
        /// 是否启用媒体信息缓存
        /// </summary>
        public bool EnableMediaInfoCache { get; set; } = true;
    }
}
