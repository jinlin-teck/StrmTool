using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Audio;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Persistence;
using MediaBrowser.Model.Logging;
using MediaBrowser.Model.Serialization;

namespace StrmTool.Common
{
    /// <summary>
    /// 媒体信息导出/恢复的批量编排与门面。
    /// JSON 读写、导出与恢复分别由 MediaInfoJsonStore / MediaInfoExporter / MediaInfoRestorer 承担。
    /// </summary>
    public class MediaInfoManager : IMediaInfoBackupInspector
    {
        private readonly ILogger _logger;
        private readonly ILibraryManager _libraryManager;
        private readonly IItemRepository _itemRepository;
        private readonly MediaInfoJsonStore _jsonStore;
        private readonly MediaInfoExporter _exporter;
        private readonly MediaInfoRestorer _restorer;

        public MediaInfoManager(
            ILogger logger,
            ILibraryManager libraryManager,
            IItemRepository itemRepository,
            IJsonSerializer jsonSerializer)
        {
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
            _libraryManager = libraryManager ?? throw new ArgumentNullException(nameof(libraryManager));
            _itemRepository = itemRepository ?? throw new ArgumentNullException(nameof(itemRepository));
            if (jsonSerializer == null) throw new ArgumentNullException(nameof(jsonSerializer));

            _jsonStore = new MediaInfoJsonStore(_logger, jsonSerializer);
            _exporter = new MediaInfoExporter(_logger, _libraryManager, _itemRepository, _jsonStore);
            _restorer = new MediaInfoRestorer(_logger, _libraryManager, _itemRepository, _jsonStore);
        }

        /// <summary>
        /// 插件卸载时清理进程级缓存
        /// </summary>
        public static void ClearCachedState()
        {
            MediaInfoJsonStore.ClearCache();
            StrmFileProcessor.ClearInFlightState();
            LibraryScopeFilter.InvalidateScopedCountCache(force: true);
        }

        /// <summary>
        /// 扫描 Emby 媒体库并导出媒体信息。
        /// </summary>
        public async Task ExportAllAsync(IProgress<double> progress, CancellationToken cancellationToken)
        {
            var validItems = MediaInfoHelper.GetAllStrmFiles(_libraryManager)
                .Where(item =>
                {
                    var destination = GetWriteMediaInfoJsonPath(item);
                    var sidecar = _jsonStore.GetSidecarJsonPath(item);
                    return (!MediaInfoJsonStore.PathStringComparer.Equals(destination, sidecar) &&
                            !File.Exists(destination) && File.Exists(sidecar)) ||
                           MediaInfoHelper.HasCompleteMediaInfo(item, this, _libraryManager);
                })
                .ToList();
            Common.LogHelper.Info(_logger, $"Found {validItems.Count} STRM files eligible for export or backup migration");

            int skipped = 0;
            int migratedCount = 0;

            int exported = await ProcessItemsAsync(
                validItems,
                "export",
                async (item, ct) =>
                {
                    string filePath = GetWriteMediaInfoJsonPath(item);
                    var sidecarPath = _jsonStore.GetSidecarJsonPath(item);
                    if (!File.Exists(filePath) &&
                        !MediaInfoJsonStore.PathStringComparer.Equals(filePath, sidecarPath) &&
                        File.Exists(sidecarPath))
                    {
                        // 先保留完整历史备份，包括数据库中可能已不存在的章节、封面及未知字段。
                        var migrated = await _jsonStore.TryMigrateSidecarBackupAsync(item, ct).ConfigureAwait(false);
                        if (migrated)
                        {
                            Interlocked.Increment(ref migratedCount);
                            Common.LogHelper.Info(_logger, $"Migrated {item.Name} backup to: {filePath}");
                            return true;
                        }

                        Common.LogHelper.Warn(_logger, $"Could not migrate {item.Name}; preserving sidecar backup: {sidecarPath}");
                        return false;
                    }

                    if (File.Exists(filePath) &&
                        (!Plugin.GetSafeConfiguration().EnableMusicMetadata ||
                         item.IsLocked ||
                         !AudioMetadataHelper.IsMusicLibraryAudio(item, _libraryManager) ||
                         HasProbedAudioTagsInJson(filePath)))
                    {
                        Interlocked.Increment(ref skipped);
                        Common.LogHelper.Debug(_logger, $"Skipped {item.Name}, JSON already exists: {filePath}");
                        return null;
                    }

                    // 候选项可能仅因存在旧备份而入选；若并发创建了目标文件，不能用不完整的数据库覆盖它。
                    if (!MediaInfoHelper.HasCompleteMediaInfo(item, this, _libraryManager))
                    {
                        Interlocked.Increment(ref skipped);
                        return null;
                    }

                    var exportSucceeded = await ExportItemAsync(item, ct).ConfigureAwait(false);
                    if (exportSucceeded)
                    {
                        Common.LogHelper.Info(_logger, $"Successfully exported {item.Name} to: {filePath}");
                        return true;
                    }

                    Common.LogHelper.Warn(_logger, $"Failed to export {item.Name} to: {filePath}");
                    return false;
                },
                progress,
                cancellationToken).ConfigureAwait(false);

            Common.LogHelper.Info(
                _logger,
                $"Export completed. Skipped {skipped} existing JSON files, migrated {migratedCount} sidecar backups, " +
                $"saved {exported - migratedCount} new files.");
        }

