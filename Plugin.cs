using System;
using System.Collections.Generic;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Common.Plugins;
using MediaBrowser.Model.Plugins;
using MediaBrowser.Model.Serialization;

namespace StrmTool
{
    public class Plugin : BasePlugin<PluginConfiguration>, IHasWebPages
    {
        public static Plugin Instance { get; private set; }
        public static readonly string PluginName = "Strm Tool";
        private static readonly Guid _id = new Guid("6107fc8c-883a-4171-b70e-7590658706B9");

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

        public IEnumerable<PluginPageInfo> GetPages()
        {
            return new[]
            {
                new PluginPageInfo
                {
                    Name = Name,
                    EmbeddedResourcePath = "StrmTool.Configuration.configPage.html"
                }
            };
        }
    }
}
