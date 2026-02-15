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
        /// 检查并读取缓存
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
        /// 保存缓存（原子写入，先写临时文件再重命名）
        /// </summary>
        public async Task SaveCacheAsync(string strmPath, IEnumerable<MediaStream> mediaStreams, CancellationToken cancellationToken = default)
        {
            string tempPath = null;
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

                // 原子写入：先写临时文件，成功后替换目标文件
                tempPath = cachePath + "." + Guid.NewGuid().ToString("N") + ".tmp";
                await File.WriteAllTextAsync(tempPath, json, cancellationToken).ConfigureAwait(false);
                if (File.Exists(cachePath))
                {
                    File.Replace(tempPath, cachePath, null);
                }
                else
                {
                    File.Move(tempPath, cachePath);
                }

                _logger.LogDebug("StrmTool - Saved cache to {Path}", cachePath);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "StrmTool - Error saving cache to {Path}", strmPath);
                if (!string.IsNullOrWhiteSpace(tempPath) && File.Exists(tempPath))
                {
                    try
                    {
                        File.Delete(tempPath);
                    }
                    catch (Exception cleanupEx)
                    {
                        _logger.LogDebug(cleanupEx, "StrmTool - Failed to cleanup temp cache file {Path}", tempPath);
                    }
                }
                // 不抛出异常，缓存失败不应中断主流程
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

                    var baseName = fileName.Substring(0, fileName.Length - CacheFileSuffix.Length);
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
    }
}