        /// <summary>
        /// 从JSON文件恢复媒体信息
        /// </summary>
        public async Task RestoreAllAsync(IProgress<double> progress, CancellationToken cancellationToken)
        {
            var strmItems = MediaInfoHelper.GetStrmFilesNeedingRestoreWithJson(_libraryManager, this);
            Common.LogHelper.Info(_logger, $"Found {strmItems.Count} STRM files requiring restore with JSON");

            int restored = await ProcessItemsAsync(
                strmItems,
                "restore",
                async (item, ct) =>
                {
                    var ok = await RestoreItemAsync(item, ct).ConfigureAwait(false);
                    if (!ok)
                    {
                        Common.LogHelper.Warn(_logger, $"Failed to restore {item.Name} from JSON");
                    }

                    return ok;
                },
                progress,
                cancellationToken).ConfigureAwait(false);

            Common.LogHelper.Info(_logger, $"Restore operation completed. Restored {restored}/{strmItems.Count} items.");
        }

        /// <summary>
        /// 以受限并发批量处理媒体项的公共骨架：取消检查、异常隔离、进度报告。
        /// action 返回 true 表示成功，false 表示失败，null 表示跳过。
        /// </summary>
        /// <returns>成功处理的项数</returns>
        private async Task<int> ProcessItemsAsync(
            IReadOnlyCollection<BaseItem> items,
            string operationName,
            Func<BaseItem, CancellationToken, Task<bool?>> action,
            IProgress<double> progress,
            CancellationToken cancellationToken)
        {
            int total = items.Count;
            if (total == 0)
            {
                progress?.Report(100);
                return 0;
            }

            int processed = 0;
            int succeeded = 0;
            var progressLock = new object();
            var concurrency = Math.Max(1, ProcessingConcurrencyLimiter.MaxConcurrency);

            // 导出/恢复都是纯本地操作：只限制本地并发，不占用远程探测槽，
            // 避免批量任务把新入库事件排到队尾。
            using var localLimit = new SemaphoreSlim(concurrency, concurrency);

            var tasks = items.Select(async item =>
            {
                await localLimit.WaitAsync(cancellationToken).ConfigureAwait(false);
                try
                {
                    if (cancellationToken.IsCancellationRequested)
                    {
                        return;
                    }

                    bool? result;
                    try
                    {
                        result = await action(item, cancellationToken).ConfigureAwait(false);
                    }
                    catch (OperationCanceledException)
                    {
                        throw;
                    }
                    catch (Exception ex)
                    {
                        Common.LogHelper.ErrorException(_logger, $"Error during {operationName} for {item.Name}", ex);
                        result = false;
                    }

                    if (result == true)
                    {
                        Interlocked.Increment(ref succeeded);
                    }
                }
                finally
                {
                    localLimit.Release();
                }

                // 加锁保证进度单调递增，不会倒退
                lock (progressLock)
                {
                    processed++;
                    progress?.Report(processed * 100.0 / total);
                }
            });

            try
            {
                await Task.WhenAll(tasks).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                // 记录取消进度后继续向上传播，让 Emby 正确将任务标记为已取消而非成功
                Common.LogHelper.Info(_logger, $"{operationName} operation cancelled. Processed {processed}/{total} items.");
                throw;
            }

            return succeeded;
        }

        /// <summary>
        /// 导出单个媒体项信息为 JSON 文件。
        /// </summary>
        public Task<bool> ExportItemAsync(
            BaseItem item,
            CancellationToken cancellationToken = default,
            bool audioTagsProbed = false)
        {
            return _exporter.ExportItemAsync(item, cancellationToken, audioTagsProbed);
        }

        /// <summary>
        /// 获取媒体信息 JSON 文件路径（读取时支持自定义目录缺失后回退同目录旧备份）
        /// </summary>
        public string GetMediaInfoJsonPath(BaseItem item)
        {
            return _jsonStore.GetJsonPath(item);
        }

        /// <summary>
        /// 获取媒体信息 JSON 的主写入路径（自定义目录优先，否则为 .strm 同目录）
        /// </summary>
        public string GetWriteMediaInfoJsonPath(BaseItem item)
        {
            return _jsonStore.GetWriteJsonPath(item);
        }

