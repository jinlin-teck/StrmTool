using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.Logging;
using MediaBrowser.Model.Serialization;

namespace StrmTool.Common
{
    /// <summary>
    /// -mediainfo.json 的单一职责存储层：
    /// 路径推导、原子写入、尾部轻量解析、按 (修改时间, 长度) 的缓存，以及坏文件隔离。
    /// </summary>
    public class MediaInfoJsonStore
    {
        /// <summary>
        /// 一次读取的结果，供恢复流程在锁外校验、并在隔离前核对文件是否已被改写
        /// </summary>
        public sealed class JsonReadResult
        {
            public string? Text { get; set; }
            public long Length { get; set; }
            public long LastWriteTicks { get; set; }
        }

        /// <summary>
        /// 整份 JSON 文档的读取/反序列化结果
        /// </summary>
        public sealed class JsonDocumentReadResult<T> where T : class
        {
            public T? Value { get; set; }
            public JsonReadResult? State { get; set; }
            public Exception? Error { get; set; }
            public bool Found => State != null;
        }

        private sealed class JsonAudioTagCacheEntry
        {
            public long LastWriteTicks { get; set; }
            public long Length { get; set; }
            public bool HasProbedAudioTags { get; set; }
            public AudioMetadataInfo? AudioMetadata { get; set; }
        }

        private const string AudioMetadataKey = "\"AudioMetadata\"";
        private const int TailReadBufferBytes = 16384;
        private const int MaxCacheEntries = 32768;
        private const int EvictBatchSize = MaxCacheEntries / 4;
        private const int PathLockStripes = 64;
        private const int WriteLockStripes = 256;

        private static readonly ConcurrentDictionary<string, JsonAudioTagCacheEntry> AudioTagCache =
            new ConcurrentDictionary<string, JsonAudioTagCacheEntry>(BackupPathResolver.PathComparer);

        /// <summary>
        /// 结构校验结果缓存：按 (修改时间, 长度) 失效，避免完整性检查反复反序列化大文件
        /// </summary>
        private sealed class DocumentValidityCacheEntry
        {
            public long LastWriteTicks { get; set; }
            public long Length { get; set; }
            public bool Valid { get; set; }
        }

        private const int MaxDocumentValidityEntries = 8192;

        private static readonly ConcurrentDictionary<string, DocumentValidityCacheEntry> DocumentValidityCache =
            new ConcurrentDictionary<string, DocumentValidityCacheEntry>(BackupPathResolver.PathComparer);

        private static readonly object CacheEvictLock = new object();
        private static int _approxCacheCount;

        private static readonly SemaphoreSlim[] PathLocks = CreateLockStripes(PathLockStripes);
        private static readonly SemaphoreSlim[] WriteCoordinationLocks = CreateLockStripes(WriteLockStripes);

        internal static StringComparer PathStringComparer => BackupPathResolver.PathComparer;

        internal static SemaphoreSlim GetPathLockForPath(string path)
        {
            return GetPathLock(path);
        }

        /// <summary>
        /// 协调同一写入路径的迁移与导出；须先取得此锁，再取得存储层路径锁。
        /// </summary>
        internal static SemaphoreSlim GetWriteCoordinationLock(string path)
        {
            int hash = BackupPathResolver.PathComparer.GetHashCode(path) & int.MaxValue;
            return WriteCoordinationLocks[hash % WriteCoordinationLocks.Length];
        }

        /// <summary>
        /// 去除路径末尾的目录分隔符，但保留文件系统根路径自身的分隔符（如 Unix "/" 或 Windows "C:\"）
        /// </summary>
        internal static string TrimEndingDirectorySeparatorPreservingRoot(string path)
        {
            return BackupPathResolver.TrimEndingDirectorySeparatorPreservingRoot(path);
        }

        private readonly ILogger _logger;
        private readonly IJsonSerializer _jsonSerializer;

        public MediaInfoJsonStore(ILogger logger, IJsonSerializer jsonSerializer)
        {
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
            _jsonSerializer = jsonSerializer ?? throw new ArgumentNullException(nameof(jsonSerializer));
        }

        /// <summary>
        /// 插件卸载时清理进程级缓存，避免跨插件生命周期持有媒体元数据
        /// </summary>
        public static void ClearCache()
        {
            AudioTagCache.Clear();
            DocumentValidityCache.Clear();
            Interlocked.Exchange(ref _approxCacheCount, 0);
            BackupPathResolver.ClearCache();
        }

