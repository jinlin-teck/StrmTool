using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using MediaBrowser.Model.Entities;

namespace StrmTool
{
    public class MediaInfoCache
    {
        private readonly ILogger _logger;
        private const string CacheFileSuffix = ".strmtool.json";
        
        private static readonly JsonSerializerOptions JsonOptions = new()
        {
            Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
            WriteIndented = true,
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        };

        public MediaInfoCache(ILogger logger)
        {
            _logger = logger;
        }

        /// <summary>
        /// 获取缓存文件路径
        /// </summary>
        private static string GetCachePath(string strmPath)
        {
            if (string.IsNullOrWhiteSpace(strmPath))
                return null;
            
            var directory = Path.GetDirectoryName(strmPath);
            if (string.IsNullOrWhiteSpace(directory))
                directory = Directory.GetCurrentDirectory();
                
            var fileName = Path.GetFileNameWithoutExtension(strmPath) + CacheFileSuffix;
            return Path.Combine(directory, fileName);
        }

        /// <summary>
        /// 检查缓存文件是否存在
        /// </summary>
        public bool HasCacheFile(string strmPath)
        {
            if (string.IsNullOrWhiteSpace(strmPath))
            {
                return false;
            }

            var cachePath = GetCachePath(strmPath);
            return !string.IsNullOrWhiteSpace(cachePath) && File.Exists(cachePath);
        }

        /// <summary>
        /// 检查并读取缓存（同步版本，保持兼容性）
        /// </summary>
        public bool TryGetCachedMediaStreams(string strmPath, out List<MediaStream> mediaStreams)
        {
            mediaStreams = null;

            if (string.IsNullOrWhiteSpace(strmPath))
            {
                _logger.LogDebug("StrmTool - Invalid strm path for cache lookup");
                return false;
            }

            try
            {
                var cachePath = GetCachePath(strmPath);
                if (string.IsNullOrWhiteSpace(cachePath))
                {
                    _logger.LogDebug("StrmTool - Failed to get cache path for: {Path}", strmPath);
                    return false;
                }

                if (!File.Exists(cachePath))
                {
                    return false;
                }

                var json = File.ReadAllText(cachePath);
                var cache = JsonSerializer.Deserialize<MediaInfoCacheData>(json, JsonOptions);

                if (cache?.IsValid != true || cache.MediaStreams == null)
                {
                    return false;
                }

                mediaStreams = cache.MediaStreams;
                _logger.LogDebug("StrmTool - Loaded cached media streams from {Path}", cachePath);
                return true;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "StrmTool - Error reading cache from {Path}", strmPath);
                return false;
            }
        }

