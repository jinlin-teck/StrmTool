using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Reflection;
using System.Text.Json;
using System.Xml.Linq;
using Microsoft.Extensions.Logging;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Model.Globalization;

namespace StrmTool
{
    public class LocalizationManager
    {
        private readonly ILogger _logger;
        private readonly Dictionary<string, Dictionary<string, string>> _translations = new Dictionary<string, Dictionary<string, string>>();
        private readonly string _defaultCulture = "en";
        private readonly ILocalizationManager _localizationManager;
        private readonly IApplicationPaths _applicationPaths;
        private readonly string _currentCulture;

        public LocalizationManager(ILogger logger, ILocalizationManager localizationManager, IApplicationPaths applicationPaths = null)
        {
            _logger = logger;
            _localizationManager = localizationManager;
            _applicationPaths = applicationPaths;

            LoadAllTranslations();

            // 启动时读取一次配置，之后保持不变（重启生效）
            _currentCulture = DetectSystemCulture();
            _logger.LogInformation("StrmTool - Initial culture detected as: {0}", _currentCulture);
        }

        /// <summary>
        /// 获取当前的文化代码（启动时检测，重启生效）
        /// </summary>
        private string GetCurrentCulture()
        {
            return _currentCulture;
        }

        /// <summary>
        /// 检测系统文化代码（从配置或系统环境）
        /// </summary>
        private string DetectSystemCulture()
        {
            // 首先尝试从 Jellyfin 配置文件读取 UICulture
            var configCulture = GetCultureFromJellyfinConfig();
            if (!string.IsNullOrEmpty(configCulture) && _translations.ContainsKey(configCulture))
            {
                return configCulture;
            }

            try
            {
                // 尝试从系统 UI 文化获取
                var systemCulture = CultureInfo.CurrentUICulture ?? CultureInfo.InstalledUICulture;
                var twoLetterLang = systemCulture.TwoLetterISOLanguageName;

                // 优先处理中文
                if (twoLetterLang == "zh")
                {
                    if (_translations.ContainsKey("zh-CN"))
                    {
                        return "zh-CN";
                    }
                }

                // 检查完全匹配的文化代码
                if (_translations.ContainsKey(twoLetterLang))
                {
                    return twoLetterLang;
                }
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "StrmTool - Failed to detect system language");
            }

            // 回退到默认语言
            return _defaultCulture;
        }

        /// <summary>
        /// 从 Jellyfin 配置文件读取 UICulture（启动时调用一次）
        /// </summary>
        private string GetCultureFromJellyfinConfig()
        {
            try
            {
                if (_applicationPaths == null)
                {
                    return null;
                }

                var configPath = Path.Combine(_applicationPaths.ConfigurationDirectoryPath, "system.xml");
                if (!File.Exists(configPath))
                {
                    _logger.LogInformation("StrmTool - Jellyfin config file not found at {0}", configPath);
                    return null;
                }

                var doc = XDocument.Load(configPath);
                var uiCultureElement = doc.Root?.Element("UICulture");
                if (uiCultureElement != null && !string.IsNullOrEmpty(uiCultureElement.Value))
                {
                    var culture = uiCultureElement.Value.Trim();
                    _logger.LogDebug("StrmTool - Loaded UICulture from config: {0}", culture);
                    return culture;
                }
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "StrmTool - Failed to read Jellyfin config file");
            }

            return null;
        }

        private void LoadAllTranslations()
        {
            var assembly = Assembly.GetExecutingAssembly();
            var resourceNames = assembly.GetManifestResourceNames();

            _logger.LogDebug("StrmTool - Found {0} embedded resources", resourceNames.Length);
            foreach (var name in resourceNames)
            {
                _logger.LogDebug("StrmTool - Resource: {0}", name);
            }

            foreach (var resourceName in resourceNames)
            {
                if (resourceName.EndsWith(".json"))
                {
                    try
                    {
                        // 解析文化代码（格式：StrmTool.Resources.zh-CN.json）
                        var parts = resourceName.Split('.');
                        if (parts.Length < 3)
                        {
                            _logger.LogWarning("StrmTool - Invalid resource name format: {0}", resourceName);
                            continue;
                        }
                        var cultureName = parts[^2];

                        using var stream = assembly.GetManifestResourceStream(resourceName);
                        if (stream != null)
                        {
                            using var reader = new StreamReader(stream);
                            var content = reader.ReadToEnd();
                            var translations = JsonSerializer.Deserialize<Dictionary<string, string>>(content);

                            if (translations != null)
                            {
                                _translations[cultureName] = translations;
                                _logger.LogDebug("StrmTool - Loaded {0} translations for culture {1}",
                                    translations.Count, cultureName);
                            }
                        }
                    }
                    catch (Exception ex)
                    {
                        _logger.LogError(ex, "StrmTool - Failed to load translation file: {0}", resourceName);
                    }
                }
            }

            _logger.LogInformation("StrmTool - Loaded translations for {0} cultures: {1}",
                _translations.Count, string.Join(", ", _translations.Keys));

            if (!_translations.ContainsKey(_defaultCulture))
            {
                _logger.LogWarning("StrmTool - Default culture '{0}' translations not found", _defaultCulture);
            }
        }

        public string GetLocalizedString(string key, params object[] args)
        {
            return GetLocalizedString(key, _currentCulture, args);
        }

        public string GetLocalizedString(string key, string culture = null, params object[] args)
        {
            if (string.IsNullOrWhiteSpace(key))
                return key;

            var actualCulture = string.IsNullOrWhiteSpace(culture) ? GetCurrentCulture() : culture;

            // 尝试使用指定的文化
            if (_translations.TryGetValue(actualCulture, out var cultureTranslations) &&
                cultureTranslations.TryGetValue(key, out var translation))
            {
                if (args.Length > 0)
                {
                    try
                    {
                        return string.Format(translation, args);
                    }
                    catch (Exception ex)
                    {
                        _logger.LogError(ex, "StrmTool - Failed to format translated string '{0}' with args {1}",
                            translation, string.Join(", ", args));
                    }
                }
                return translation;
            }

            // 尝试使用语言前缀匹配（如 zh-CN 匹配 zh）
            var languageCode = actualCulture.Split('-', '_')[0];
            if (languageCode != actualCulture &&
                _translations.TryGetValue(languageCode, out var languageTranslations) &&
                languageTranslations.TryGetValue(key, out var languageTranslation))
            {
                if (args.Length > 0)
                {
                    try
                    {
                        return string.Format(languageTranslation, args);
                    }
                    catch (Exception ex)
                    {
                        _logger.LogError(ex, "StrmTool - Failed to format translated string '{0}' with args {1}",
                            languageTranslation, string.Join(", ", args));
                    }
                }
                return languageTranslation;
            }

            // 回退到默认文化
            if (_translations.TryGetValue(_defaultCulture, out var defaultTranslations) &&
                defaultTranslations.TryGetValue(key, out var defaultTranslation))
            {
                if (args.Length > 0)
                {
                    try
                    {
                        return string.Format(defaultTranslation, args);
                    }
                    catch (Exception ex)
                    {
                        _logger.LogError(ex, "StrmTool - Failed to format translated string '{0}' with args {1}",
                            defaultTranslation, string.Join(", ", args));
                    }
                }
                return defaultTranslation;
            }

            // 未找到翻译，返回原 key
            _logger.LogDebug("StrmTool - Translation not found for key '{0}' in culture '{1}'", key, actualCulture);
            return key;
        }
    }
}
