using MediaBrowser.Common.Configuration;
using MediaBrowser.Common.Plugins;
using MediaBrowser.Model.Plugins;
using MediaBrowser.Model.Serialization;
using System;

namespace StrmTool
{
    public class Plugin : BasePlugin<PluginConfiguration>
    {
        public static Plugin Instance { get; private set; }
        public static string PluginName = "Strm Tool";
        private Guid _id = new Guid("6107fc8c-883a-4171-b70e-7590658706d8");

        public Plugin(IApplicationPaths applicationPaths, IXmlSerializer xmlSerializer) : base(applicationPaths, xmlSerializer)
        {
            Instance = this;
        }

        public override string Description
        {
            get
            {
                return "Extracts info from Strm targets";
            }
        }

        public override string Name
        {
            get { return PluginName; }
        }

        public override Guid Id
        {
            get { return _id; }
        }
    }
}