        /// <summary>
        /// 检查并读取缓存（异步版本）
        /// </summary>
        public async Task<(bool success, List<MediaStream> mediaStreams)> TryGetCachedMediaStreamsAsync(string strmPath, CancellationToken cancellationToken = default)
        {
            if (string.IsNullOrWhiteSpace(strmPath))
            {
                _logger.LogDebug("StrmTool - Invalid strm path for cache lookup");
                return (false, null);
            }

            try
            {
                var cachePath = GetCachePath(strmPath);
                if (string.IsNullOrWhiteSpace(cachePath))
                {
                    _logger.LogDebug("StrmTool - Failed to get cache path for: {Path}", strmPath);
                    return (false, null);
                }

                if (!File.Exists(cachePath))
                {
                    return (false, null);
                }

                var json = await File.ReadAllTextAsync(cachePath, cancellationToken).ConfigureAwait(false);
                var cache = JsonSerializer.Deserialize<MediaInfoCacheData>(json, JsonOptions);

                if (cache?.IsValid != true || cache.MediaStreams == null)
                {
                    return (false, null);
                }

                _logger.LogDebug("StrmTool - Loaded cached media streams from {Path}", cachePath);
                return (true, cache.MediaStreams);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "StrmTool - Error reading cache from {Path}", strmPath);
                return (false, null);
            }
        }

        /// <summary>
        /// 原子写入文件的私有方法
        /// </summary>
        private async Task WriteFileAtomicallyAsync(string filePath, string content, CancellationToken cancellationToken)
        {
            string tempPath = null;
            try
            {
                tempPath = filePath + "." + Guid.NewGuid().ToString("N") + ".tmp";
                await File.WriteAllTextAsync(tempPath, content, cancellationToken).ConfigureAwait(false);

                if (File.Exists(filePath))
                {
                    File.Replace(tempPath, filePath, null);
                }
                else
                {
                    File.Move(tempPath, filePath);
                }
            }
            catch
            {
                CleanupTempFile(tempPath);
                throw;
            }
        }

        /// <summary>
        /// 清理临时文件
        /// </summary>
        private void CleanupTempFile(string tempPath)
        {
            if (!string.IsNullOrWhiteSpace(tempPath) && File.Exists(tempPath))
            {
                try
                {
                    File.Delete(tempPath);
                }
                catch (Exception cleanupEx)
                {
                    _logger.LogWarning(cleanupEx, "StrmTool - Failed to cleanup temp cache file {Path}", tempPath);
                }
            }
        }

        /// <summary>
        /// 保存缓存（原子写入，先写临时文件再重命名）
        /// </summary>
        public async Task SaveCacheAsync(string strmPath, IEnumerable<MediaStream> mediaStreams, CancellationToken cancellationToken = default)
        {
            try
            {
                if (string.IsNullOrWhiteSpace(strmPath))
                {
                    _logger.LogDebug("StrmTool - Invalid strm path for cache save");
                    return;
                }

                var cachePath = GetCachePath(strmPath);
                if (string.IsNullOrWhiteSpace(cachePath))
                {
                    _logger.LogDebug("StrmTool - Failed to get cache path for: {Path}", strmPath);
                    return;
                }

                var cache = new MediaInfoCacheData
                {
                    Version = "1.0",
                    Timestamp = DateTime.UtcNow,
                    MediaStreams = mediaStreams.ToList(),
                    IsValid = true
                };

                var json = JsonSerializer.Serialize(cache, JsonOptions);
                await WriteFileAtomicallyAsync(cachePath, json, cancellationToken).ConfigureAwait(false);

                _logger.LogDebug("StrmTool - Saved cache to {Path}", cachePath);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "StrmTool - Error saving cache to {Path}", strmPath);
            }
        }

        /// <summary>
        /// 保存完整媒体信息缓存（包含Size等元数据）
        /// </summary>
        public async Task SaveFullCacheAsync(
            string strmPath,
            IEnumerable<MediaStream> mediaStreams,
            long size,
            long? runTimeTicks,
            string container,
            int totalBitrate,
            int width,
            int height,
            CancellationToken cancellationToken = default)
        {
            try
            {
                if (string.IsNullOrWhiteSpace(strmPath))
                {
                    _logger.LogDebug("StrmTool - Invalid strm path for full cache save");
                    return;
                }

                var cachePath = GetCachePath(strmPath);
                if (string.IsNullOrWhiteSpace(cachePath))
                {
                    _logger.LogDebug("StrmTool - Failed to get cache path for: {Path}", strmPath);
                    return;
                }

                var cache = new MediaInfoCacheData
                {
                    Version = "1.0",
                    Timestamp = DateTime.UtcNow,
                    MediaStreams = mediaStreams?.ToList() ?? new List<MediaStream>(),
                    IsValid = true,
                    Size = size,
                    RunTimeTicks = runTimeTicks,
                    Container = container,
                    TotalBitrate = totalBitrate,
                    Width = width,
                    Height = height
                };

                var json = JsonSerializer.Serialize(cache, JsonOptions);
                await WriteFileAtomicallyAsync(cachePath, json, cancellationToken).ConfigureAwait(false);

                _logger.LogDebug("StrmTool - Saved full cache (Size={Size}) to {Path}", size, cachePath);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "StrmTool - Error saving full cache to {Path}", strmPath);
            }
        }

        /// <summary>
        /// 尝试读取完整缓存数据（包含元数据）
        /// </summary>
        public bool TryGetFullCache(string strmPath, out MediaInfoCacheData cacheData)
        {
            cacheData = null;

            if (string.IsNullOrWhiteSpace(strmPath))
            {
                _logger.LogDebug("StrmTool - Invalid strm path for full cache lookup");
                return false;
            }

            try
            {
                var cachePath = GetCachePath(strmPath);
                if (string.IsNullOrWhiteSpace(cachePath))
                {
                    _logger.LogDebug("StrmTool - Failed to get cache path for: {Path}", strmPath);
                    return false;
                }

                if (!File.Exists(cachePath))
                {
                    return false;
                }

                var json = File.ReadAllText(cachePath);
                var cache = JsonSerializer.Deserialize<MediaInfoCacheData>(json, JsonOptions);

                if (cache?.IsValid != true)
                {
                    return false;
                }

                cacheData = cache;
                _logger.LogDebug("StrmTool - Loaded full cache (Size={Size}) from {Path}", cache.Size, cachePath);
                return true;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "StrmTool - Error reading full cache from {Path}", strmPath);
                return false;
            }
        }

        /// <summary>
        /// 清除缓存
        /// </summary>
        public void ClearCache(string strmPath)
        {
            try
            {
                if (string.IsNullOrWhiteSpace(strmPath))
                {
                    _logger.LogDebug("StrmTool - Invalid strm path for cache clear");
                    return;
                }

                var cachePath = GetCachePath(strmPath);
                if (string.IsNullOrWhiteSpace(cachePath))
                {
                    _logger.LogDebug("StrmTool - Failed to get cache path for: {Path}", strmPath);
                    return;
                }

                if (File.Exists(cachePath))
                {
                    File.Delete(cachePath);
                    _logger.LogInformation("StrmTool - Cleared cache: {Path}", cachePath);
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "StrmTool - Error clearing cache: {Path}", strmPath);
            }
        }

        /// <summary>
        /// 清除目录下所有缓存
        /// </summary>
        public void ClearAllCaches(string directoryPath)
        {
            try
            {
                if (!Directory.Exists(directoryPath))
                    return;

                var jsonFiles = Directory.GetFiles(directoryPath, "*" + CacheFileSuffix, SearchOption.AllDirectories);
                int cleared = 0;

                foreach (var jsonFile in jsonFiles)
                {
                    var fileName = Path.GetFileName(jsonFile);
                    if (string.IsNullOrWhiteSpace(fileName) || !fileName.EndsWith(CacheFileSuffix, StringComparison.OrdinalIgnoreCase))
                    {
                        continue;
                    }

                    var baseName = fileName.Remove(fileName.Length - CacheFileSuffix.Length);
                    var strmPath = Path.Combine(Path.GetDirectoryName(jsonFile) ?? string.Empty, baseName + ".strm");

                    if (File.Exists(strmPath))
                    {
                        try
                        {
                            File.Delete(jsonFile);
                            cleared++;
                        }
                        catch (Exception ex)
                        {
                            _logger.LogWarning(ex, "StrmTool - Error deleting cache file {Path}", jsonFile);
                        }
                    }
                }

                _logger.LogInformation("StrmTool - Cleared {Count} cache files in {Dir}", cleared, directoryPath);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "StrmTool - Error clearing caches in {Dir}", directoryPath);
            }
        }
    }

    /// <summary>
    /// 缓存数据结构
    /// </summary>
    public class MediaInfoCacheData
    {
        [JsonPropertyName("version")]
        public string Version { get; set; }

        [JsonPropertyName("timestamp")]
        public DateTime Timestamp { get; set; }

        [JsonPropertyName("mediaStreams")]
        public List<MediaStream> MediaStreams { get; set; }

        [JsonPropertyName("isValid")]
        public bool IsValid { get; set; }

        [JsonPropertyName("size")]
        public long Size { get; set; }

        [JsonPropertyName("runTimeTicks")]
        public long? RunTimeTicks { get; set; }

        [JsonPropertyName("container")]
        public string Container { get; set; }

        [JsonPropertyName("totalBitrate")]
        public int TotalBitrate { get; set; }

        [JsonPropertyName("width")]
        public int Width { get; set; }

        [JsonPropertyName("height")]
        public int Height { get; set; }
    }
}
