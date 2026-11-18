using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Audio;

namespace StrmTool.Common
{
    /// <summary>
    /// 媒体信息 JSON 备份状态只读检查接口，用于解耦辅助方法与高层门面 MediaInfoManager 的循环依赖
    /// </summary>
    public interface IMediaInfoBackupInspector
    {
        string GetMediaInfoJsonPath(BaseItem item);

        bool HasProbedAudioTagsInJson(BaseItem item);

        bool CanRestoreAudioMetadataFromJson(Audio audio);
    }
}
