using MediaBrowser.Model.Plugins;

namespace StrmTool
{
    public class PluginConfiguration : BasePluginConfiguration
    {
        public int RefreshDelayMs { get; set; } = 1000;
    }
}
