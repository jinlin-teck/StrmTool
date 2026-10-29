using System.Collections.Generic;
using MediaBrowser.Model.Dto;
using MediaBrowser.Model.Entities;

namespace StrmTool.Common
{
    /// <summary>
    /// -mediainfo.json 的持久化单元：媒体源、章节与音频附加信息
    /// </summary>
    public class MediaSourceWithChapters
    {
        public MediaSourceInfo? MediaSourceInfo { get; set; }
        public List<ChapterInfo> Chapters { get; set; } = new List<ChapterInfo>();
        public bool? ZeroFingerprintConfidence { get; set; }
        public string? EmbeddedImage { get; set; }
        public string? EmbeddedImageExtension { get; set; }
        public AudioMetadataInfo? AudioMetadata { get; set; }
    }
}
