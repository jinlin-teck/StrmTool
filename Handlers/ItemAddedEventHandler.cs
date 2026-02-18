using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Model.Logging;
using MediaBrowser.Model.IO;
using MediaBrowser.Controller.IO;
using MediaBrowser.Controller.Providers;
using MediaBrowser.Model.Configuration;
using MediaBrowser.Model.Entities;
using StrmTool.Common;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using MediaBrowser.Controller.Persistence;

namespace StrmTool.Handlers
{
    public class ItemAddedEventHandler
    {
        private readonly ILogger _logger;
        private readonly ILibraryManager _libraryManager;
        private readonly IFileSystem _fileSystem;
        private readonly IItemRepository _itemRepository;

        public ItemAddedEventHandler(ILogger logger, ILibraryManager libraryManager, IFileSystem fileSystem, IItemRepository itemRepository)
        {
            _logger = logger;
            _libraryManager = libraryManager;
            _fileSystem = fileSystem;
            _itemRepository = itemRepository;
        }

        public void OnItemAdded(object sender, ItemChangeEventArgs e)
        {
            // 使用 _ = 防止 async void 的问题
            _ = OnItemAddedAsync(e);
        }

        private async Task OnItemAddedAsync(ItemChangeEventArgs e)
        {
            try
            {
                // 只处理strm文件
                if (e.Item == null || string.IsNullOrEmpty(e.Item.Path) || 
                    !MediaInfoHelper.IsStrmFile(e.Item.Path))
                {
                    return;
                }

                _logger.Info($"StrmTool - New strm file detected: {e.Item.Name}");

                // 检查是否已经有完整的媒体信息
                if (MediaInfoHelper.HasCompleteMediaInfo(e.Item))
                {
                    _logger.Debug($"StrmTool - {e.Item.Name} already has complete media info, skipping");
                    return;
                }

                _logger.Debug($"StrmTool - Processing new strm file: {e.Item.Name}");

                // 添加延迟，避免对远程服务器造成压力
                await Task.Delay(CommonConfiguration.StandardProcessingDelayMs);

                // 创建处理器实例
                var processor = new StrmFileProcessor(_logger, _libraryManager, _fileSystem, _itemRepository);
                
                // 使用统一的处理器处理文件
                var result = await processor.ProcessStrmFileAsync(e.Item);

                // 根据处理结果记录日志
                switch (result)
                {
                    case ProcessResult.Skipped:
                        _logger.Debug($"StrmTool - {e.Item.Name} was skipped");
                        break;
                    case ProcessResult.RestoredFromJson:
                        _logger.Info($"StrmTool - {e.Item.Name} successfully restored from JSON");
                        break;
                    case ProcessResult.ExtractedAndExported:
                        _logger.Info($"StrmTool - {e.Item.Name} successfully extracted and exported");
                        break;
                    case ProcessResult.ExtractionFailed:
                        _logger.Warn($"StrmTool - {e.Item.Name} extraction failed, will be processed by scheduled task");
                        break;
                    case ProcessResult.Failed:
                        _logger.Error($"StrmTool - {e.Item.Name} processing failed");
                        break;
                }
            }
            catch (Exception ex)
            {
                _logger.Error($"StrmTool - Error in item added event handler: {ex.Message}");
            }
        }
    }
}
