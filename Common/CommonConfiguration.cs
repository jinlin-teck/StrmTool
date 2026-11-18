using System;

namespace StrmTool.Common
{
    /// <summary>
    /// 提供公共配置的类，用于在多个地方重用相同的配置设置
    /// </summary>
    public static class CommonConfiguration
    {
        /// <summary>
        /// 插件 GUID
        /// </summary>
        public static readonly Guid PluginId = new Guid("a1b2c3d4-e5f6-7890-abcd-ef1234567891");

        /// <summary>
        /// 标准处理延迟（毫秒）- 增加延迟避免对远程服务器造成压力
        /// </summary>
        public const int StandardProcessingDelayMs = 2000;

        /// <summary>
        /// 最大处理延迟（毫秒）
        /// </summary>
        public const int MaxProcessingDelayMs = 60000;

        /// <summary>
        /// 默认处理延迟随机抖动（毫秒）
        /// </summary>
        public const int DefaultProcessingDelayJitterMs = 0;

        /// <summary>
        /// 最大处理延迟随机抖动（毫秒）
        /// </summary>
        public const int MaxProcessingDelayJitterMs = 30000;

        /// <summary>
        /// 媒体信息JSON文件扩展名
        /// </summary>
        public const string MediaInfoFileExtension = "-mediainfo.json";

        /// <summary>
        /// 最大并发处理数（默认值）
        /// </summary>
        public const int MaxConcurrency = 3;

        /// <summary>
        /// 最小并发处理数
        /// </summary>
        public const int MinConcurrency = 1;

        /// <summary>
        /// 最大并发处理数上限
        /// </summary>
        public const int MaxConcurrencyLimit = 10;
    }
}
