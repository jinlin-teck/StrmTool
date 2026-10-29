using System;
using System.Collections.Generic;

namespace StrmTool.Common
{
    /// <summary>
    /// 元数据字符串/列表的规范化工具
    /// </summary>
    internal static class MetadataNormalizer
    {
        internal static string? NormalizeString(string? value)
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                return null;
            }

            var trimmed = value.Trim();
            return trimmed.Length > 0 ? trimmed : null;
        }

        /// <summary>
        /// 规范化已拆分的字符串列表（仅做 Trim 与去重，不按分隔符二次切分，避免破坏 "AC/DC" 等合法名称）
        /// </summary>
        internal static List<string>? NormalizeList(IEnumerable<string>? values)
        {
            if (values == null)
            {
                return null;
            }

            var result = new List<string>();
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            foreach (var raw in values)
            {
                if (string.IsNullOrWhiteSpace(raw))
                {
                    continue;
                }

                var trimmed = raw.Trim();
                if (trimmed.Length > 0 && seen.Add(trimmed))
                {
                    result.Add(trimmed);
                }
            }

            return result.Count > 0 ? result : null;
        }

        internal static Dictionary<string, string>? NormalizeProviderIds(IDictionary<string, string>? providerIds)
        {
            if (providerIds == null || providerIds.Count == 0)
            {
                return null;
            }

            var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var kvp in providerIds)
            {
                var key = NormalizeString(kvp.Key);
                var val = NormalizeString(kvp.Value);
                if (key != null && val != null)
                {
                    result[key] = val;
                }
            }

            return result.Count > 0 ? result : null;
        }
    }
}
