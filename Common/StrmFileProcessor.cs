using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Persistence;
using MediaBrowser.Model.Logging;
using MediaBrowser.Model.IO;
using System;
using System.Linq;
using System.Threading.Tasks;

namespace StrmTool.Common
{
    /// <summary>
    /// STRM文件处理类，提供统一的处理逻辑
    /// </summary>
    public class StrmFileProcessor
    {
        private readonly ILogger _logger;
        private readonly ILibraryManager _libraryManager;
        private readonly IFileSystem _fileSystem;
        private readonly IItemRepository _itemRepository;
        private readonly MediaInfoManager _mediaInfoManager;

        public StrmFileProcessor(ILogger logger, ILibraryManager libraryManager, IFileSystem fileSystem, IItemRepository itemRepository)
        {
            _logger = logger;
            _libraryManager = libraryManager;
            _fileSystem = fileSystem;
            _itemRepository = itemRepository;
            _mediaInfoManager = new MediaInfoManager(logger, libraryManager, itemRepository);
        }

        /// <summary>
        /// 检查媒体项的媒体流信息
        /// </summary>
        private (bool hasVideo, bool hasAudio) CheckMediaStreams(BaseItem item)
        {
            var streams = item.GetMediaStreams() ?? new System.Collections.Generic.List<MediaBrowser.Model.Entities.MediaStream>();
            bool hasVideo = streams.Any(s => s.Type == MediaBrowser.Model.Entities.MediaStreamType.Video);
            bool hasAudio = streams.Any(s => s.Type == MediaBrowser.Model.Entities.MediaStreamType.Audio);
            return (hasVideo, hasAudio);
        }

        /// <summary>
        /// 处理单个STRM文件
        /// </summary>
        /// <param name="item">媒体项</param>
        /// <param name="cancellationToken">取消令牌</param>
        /// <returns>处理结果</returns>
        public async Task<ProcessResult> ProcessStrmFileAsync(BaseItem item, System.Threading.CancellationToken cancellationToken = default)
        {
            using var monitor = new PerformanceMonitor(_logger, "Processing", item.Name);
            
            try
            {
                _logger.Debug($"StrmTool - Processing {item.Name}");

                // 检查是否已经有完整的媒体信息
                if (MediaInfoHelper.HasCompleteMediaInfo(item))
                {
                    _logger.Info($"StrmTool - {item.Name} already has complete media info, skipping...");
                    return ProcessResult.Skipped;
                }

                // 先检查是否存在JSON文件
                if (MediaInfoHelper.ShouldRestoreFromJson(item, _mediaInfoManager))
                {
                    _logger.Debug($"StrmTool - Found JSON file for {item.Name}, attempting to restore from JSON...");
                    
                    // 如果存在JSON文件，则直接从JSON文件恢复
                    await _mediaInfoManager.RestoreItemAsync(item);
                    
                    var (hasVideo, hasAudio) = CheckMediaStreams(item);
                    
                    _logger.Debug($"StrmTool - {item.Name}: Restored from JSON. Video:{hasVideo}, Audio:{hasAudio}");
                    return ProcessResult.RestoredFromJson;
                }
                else
                {
                    // 如果不存在JSON文件，则执行原有的元数据提取操作
                    _logger.Debug($"StrmTool - No JSON file found for {item.Name}, extracting metadata...");
                    
                    var beforeStreams = item.GetMediaStreams() ?? new System.Collections.Generic.List<MediaBrowser.Model.Entities.MediaStream>();
                    _logger.Debug($"StrmTool - Before: {beforeStreams.Count} streams");

                    var options = CommonConfiguration.CreateStandardMetadataRefreshOptions(_fileSystem);
                    var result = await item.RefreshMetadata(options, cancellationToken);

                    var (hasVideo, hasAudio) = CheckMediaStreams(item);

                    _logger.Info($"StrmTool - {item.Name}: Extracted metadata. Streams {beforeStreams.Count}→{item.GetMediaStreams()?.Count ?? 0}. Video:{hasVideo}, Audio:{hasAudio}");

                    if (!hasVideo || !hasAudio)
                    {
                        _logger.Warn($"StrmTool - {item.Name} may still lack full media info");
                        return ProcessResult.ExtractionFailed;
                    }
                    else
                    {
                        // 如果提取成功，则保存到JSON文件
                        try
                        {
                            await _mediaInfoManager.ExportItemAsync(item);
                            _logger.Debug($"StrmTool - {item.Name}: Media info exported to JSON file");
                            return ProcessResult.ExtractedAndExported;
                        }
                        catch (Exception ex)
                        {
                            _logger.Error($"StrmTool - Error exporting {item.Name} to JSON: {ex.Message}");
                            return ProcessResult.ExtractionFailed;
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.Error($"StrmTool - Error processing {item.Name} ({item.Path}): {ex.Message}");
                return ProcessResult.Failed;
            }
        }
    }

    /// <summary>
    /// 处理结果枚举
    /// </summary>
    public enum ProcessResult
    {
        /// <summary>
        /// 已跳过（已有完整信息）
        /// </summary>
        Skipped,
        
        /// <summary>
        /// 从JSON恢复成功
        /// </summary>
        RestoredFromJson,
        
        /// <summary>
        /// 提取并导出成功
        /// </summary>
        ExtractedAndExported,
        
        /// <summary>
        /// 提取失败
        /// </summary>
        ExtractionFailed,
        
        /// <summary>
        /// 处理失败
        /// </summary>
        Failed
    }
}
