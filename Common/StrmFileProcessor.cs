using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Persistence;
using MediaBrowser.Model.Logging;
using MediaBrowser.Model.IO;
using MediaBrowser.Model.Serialization;
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

        public StrmFileProcessor(
            ILogger logger,
            ILibraryManager libraryManager,
            IFileSystem fileSystem,
            IItemRepository itemRepository,
            MediaInfoManager? mediaInfoManager = null)
        {
            _logger = logger;
            _libraryManager = libraryManager;
            _fileSystem = fileSystem;
            _itemRepository = itemRepository;
            _mediaInfoManager = mediaInfoManager ?? new MediaInfoManager(logger, libraryManager, itemRepository, Plugin.JsonSerializer!);
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

                if (MediaInfoHelper.HasCompleteMediaInfo(item))
                {
                    _logger.Info($"StrmTool - {item.Name} already has complete media info, skipping...");
                    monitor.Stop();
                    return ProcessResult.Skipped;
                }

                if (MediaInfoHelper.ShouldRestoreFromJson(item, _mediaInfoManager))
                {
                    _logger.Debug($"StrmTool - Found JSON file for {item.Name}, attempting to restore from JSON...");
                    
                    await _mediaInfoManager.RestoreItemAsync(item);
                    
                    var (hasVideo, hasAudio) = CheckMediaStreams(item);
                    
                    _logger.Debug($"StrmTool - {item.Name}: Restored from JSON. Video:{hasVideo}, Audio:{hasAudio}");
                    monitor.Stop();
                    return ProcessResult.RestoredFromJson;
                }
                else
                {
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
                        monitor.Stop();
                        return ProcessResult.ExtractionFailed;
                    }
                    else
                    {
                        try
                        {
                            await _mediaInfoManager.ExportItemAsync(item);
                            _logger.Debug($"StrmTool - {item.Name}: Media info exported to JSON file");
                            monitor.Stop();
                            return ProcessResult.ExtractedAndExported;
                        }
                        catch (Exception ex)
                        {
                            _logger.Error($"StrmTool - Error exporting {item.Name} to JSON: {ex.Message}");
                            monitor.Stop();
                            return ProcessResult.ExtractionFailed;
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.Error($"StrmTool - Error processing {item.Name} ({item.Path}): {ex.Message}");
                monitor.Stop();
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
