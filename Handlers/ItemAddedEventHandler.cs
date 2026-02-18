using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Model.Logging;
using MediaBrowser.Model.IO;
using MediaBrowser.Controller.Persistence;
using MediaBrowser.Model.Serialization;
using StrmTool.Common;
using System;
using System.Threading;
using System.Threading.Tasks;

namespace StrmTool.Handlers
{
    public class ItemAddedEventHandler
    {
        private readonly ILogger _logger;
        private readonly ILibraryManager _libraryManager;
        private readonly IFileSystem _fileSystem;
        private readonly IItemRepository _itemRepository;
        private readonly MediaInfoManager _mediaInfoManager;
        private readonly SemaphoreSlim _semaphore = new SemaphoreSlim(1, 1);

        public ItemAddedEventHandler(
            ILogger logger,
            ILibraryManager libraryManager,
            IFileSystem fileSystem,
            IItemRepository itemRepository,
            IJsonSerializer jsonSerializer,
            MediaInfoManager? mediaInfoManager = null)
        {
            _logger = logger;
            _libraryManager = libraryManager;
            _fileSystem = fileSystem;
            _itemRepository = itemRepository;
            _mediaInfoManager = mediaInfoManager ?? new MediaInfoManager(logger, libraryManager, itemRepository, jsonSerializer);
        }

        public void OnItemAdded(object sender, ItemChangeEventArgs e)
        {
            if (e.Item == null || string.IsNullOrEmpty(e.Item.Path) || 
                !MediaInfoHelper.IsStrmFile(e.Item.Path))
            {
                return;
            }

            _logger.Info($"StrmTool - New strm file detected: {e.Item.Name}");

            if (MediaInfoHelper.HasCompleteMediaInfo(e.Item))
            {
                _logger.Debug($"StrmTool - {e.Item.Name} already has complete media info, skipping");
                return;
            }

            _logger.Debug($"StrmTool - Processing new strm file: {e.Item.Name}");

            Task.Run(async () => await ProcessItemWithErrorHandlingAsync(e.Item));
        }

        private async Task ProcessItemWithErrorHandlingAsync(BaseItem item)
        {
            await _semaphore.WaitAsync();
            try
            {
                await ProcessItemAsync(item);
            }
            catch (Exception ex)
            {
                _logger.Error($"StrmTool - Error processing item {item.Name}: {ex.Message}");
            }
            finally
            {
                _semaphore.Release();
            }
        }

        private async Task ProcessItemAsync(BaseItem item)
        {
            await Task.Delay(CommonConfiguration.StandardProcessingDelayMs);

            var processor = new StrmFileProcessor(_logger, _libraryManager, _fileSystem, _itemRepository, _mediaInfoManager);
            var result = await processor.ProcessStrmFileAsync(item);

            switch (result)
            {
                case ProcessResult.Skipped:
                    _logger.Debug($"StrmTool - {item.Name} was skipped");
                    break;
                case ProcessResult.RestoredFromJson:
                    _logger.Info($"StrmTool - {item.Name} successfully restored from JSON");
                    break;
                case ProcessResult.ExtractedAndExported:
                    _logger.Info($"StrmTool - {item.Name} successfully extracted and exported");
                    break;
                case ProcessResult.ExtractionFailed:
                    _logger.Warn($"StrmTool - {item.Name} extraction failed, will be processed by scheduled task");
                    break;
                case ProcessResult.Failed:
                    _logger.Error($"StrmTool - {item.Name} processing failed");
                    break;
            }
        }
    }
}
