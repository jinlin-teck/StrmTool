using System.Globalization;

namespace StrmTool.Common
{
    /// <summary>
    /// 任务本地化辅助类，提供统一的多语言文本处理
    /// </summary>
    public static class TaskLocalizer
    {
        /// <summary>
        /// 获取任务分类名称
        /// </summary>
        public static string GetCategory()
        {
            return T("Strm 工具", "Strm Tool");
        }

        /// <summary>
        /// 获取提取任务的名称
        /// </summary>
        public static string GetExtractTaskName()
        {
            return T("提取 Strm 媒体信息", "Strm File Media Info Extraction");
        }

        /// <summary>
        /// 获取提取任务的描述
        /// </summary>
        public static string GetExtractTaskDescription()
        {
            return T(
                "扫描库中的 Strm 文件并提取完整的媒体信息（视频流、音频流、内嵌字幕、章节信息等等）。",
                "Scans Strm files in the library and extracts complete media information (video and audio streams, embedded subtitles, chapter information, etc.).");
        }

        /// <summary>
        /// 获取导出任务的名称
        /// </summary>
        public static string GetExportTaskName()
        {
            return T("导出 STRM 媒体信息", "Export Strm File Media Info");
        }

        /// <summary>
        /// 获取导出任务的描述
        /// </summary>
        public static string GetExportTaskDescription()
        {
            return T(
                "导出库中所有 Strm 文件的媒体信息到 JSON 文件，便于备份或迁移。",
                "Exports media information of all Strm files in the library to JSON files for backup or migration.");
        }

        /// <summary>
        /// 获取恢复任务的名称
        /// </summary>
        public static string GetRestoreTaskName()
        {
            return T("恢复 STRM 媒体信息", "Restore STRM Media Info");
        }

        /// <summary>
        /// 获取恢复任务的描述
        /// </summary>
        public static string GetRestoreTaskDescription()
        {
            return T(
                "从之前导出的 JSON 文件中恢复 Emby 媒体库中 STRM 文件的媒体信息。",
                "Restores media information of STRM files in the Emby library from previously exported JSON files.");
        }

        private static string T(string chinese, string english)
        {
            return CultureInfo.CurrentUICulture.TwoLetterISOLanguageName == "zh" ? chinese : english;
        }
    }
}
