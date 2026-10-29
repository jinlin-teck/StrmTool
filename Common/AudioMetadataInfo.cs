using System;
using System.Collections.Generic;

namespace StrmTool.Common
{
    /// <summary>
    /// 随 -mediainfo.json 持久化导出的音频元数据模型
    /// </summary>
    public class AudioMetadataInfo
    {
        public bool? AudioTagsProbed { get; set; }
        public string? Title { get; set; }
        public string? SortName { get; set; }
        public string? Album { get; set; }
        public List<string>? Artists { get; set; }
        public List<string>? AlbumArtists { get; set; }
        public List<string>? Composers { get; set; }
        public List<string>? Genres { get; set; }
        public int? TrackNumber { get; set; }
        public int? DiscNumber { get; set; }
        public int? ProductionYear { get; set; }
        public DateTimeOffset? PremiereDate { get; set; }
        public Dictionary<string, string>? ProviderIds { get; set; }
    }
}
