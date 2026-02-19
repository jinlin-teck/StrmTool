using System;
using System.Threading;
using System.Threading.Tasks;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.MediaEncoding;
using MediaBrowser.Controller.Persistence;
using MediaBrowser.Model.Logging;
using MediaBrowser.Model.Serialization;
using StrmTool.Common;

namespace StrmTool.Handlers
{
    public class ItemAddedEventHandler
    {
        private readonly ILogger _logger;
        private readonly ILibraryManager _libraryManager;
        private readonly IItemRepository _itemRepository;
        private readonly MediaInfoManager _mediaInfoManager;
        private readonly IMediaProbeManager _mediaProbeManager;
        private readonly CancellationTokenSource? _cancellationTokenSource;
        private readonly SemaphoreSlim _semaphore;

        public ItemAddedEventHandler(
            ILogger logger,
            ILibraryManager libraryManager,
            IItemRepository itemRepository,
            IJsonSerializer jsonSerializer,
            IMediaProbeManager mediaProbeManager,
            CancellationTokenSource? cancellationTokenSource = null,
            MediaInfoManager? mediaInfoManager = null)
        {
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
            _libraryManager = libraryManager ?? throw new ArgumentNullException(nameof(libraryManager));
            _itemRepository = itemRepository ?? throw new ArgumentNullException(nameof(itemRepository));
            _mediaProbeManager = mediaProbeManager ?? throw new ArgumentNullException(nameof(mediaProbeManager));
            _cancellationTokenSource = cancellationTokenSource;
            _semaphore = new SemaphoreSlim(1, 1);
            _mediaInfoManager = mediaInfoManager ?? new MediaInfoManager(logger, libraryManager, itemRepository, jsonSerializer);
        }

        public void OnItemAdded(object sender, ItemChangeEventArgs e)
        {
            if (e.Item == null || string.IsNullOrEmpty(e.Item.Path) || 
                !MediaInfoHelper.IsStrmFile(e.Item.Path))
            {
                return;
            }

            if (_cancellationTokenSource?.IsCancellationRequested == true)
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

            var cancellationToken = _cancellationTokenSource?.Token ?? CancellationToken.None;
            Task.Run(async () => await ProcessItemWithErrorHandlingAsync(e.Item, cancellationToken), cancellationToken);
        }

        private async Task ProcessItemWithErrorHandlingAsync(BaseItem item, CancellationToken cancellationToken)
        {
            if (cancellationToken.IsCancellationRequested)
            {
                return;
            }

            await _semaphore.WaitAsync(cancellationToken);
            try
            {
                if (cancellationToken.IsCancellationRequested)
                {
                    return;
                }

                await ProcessItemAsync(item, cancellationToken);
            }
            catch (OperationCanceledException)
            {
                _logger.Debug($"StrmTool - Processing cancelled for {item.Name}");
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

        private async Task ProcessItemAsync(BaseItem item, CancellationToken cancellationToken)
        {
            await Task.Delay(CommonConfiguration.StandardProcessingDelayMs, cancellationToken);

            if (cancellationToken.IsCancellationRequested)
            {
                return;
            }

            var jsonSerializer = Plugin.JsonSerializer ?? throw new InvalidOperationException("Plugin.JsonSerializer is not initialized");
            var processor = new StrmFileProcessor(_logger, _libraryManager, _itemRepository, _mediaProbeManager, jsonSerializer, _mediaInfoManager);
            var result = await processor.ProcessStrmFileAsync(item, cancellationToken);

            LogProcessResult(item.Name, result);
        }

        private void LogProcessResult(string itemName, ProcessResult result)
        {
            switch (result)
            {
                case ProcessResult.Skipped:
                    _logger.Debug($"StrmTool - {itemName} was skipped");
                    break;
                case ProcessResult.RestoredFromJson:
                    _logger.Info($"StrmTool - {itemName} successfully restored from JSON");
                    break;
                case ProcessResult.ExtractedAndExported:
                    _logger.Info($"StrmTool - {itemName} successfully extracted and exported");
                    break;
                case ProcessResult.ExtractionFailed:
                    _logger.Warn($"StrmTool - {itemName} extraction failed, will be processed by scheduled task");
                    break;
                case ProcessResult.Failed:
                    _logger.Error($"StrmTool - {itemName} processing failed");
                    break;
            }
        }
    }
}