        /// <summary>
        /// 校验备份文档结构是否可用于恢复（必须包含媒体源及至少一条音/视频流）。
        /// </summary>
        internal static string? GetValidationFailure(List<MediaSourceWithChapters>? mediaSourcesWithChapters)
        {
            if (mediaSourcesWithChapters == null)
            {
                return "deserialized value is null";
            }

            if (mediaSourcesWithChapters.Count == 0)
            {
                return "media source list is empty";
            }

            var first = mediaSourcesWithChapters[0];
            if (first?.MediaSourceInfo == null)
            {
                return "first media source has no MediaSourceInfo";
            }

            var streams = first.MediaSourceInfo.MediaStreams;
            if (streams == null ||
                !streams.Any(stream =>
                    stream != null &&
                    (stream.Type == MediaStreamType.Video || stream.Type == MediaStreamType.Audio)))
            {
                return "MediaSourceInfo.MediaStreams has no video or audio stream";
            }

            return null;
        }

        /// <summary>
        /// 获取媒体信息 JSON 的读取/判存路径：
        /// 若启用了自定义集中备份目录，优先使用集中目录路径；若集中目录尚无文件而原同目录存在历史备份，则兼容回退复用旧备份。
        /// </summary>
        public string GetJsonPath(BaseItem item, string? customBackupDirectory = null)
        {
            return BackupPathResolver.GetJsonPath(item, customBackupDirectory, _logger);
        }

        /// <summary>
        /// 获取媒体信息 JSON 的主写入路径（启用合法的自定义集中目录时返回镜像路径，否则返回 .strm 同目录路径）
        /// </summary>
        public string GetWriteJsonPath(BaseItem item, string? customBackupDirectory = null)
        {
            return BackupPathResolver.GetWriteJsonPath(item, customBackupDirectory, _logger);
        }

        /// <summary>
        /// 获取经典的 .strm 同目录 JSON 备份路径
        /// </summary>
        public string GetSidecarJsonPath(BaseItem item)
        {
            return BackupPathResolver.GetSidecarJsonPath(item);
        }

        /// <summary>
        /// 获取音频内嵌封面的 .metadata 目录路径（启用合法的自定义集中目录时返回镜像目录下的 .metadata，否则返回 .strm 同目录 .metadata）
        /// </summary>
        public string GetAudioMetadataDirectory(BaseItem item, string? customBackupDirectory = null)
        {
            return BackupPathResolver.GetAudioMetadataDirectory(item, customBackupDirectory, _logger);
        }

        /// <summary>
        /// 获取 .strm 同目录下的经典 .metadata 目录路径
        /// </summary>
        public string GetSidecarAudioMetadataDirectory(BaseItem item)
        {
            return BackupPathResolver.GetSidecarAudioMetadataDirectory(item);
        }

        /// <summary>
        /// 在路径级互斥下原子写入 JSON，并同步更新音频标签缓存
        /// </summary>
        public async Task WriteAsync(
            string jsonFilePath,
            List<MediaSourceWithChapters> mediaSources,
            CancellationToken cancellationToken)
        {
            var directory = Path.GetDirectoryName(jsonFilePath);
            if (!string.IsNullOrEmpty(directory))
            {
                Directory.CreateDirectory(directory);
            }

            var json = _jsonSerializer.SerializeToString(mediaSources);
            var gate = GetPathLock(jsonFilePath);
            await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                await AtomicFile.WriteAllTextAsync(jsonFilePath, json, cancellationToken, reuseTempFile: true).ConfigureAwait(false);
                UpdateCache(jsonFilePath, mediaSources.FirstOrDefault()?.AudioMetadata);
            }
            finally
            {
                gate.Release();
            }
        }

