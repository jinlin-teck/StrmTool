using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using MediaBrowser.Controller.Entities;
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

        private static readonly StringComparer PathComparer = RuntimeInformation.IsOSPlatform(OSPlatform.Windows)
            ? StringComparer.OrdinalIgnoreCase
            : StringComparer.Ordinal;

        private static readonly ConcurrentDictionary<string, JsonAudioTagCacheEntry> AudioTagCache =
            new ConcurrentDictionary<string, JsonAudioTagCacheEntry>(PathComparer);

        private static readonly object CacheEvictLock = new object();
        private static int _approxCacheCount;

        private static readonly SemaphoreSlim[] PathLocks = CreatePathLocks();

        internal static StringComparer PathStringComparer => PathComparer;

        internal static SemaphoreSlim GetPathLockForPath(string path)
        {
            return GetPathLock(path);
        }

        /// <summary>
        /// 去除路径末尾的目录分隔符，但保留文件系统根路径自身的分隔符（如 Unix "/" 或 Windows "C:\"）
        /// </summary>
        internal static string TrimEndingDirectorySeparatorPreservingRoot(string path)
        {
            if (string.IsNullOrEmpty(path))
            {
                return path;
            }

            var root = Path.GetPathRoot(path);
            if (!string.IsNullOrEmpty(root) &&
                path.Length <= root.Length &&
                string.Equals(path, root, StringComparison.OrdinalIgnoreCase))
            {
                return path;
            }

            var trimmed = path.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            if (trimmed.Length == 0)
            {
                return Path.DirectorySeparatorChar.ToString();
            }

            if (!string.IsNullOrEmpty(root) && trimmed.Length < root.Length)
            {
                return root;
            }

            return trimmed;
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
            Interlocked.Exchange(ref _approxCacheCount, 0);
        }

        /// <summary>
        /// 获取媒体信息JSON文件路径，直接保存在strm文件所在目录
        /// </summary>
        public string GetJsonPath(BaseItem item)
        {
            if (item == null)
            {
                throw new ArgumentNullException(nameof(item));
            }

            var mediaDirectory = Path.GetDirectoryName(item.Path);
            if (string.IsNullOrEmpty(mediaDirectory))
            {
                string safeName = MakeSafeFilename(item.Name);
                var tempPath = Path.Combine(Path.GetTempPath(), "StrmTool");
                var suffix = item.InternalId > 0 ? $"_{item.InternalId}" : string.Empty;
                return Path.Combine(tempPath, $"{safeName}{suffix}{CommonConfiguration.MediaInfoFileExtension}");
            }

            var mediaFileName = Path.GetFileNameWithoutExtension(item.Path);
            if (string.IsNullOrEmpty(mediaFileName))
            {
                mediaFileName = MakeSafeFilename(item.Name);
            }

            return Path.Combine(mediaDirectory, $"{mediaFileName}{CommonConfiguration.MediaInfoFileExtension}");
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
            if (AudioTagCache.TryRemove(jsonFilePath, out _))
            {
                Interlocked.Decrement(ref _approxCacheCount);
            }
        }

        private static SemaphoreSlim[] CreatePathLocks()
        {
            var locks = new SemaphoreSlim[PathLockStripes];
            for (int i = 0; i < locks.Length; i++)
            {
                locks[i] = new SemaphoreSlim(1, 1);
            }

            return locks;
        }

        private static SemaphoreSlim GetPathLock(string path)
        {
            int hash = PathComparer.GetHashCode(path) & int.MaxValue;
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
            if (string.IsNullOrEmpty(name))
            {
                return "strmtool";
            }

            var invalidChars = new HashSet<char>(Path.GetInvalidFileNameChars());
            var sb = new StringBuilder(name.Length);
            foreach (char c in name)
            {
                sb.Append(invalidChars.Contains(c) ? '_' : c);
            }

            return sb.ToString();
        }
    }
}
