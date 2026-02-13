using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Text.Json;
using System.Threading.Tasks;
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
        private readonly string _currentCulture;

        public LocalizationManager(ILogger logger, ILocalizationManager localizationManager)
        {
            _logger = logger;
            // 尝试从 localizationManager 获取当前语言
            _currentCulture = _defaultCulture; // 默认值
            
            try
            {
                // 尝试获取语言设置
                var cultures = localizationManager.GetCultures();
                if (cultures != null)
                {
                    int cultureCount = 0;
                    foreach (var culture in cultures)
                    {
                        cultureCount++;
                    }
                    _logger.LogDebug("StrmTool - Found {0} supported cultures", cultureCount);
                }
                
                // 尝试获取系统配置中的语言设置
                // 如果未找到语言配置，则使用默认值
                
                // 检查是否有 Chinese (Simplified) 语言支持
                var chineseCulture = localizationManager.FindLanguageInfo("zh-CN");
                if (chineseCulture != null)
                {
                    _logger.LogDebug("StrmTool - Found Chinese (Simplified) culture: {0}", chineseCulture.Name);
                    
                    // 尝试确定是否应该使用中文
                    // 检查系统语言或默认语言是否是中文
                    _currentCulture = "zh-CN"; // 暂时强制设置为中文
                }
                else
                {
                    _logger.LogDebug("StrmTool - Chinese (Simplified) culture not found");
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "StrmTool - Failed to get cultures from ILocalizationManager");
            }
            
            LoadAllTranslations();
            _logger.LogDebug("StrmTool - Current culture set to: {0}", _currentCulture);
        }

        private void LoadAllTranslations()
        {
            var assembly = Assembly.GetExecutingAssembly();
            var resourceNames = assembly.GetManifestResourceNames();

            foreach (var resourceName in resourceNames)
            {
                if (resourceName.EndsWith(".json"))
                {
                    try
                    {
                        // 解析文化代码（格式：StrmTool.Resources.zh-CN.json）
                        var parts = resourceName.Split('.');
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

            var actualCulture = string.IsNullOrWhiteSpace(culture) ? _currentCulture : culture;

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