        /// <summary>
        /// 恢复单个媒体项信息从JSON文件
        /// </summary>
        public Task<bool> RestoreItemAsync(
            BaseItem item,
            CancellationToken cancellationToken = default)
        {
            return _restorer.RestoreItemAsync(item, cancellationToken);
        }

        internal Task<bool> TryRestoreFromSidecarIfPrimaryInvalidAsync(
            BaseItem item, CancellationToken cancellationToken)
        {
            return _restorer.TryRestoreFromSidecarIfPrimaryInvalidAsync(item, cancellationToken);
        }

        /// <summary>
        /// 当音乐库音频已有媒体流但同目录存在尚未入库的外挂歌词（.lrc / .elrc）时，纯本地合并并保存歌词流。
        /// </summary>
        public bool TryMergeLocalLyrics(BaseItem item, CancellationToken cancellationToken = default)
        {
            if (item == null)
            {
                return false;
            }

            try
            {
                var currentStreams = item.GetMediaStreams();
                if (!MediaInfoHelper.HasCompleteMediaInfo(currentStreams) ||
                    !AudioMetadataHelper.HasMissingLocalLyrics(item, currentStreams, _libraryManager))
                {
                    return false;
                }

                var mergedStreams = MediaStreamMerger.PrepareMediaStreamsForSave(
                    item, currentStreams, _libraryManager, existingStreams: currentStreams);
                _itemRepository.SaveMediaStreams(item.InternalId, mergedStreams, cancellationToken);
                Common.LogHelper.Info(_logger, $"Merged local lyrics for {item.Name}");
                return true;
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                Common.LogHelper.ErrorException(_logger, $"Error merging local lyrics for {item.Name}", ex);
                return false;
            }
        }

        /// <summary>
        /// 检查媒体项对应的 JSON 文件中是否已记录探测过的音频标签（AudioTagsProbed == true）。
        /// </summary>
        public bool HasProbedAudioTagsInJson(BaseItem item)
        {
            if (item == null)
            {
                return false;
            }

            return HasProbedAudioTagsInJson(GetMediaInfoJsonPath(item));
        }

        /// <summary>
        /// 判断音频项是否可通过本地 JSON（或目录/文件名兜底）补齐当前缺失的核心音频元数据。
        /// </summary>
        public bool CanRestoreAudioMetadataFromJson(Audio audio)
        {
            if (audio == null)
            {
                return false;
            }

            var primary = GetWriteMediaInfoJsonPath(audio);
            var selected = GetMediaInfoJsonPath(audio);
            _jsonStore.TryGetAudioMetadata(selected, includeMetadata: true, out _, out var cachedAudioMetadata);
            if (AudioMetadataHelper.CanRestoreMissingAudioMetadata(audio, cachedAudioMetadata, _libraryManager))
            {
                return true;
            }

            var sidecar = _jsonStore.GetSidecarJsonPath(audio);
            if (MediaInfoJsonStore.PathStringComparer.Equals(selected, primary) &&
                !MediaInfoJsonStore.PathStringComparer.Equals(primary, sidecar) &&
                File.Exists(primary) && File.Exists(sidecar))
            {
                // 即使损坏文件的尾部恰好包含 AudioTagsProbed，也不能因此遮蔽有效的同目录标签。
                // 仅在已有集中备份无法补全标签时读取全文，且结构校验结果按 (修改时间, 长度) 缓存，
                // 避免每次完整性检查都重新反序列化含 Base64 封面的大文件。
                if (!IsBackupStructurallyValid(primary))
                {
                    _jsonStore.TryGetAudioMetadata(sidecar, includeMetadata: true, out _, out cachedAudioMetadata);
                    return AudioMetadataHelper.CanRestoreMissingAudioMetadata(audio, cachedAudioMetadata, _libraryManager);
                }
            }

            return false;
        }

        /// <summary>
        /// 判断 JSON 备份结构是否可用（结果按文件状态缓存，避免重复全量反序列化）。
        /// </summary>
        private bool IsBackupStructurallyValid(string jsonFilePath)
        {
            var cachedValidity = _jsonStore.TryGetCachedDocumentValidity(jsonFilePath);
            if (cachedValidity.HasValue)
            {
                return cachedValidity.Value;
            }

            bool valid;
            try
            {
                var document = _jsonStore.ReadDocumentSync<List<MediaSourceWithChapters>>(jsonFilePath);
                valid = document.Found &&
                        document.Error == null &&
                        MediaInfoJsonStore.GetValidationFailure(document.Value) == null;
            }
            catch (Exception ex)
            {
                Common.LogHelper.Debug(_logger, $"Could not inspect backup '{jsonFilePath}': {ex.Message}");
                valid = false;
            }

            _jsonStore.StoreDocumentValidity(jsonFilePath, valid);
            return valid;
        }

        private bool HasProbedAudioTagsInJson(string jsonFilePath)
        {
            return _jsonStore.HasProbedAudioTags(jsonFilePath);
        }
    }
}
