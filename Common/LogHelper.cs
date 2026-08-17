using MediaBrowser.Model.Logging;
using System;

namespace StrmTool.Common
{
    /// <summary>
    /// 日志辅助类，提供统一的日志处理方式
    /// </summary>
    public static class LogHelper
    {
        private const string LogPrefix = "StrmTool";

        /// <summary>
        /// 记录调试日志
        /// </summary>
        public static void Debug(ILogger logger, string message)
        {
            logger.Debug($"{LogPrefix} - {message}");
        }

        /// <summary>
        /// 记录信息日志
        /// </summary>
        public static void Info(ILogger logger, string message)
        {
            logger.Info($"{LogPrefix} - {message}");
        }

        /// <summary>
        /// 记录警告日志
        /// </summary>
        public static void Warn(ILogger logger, string message)
        {
            logger.Warn($"{LogPrefix} - {message}");
        }

        /// <summary>
        /// 记录错误日志
        /// </summary>
        public static void Error(ILogger logger, string message)
        {
            logger.Error($"{LogPrefix} - {message}");
        }

        /// <summary>
        /// 记录错误日志和异常信息
        /// </summary>
        public static void ErrorException(ILogger logger, string message, Exception exception)
        {
            var errorMessage = $"{LogPrefix} - {message}";

            if (exception != null)
            {
                logger.ErrorException(errorMessage, exception);
            }
            else
            {
                logger.Error(errorMessage);
            }
        }
    }
}
