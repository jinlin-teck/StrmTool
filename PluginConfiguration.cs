using MediaBrowser.Model.Plugins;
using System;

namespace StrmTool
{
    public class PluginConfiguration : BasePluginConfiguration
    {
        public int RefreshDelayMs { get; set; } = 1000;
    }
}
