using System.Text.Json;
using System.Text.Json.Serialization;

namespace StrmTool.Common
{
    /// <summary>
    /// JSON序列化辅助类，提供统一的JSON配置
    /// </summary>
    public static class JsonHelper
    {
        /// <summary>
        /// 用于导出的JSON序列化选项（缓存以提高性能）
        /// </summary>
        private static readonly JsonSerializerOptions ExportOptions = CreateExportOptions();

        /// <summary>
        /// 用于导入的JSON序列化选项（缓存以提高性能）
        /// </summary>
        private static readonly JsonSerializerOptions ImportOptions = CreateImportOptions();

        /// <summary>
        /// 创建导出选项
        /// </summary>
        private static JsonSerializerOptions CreateExportOptions()
        {
            return new JsonSerializerOptions
            {
                WriteIndented = true,
                PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
                DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
            };
        }

        /// <summary>
        /// 创建导入选项
        /// </summary>
        private static JsonSerializerOptions CreateImportOptions()
        {
            var options = new JsonSerializerOptions
            {
                PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
                PropertyNameCaseInsensitive = true,
                NumberHandling = JsonNumberHandling.AllowReadingFromString
            };
            options.Converters.Add(new JsonStringEnumConverter(JsonNamingPolicy.CamelCase));
            return options;
        }

        /// <summary>
        /// 获取用于导出的JSON序列化选项
        /// </summary>
        public static JsonSerializerOptions GetExportOptions() => ExportOptions;

        /// <summary>
        /// 获取用于导入的JSON序列化选项
        /// </summary>
        public static JsonSerializerOptions GetImportOptions() => ImportOptions;
    }
}