        /// <summary>
        /// 将有效的同目录历史备份原样迁移至集中目录；已有目标文件不会被覆盖。
        /// </summary>
        public async Task<bool> TryMigrateSidecarBackupAsync(BaseItem item, CancellationToken cancellationToken)
        {
            var customBackupDirectory = Plugin.GetSafeConfiguration().CustomBackupDirectory;
            var destination = GetWriteJsonPath(item, customBackupDirectory);
            var source = GetSidecarJsonPath(item);
            if (PathStringComparer.Equals(source, destination) || !File.Exists(source))
            {
                return false;
            }

            // 先读源文件并校验：源读取只占用源路径锁，不阻塞目标路径的导出。
            // 目标是否已存在会在取得导出锁后再次确认，因此提前读取不会造成覆盖。
            var document = await ReadDocumentAsync<List<MediaSourceWithChapters>>(source, cancellationToken)
                .ConfigureAwait(false);
            if (!document.Found || document.Error != null ||
                GetValidationFailure(document.Value) != null)
            {
                Common.LogHelper.Warn(_logger, $"Cannot migrate invalid or unavailable sidecar backup: {source}");
                return false;
            }

            var exportGate = GetWriteCoordinationLock(destination);
            await exportGate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                if (File.Exists(destination))
                {
                    return true;
                }

                var gate = GetPathLock(destination);
                await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
                try
                {
                    if (File.Exists(destination))
                    {
                        return true;
                    }

                    var directory = Path.GetDirectoryName(destination);
                    if (!string.IsNullOrEmpty(directory))
                    {
                        Directory.CreateDirectory(directory);
                    }

                    await AtomicFile.WriteAllTextAsync(destination, document.State!.Text!, cancellationToken, reuseTempFile: true)
                        .ConfigureAwait(false);
                    UpdateCache(destination, document.Value![0].AudioMetadata);
                    return true;
                }
                finally
                {
                    gate.Release();
                }
            }
            finally
            {
                exportGate.Release();
            }
        }

        /// <summary>
        /// 在路径级互斥下读取并反序列化整份 JSON 文档。
        /// 读取、反序列化与文件状态采集都由存储层统一完成，恢复方只负责语义校验。
        /// </summary>
        public async Task<JsonDocumentReadResult<T>> ReadDocumentAsync<T>(
            string jsonFilePath, CancellationToken cancellationToken)
            where T : class
        {
            var result = new JsonDocumentReadResult<T>();
            var read = await ReadAsync(jsonFilePath, cancellationToken).ConfigureAwait(false);
            if (read?.Text == null)
            {
                return result;
            }

            result.State = read;
            try
            {
                result.Value = _jsonSerializer.DeserializeFromString<T>(read.Text);
            }
            catch (Exception ex)
            {
                result.Error = ex;
            }

            return result;
        }

        /// <summary>
        /// 同步读取并反序列化整份 JSON 文档，供必须在同步上下文（如完整性检查）中调用的场景使用，
        /// 避免 `.GetAwaiter().GetResult()` 这种同步等待异步的状态机开销。
        /// </summary>
        internal JsonDocumentReadResult<T> ReadDocumentSync<T>(string jsonFilePath)
            where T : class
        {
            var result = new JsonDocumentReadResult<T>();
            var gate = GetPathLock(jsonFilePath);
            gate.Wait();
            try
            {
                var fileInfo = new FileInfo(jsonFilePath);
                if (!fileInfo.Exists)
                {
                    return result;
                }

                result.State = new JsonReadResult
                {
                    Text = File.ReadAllText(jsonFilePath),
                    Length = fileInfo.Length,
                    LastWriteTicks = fileInfo.LastWriteTimeUtc.Ticks
                };
            }
            finally
            {
                gate.Release();
            }

            try
            {
                result.Value = _jsonSerializer.DeserializeFromString<T>(result.State.Text!);
            }
            catch (Exception ex)
            {
                result.Error = ex;
            }

            return result;
        }

        /// <summary>
        /// 获取已缓存的结构校验结论；文件已被改写或未缓存时返回 null
        /// </summary>
        internal bool? TryGetCachedDocumentValidity(string jsonFilePath)
        {
            try
            {
                var fileInfo = new FileInfo(jsonFilePath);
                if (!fileInfo.Exists)
                {
                    DocumentValidityCache.TryRemove(jsonFilePath, out _);
                    return null;
                }

                if (DocumentValidityCache.TryGetValue(jsonFilePath, out var cached) &&
                    cached.LastWriteTicks == fileInfo.LastWriteTimeUtc.Ticks &&
                    cached.Length == fileInfo.Length)
                {
                    return cached.Valid;
                }
            }
            catch (Exception ex)
            {
                Common.LogHelper.Debug(_logger, $"Failed to inspect backup validity cache for '{jsonFilePath}': {ex.Message}");
            }

            return null;
        }

        /// <summary>
        /// 记录结构校验结论，供后续完整性检查直接复用
        /// </summary>
        internal void StoreDocumentValidity(string jsonFilePath, bool valid)
        {
            try
            {
                var fileInfo = new FileInfo(jsonFilePath);
                if (!fileInfo.Exists)
                {
                    DocumentValidityCache.TryRemove(jsonFilePath, out _);
                    return;
                }

                if (DocumentValidityCache.Count >= MaxDocumentValidityEntries)
                {
                    DocumentValidityCache.Clear();
                }

                DocumentValidityCache[jsonFilePath] = new DocumentValidityCacheEntry
                {
                    LastWriteTicks = fileInfo.LastWriteTimeUtc.Ticks,
                    Length = fileInfo.Length,
                    Valid = valid
                };
            }
            catch (Exception ex)
            {
                Common.LogHelper.Debug(_logger, $"Failed to cache backup validity for '{jsonFilePath}': {ex.Message}");
            }
        }

        /// <summary>
        /// 在路径级互斥下读取 JSON 全文，并返回用于隔离判定的文件状态
        /// </summary>
        private async Task<JsonReadResult?> ReadAsync(string jsonFilePath, CancellationToken cancellationToken)
        {
            var gate = GetPathLock(jsonFilePath);
            await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                var fileInfo = new FileInfo(jsonFilePath);
                if (!fileInfo.Exists)
                {
                    return null;
                }

                long length = fileInfo.Length;
                long lastWriteTicks = fileInfo.LastWriteTimeUtc.Ticks;
                var text = await File.ReadAllTextAsync(jsonFilePath, cancellationToken).ConfigureAwait(false);
                return new JsonReadResult
                {
                    Text = text,
                    Length = length,
                    LastWriteTicks = lastWriteTicks
                };
            }
            finally
            {
                gate.Release();
            }
        }

        /// <summary>
        /// 把无法解析的备份文件隔离为 .bak（保留上一份 .bak.1，最多两代），
        /// 并在隔离前核对文件状态，避免误伤刚被重新写入的合法文件。
        /// </summary>
        public async Task<bool> TryQuarantineAsync(
            string jsonFilePath, long expectedLength, long expectedLastWriteTicks, string reason)
        {
            var gate = GetPathLock(jsonFilePath);
            await gate.WaitAsync().ConfigureAwait(false);
            try
            {
                var fileInfo = new FileInfo(jsonFilePath);
                if (!fileInfo.Exists)
                {
                    return false;
                }

                if (expectedLength >= 0 &&
                    (fileInfo.Length != expectedLength || fileInfo.LastWriteTimeUtc.Ticks != expectedLastWriteTicks))
                {
                    Common.LogHelper.Debug(
                        _logger,
                        $"Media info JSON changed during validation, skip quarantine: {jsonFilePath}");
                    return false;
                }

                var backupPath = $"{jsonFilePath}.bak";
                var previousBackupPath = $"{jsonFilePath}.bak.1";
                if (File.Exists(backupPath))
                {
                    if (File.Exists(previousBackupPath))
                    {
                        File.Delete(previousBackupPath);
                    }

                    File.Move(backupPath, previousBackupPath);
                }

                File.Move(jsonFilePath, backupPath);
                RemoveCacheEntry(jsonFilePath);
                Common.LogHelper.Warn(
                    _logger,
                    $"Invalid media info JSON ({reason}) moved to: {backupPath}");
                return true;
            }
            catch (Exception ex)
            {
                Common.LogHelper.Warn(
                    _logger,
                    $"Could not quarantine invalid media info JSON '{jsonFilePath}': {ex.Message}");
                return false;
            }
            finally
            {
                gate.Release();
            }
        }

        /// <summary>
        /// 检查媒体项对应的 JSON 文件中是否已记录探测过的音频标签（AudioTagsProbed == true）
        /// </summary>
        public bool HasProbedAudioTags(string jsonFilePath)
        {
            return TryGetAudioMetadata(jsonFilePath, includeMetadata: false, out var hasProbed, out _) && hasProbed;
        }

        /// <summary>
        /// 读取 JSON 中的音频元数据，使用 (修改时间, 长度) 缓存与尾部轻量解析，
        /// 避免重复全量读取和反序列化 Base64 封面。
        /// </summary>
        public bool TryGetAudioMetadata(
            string jsonFilePath,
            bool includeMetadata,
            out bool hasProbedAudioTags,
            out AudioMetadataInfo? audioMetadata)
        {
            hasProbedAudioTags = false;
            audioMetadata = null;

            try
            {
                var fileInfo = new FileInfo(jsonFilePath);
                if (!fileInfo.Exists)
                {
                    RemoveCacheEntry(jsonFilePath);
                    return false;
                }

                long ticks = fileInfo.LastWriteTimeUtc.Ticks;
                long length = fileInfo.Length;

                if (AudioTagCache.TryGetValue(jsonFilePath, out var cached) &&
                    cached.LastWriteTicks == ticks &&
                    cached.Length == length)
                {
                    hasProbedAudioTags = cached.HasProbedAudioTags;
                    audioMetadata = includeMetadata ? cached.AudioMetadata : null;
                    return true;
                }

                var parsed = ReadAudioMetadata(jsonFilePath, length);
                hasProbedAudioTags = parsed?.AudioTagsProbed == true;
                audioMetadata = includeMetadata ? parsed : null;

                StoreCacheEntry(jsonFilePath, new JsonAudioTagCacheEntry
                {
                    LastWriteTicks = ticks,
                    Length = length,
                    HasProbedAudioTags = hasProbedAudioTags,
                    AudioMetadata = parsed
                });
                return true;
            }
            catch (Exception ex)
            {
                Common.LogHelper.Debug(
                    _logger,
                    $"Failed to inspect media info JSON '{jsonFilePath}': {ex.Message}");
                return false;
            }
        }

        /// <summary>
        /// 导出/恢复后刷新缓存，避免下一次检查重复读取
        /// </summary>
        public void UpdateCache(string jsonFilePath, AudioMetadataInfo? audioMetadata)
        {
            try
            {
                var fileInfo = new FileInfo(jsonFilePath);
                if (!fileInfo.Exists)
                {
                    RemoveCacheEntry(jsonFilePath);
                    return;
                }

                DocumentValidityCache.TryRemove(jsonFilePath, out _);
                StoreCacheEntry(jsonFilePath, new JsonAudioTagCacheEntry
                {
                    LastWriteTicks = fileInfo.LastWriteTimeUtc.Ticks,
                    Length = fileInfo.Length,
                    HasProbedAudioTags = audioMetadata?.AudioTagsProbed == true,
                    AudioMetadata = audioMetadata
                });
            }
            catch (Exception ex)
            {
                Common.LogHelper.Debug(
                    _logger,
                    $"Failed to update audio tag cache for '{jsonFilePath}': {ex.Message}");
            }
        }

        private AudioMetadataInfo? ReadAudioMetadata(string jsonFilePath, long fileLength)
        {
            string? objectJson;

            if (fileLength <= TailReadBufferBytes)
            {
                TryExtractJsonObjectOrNullAfterKey(File.ReadAllText(jsonFilePath), AudioMetadataKey, out objectJson);
            }
            else
            {
                var tailText = ReadFileTailText(jsonFilePath, fileLength, TailReadBufferBytes);
                if (!TryExtractJsonObjectOrNullAfterKey(tailText, AudioMetadataKey, out objectJson))
                {
                    // 尾部窗口未命中（如旧版无该字段、JSON 字段顺序被重排到大封面之前、或对象跨越窗口边界），
                    // 统一回退到全文解析；解析结果（含 null）随后会按 (LastWriteTicks, Length) 持久缓存。
                    TryExtractJsonObjectOrNullAfterKey(File.ReadAllText(jsonFilePath), AudioMetadataKey, out objectJson);
                }
            }

            if (string.IsNullOrEmpty(objectJson))
            {
                return null;
            }

            try
            {
                return _jsonSerializer.DeserializeFromString<AudioMetadataInfo>(objectJson);
            }
            catch (Exception ex)
            {
                Common.LogHelper.Debug(
                    _logger,
                    $"Failed to parse AudioMetadata from '{jsonFilePath}': {ex.Message}");
                return null;
            }
        }

        private static void StoreCacheEntry(string jsonFilePath, JsonAudioTagCacheEntry entry)
        {
            bool added = false;
            AudioTagCache.AddOrUpdate(
                jsonFilePath,
                _ =>
                {
                    added = true;
                    return entry;
                },
                (_, __) => entry);

            if (!added)
            {
                return;
            }

            if (Interlocked.Increment(ref _approxCacheCount) <= MaxCacheEntries)
            {
                return;
            }

            lock (CacheEvictLock)
            {
                if (Volatile.Read(ref _approxCacheCount) <= MaxCacheEntries)
                {
                    return;
                }

                int removed = 0;
                foreach (var key in AudioTagCache.Keys)
                {
                    if (AudioTagCache.TryRemove(key, out _))
                    {
                        removed++;
                        if (removed >= EvictBatchSize)
                        {
                            break;
                        }
                    }
                }

                Interlocked.Add(ref _approxCacheCount, -removed);
            }
        }

        private static void RemoveCacheEntry(string jsonFilePath)
        {
            DocumentValidityCache.TryRemove(jsonFilePath, out _);
            if (AudioTagCache.TryRemove(jsonFilePath, out _))
            {
                Interlocked.Decrement(ref _approxCacheCount);
            }
        }

        private static SemaphoreSlim[] CreateLockStripes(int count)
        {
            var locks = new SemaphoreSlim[count];
            for (int i = 0; i < locks.Length; i++)
            {
                locks[i] = new SemaphoreSlim(1, 1);
            }

            return locks;
        }

        private static SemaphoreSlim GetPathLock(string path)
        {
            int hash = BackupPathResolver.PathComparer.GetHashCode(path) & int.MaxValue;
            return PathLocks[hash % PathLockStripes];
        }

        private static string ReadFileTailText(string filePath, long fileLength, int maxBytes)
        {
            if (fileLength <= maxBytes)
            {
                return File.ReadAllText(filePath);
            }

            using var fs = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            fs.Seek(-maxBytes, SeekOrigin.End);
            var buffer = new byte[maxBytes];
            int totalRead = 0;
            while (totalRead < maxBytes)
            {
                int read = fs.Read(buffer, totalRead, maxBytes - totalRead);
                if (read <= 0)
                {
                    break;
                }

                totalRead += read;
            }

            return Encoding.UTF8.GetString(buffer, 0, totalRead);
        }

        private static bool TryExtractJsonObjectOrNullAfterKey(string text, string keyToken, out string? objectJson)
        {
            objectJson = null;
            int keyIndex = text.LastIndexOf(keyToken, StringComparison.Ordinal);
            if (keyIndex < 0)
            {
                return false;
            }

            int colonIndex = text.IndexOf(':', keyIndex + keyToken.Length);
            if (colonIndex < 0)
            {
                return false;
            }

            int valueIndex = -1;
            for (int i = colonIndex + 1; i < text.Length; i++)
            {
                if (!char.IsWhiteSpace(text[i]))
                {
                    valueIndex = i;
                    break;
                }
            }

            if (valueIndex < 0)
            {
                return false;
            }

            // 明确匹配 "AudioMetadata": null
            if (text[valueIndex] == 'n' &&
                valueIndex + 4 <= text.Length &&
                string.CompareOrdinal(text, valueIndex, "null", 0, 4) == 0)
            {
                return true;
            }

            if (text[valueIndex] != '{')
            {
                return false;
            }

            int startBrace = valueIndex;
            int depth = 0;
            bool inString = false;
            bool escaped = false;

            for (int i = startBrace; i < text.Length; i++)
            {
                char c = text[i];
                if (inString)
                {
                    if (escaped)
                    {
                        escaped = false;
                    }
                    else if (c == '\\')
                    {
                        escaped = true;
                    }
                    else if (c == '"')
                    {
                        inString = false;
                    }

                    continue;
                }

                if (c == '"')
                {
                    inString = true;
                }
                else if (c == '{')
                {
                    depth++;
                }
                else if (c == '}')
                {
                    depth--;
                    if (depth == 0)
                    {
                        objectJson = text.Substring(startBrace, i - startBrace + 1);
                        return true;
                    }
                }
            }

            return false;
        }

        /// <summary>
        /// 使文件名安全，替换无效字符（单遍扫描）
        /// </summary>
        internal static string MakeSafeFilename(string? name)
        {
            return BackupPathResolver.MakeSafeFilename(name);
        }
    }
}
