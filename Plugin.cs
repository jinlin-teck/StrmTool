using System;
using System.Collections.Generic;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Common.Plugins;
using MediaBrowser.Model.Plugins;
using MediaBrowser.Model.Serialization;
using Microsoft.Extensions.Logging;
using MediaBrowser.Model.Globalization;

namespace StrmTool
{
    public class Plugin : BasePlugin<PluginConfiguration>, IHasWebPages
    {
        public static Plugin Instance { get; private set; }
        private static readonly Guid _id = new Guid("6107fc8c-883a-4171-b70e-7590658706B9");

        private readonly ILocalizationManager _localizationManager;
        private readonly LocalizationManager _customLocalization;
        private readonly ILogger<Plugin> _logger;

        public Plugin(
            IApplicationPaths applicationPaths, 
            IXmlSerializer xmlSerializer,
            ILocalizationManager localizationManager,
            ILogger<Plugin> logger) 
            : base(applicationPaths, xmlSerializer)
        {
            Instance = this;
            _localizationManager = localizationManager;
            _logger = logger;
            _customLocalization = new LocalizationManager(logger, localizationManager, applicationPaths);
        }

        public override string Description
        {
            get
            {
                return _customLocalization.GetLocalizedString("StrmTool.PluginDescription");
            }
        }

        public override string Name
        {
            get { return _customLocalization.GetLocalizedString("StrmTool.PluginName"); }
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

        /// <summary>
        /// 获取本地化字符串
        /// </summary>
        /// <param name="key">字符串键</param>
        /// <param name="args">格式化参数</param>
        /// <returns>本地化后的字符串</returns>
        public string GetLocalizedString(string key, params object[] args)
        {
            try
            {
                // 首先尝试自定义资源
                var translation = _customLocalization.GetLocalizedString(key);

                if (args.Length > 0 && !string.IsNullOrEmpty(translation) && translation != key)
                {
                    try
                    {
                        return string.Format(translation, args);
                    }
                    catch (FormatException ex)
                    {
                        _logger.LogError(ex, "StrmTool - Invalid format string for key: {Key}", key);
                        return translation;  // 返回未格式化的翻译
                    }
                }

                return translation;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "StrmTool - Failed to get localized string: {Key}", key);
                return key;
            }
        }
    }
}
