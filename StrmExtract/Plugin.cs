using MediaBrowser.Common.Configuration;
using MediaBrowser.Common.Plugins;
using MediaBrowser.Model.Drawing;
using MediaBrowser.Model.Serialization;
using System;
using System.IO;

namespace StrmExtract
{
    public class Plugin : BasePlugin<PluginConfiguration>, IHasThumbImage
    {

        public static Plugin Instance { get; private set; }
        public static string PluginName = "Strm Extract";
        private Guid _id = new Guid("6107fc8c-443a-4171-b70e-7590658706d8");

        public Plugin(IApplicationPaths applicationPaths, IXmlSerializer xmlSerializer) : base(applicationPaths, xmlSerializer)
        {
            Instance = this;
        }

        public Stream GetThumbImage()
        {
            var type = GetType();
            return type.Assembly.GetManifestResourceStream(type.Namespace + ".Images.thumb.png");
        }

        public ImageFormat ThumbImageFormat
        {
            get
            {
                return ImageFormat.Png;
            }
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


