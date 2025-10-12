using MediaBrowser.Common.Configuration;
using MediaBrowser.Common.Plugins;
using MediaBrowser.Model.Drawing;
using MediaBrowser.Model.Serialization;
using System;
using System.IO;

namespace StrmTool
{
    public class Plugin : BasePlugin<PluginConfiguration>, IHasThumbImage
    {
        public static Plugin Instance { get; private set; }
        public static string PluginName = "StrmTool";
        private Guid _id = new Guid("a1b2c3d4-e5f6-7890-abcd-ef1234567890");

        public Plugin(IApplicationPaths applicationPaths, IXmlSerializer xmlSerializer) : base(applicationPaths, xmlSerializer)
        {
            Instance = this;
        }

        public Stream GetThumbImage()
        {
            var type = GetType();
            return type.Assembly.GetManifestResourceStream(type.Namespace + ".Images.thumb.png");
        }

        public ImageFormat ThumbImageFormat => ImageFormat.Png;

        public override string Description => "Extracts media info from Strm targets using ffprobe";

        public override string Name => PluginName;

        public override Guid Id => _id;
    }
}
