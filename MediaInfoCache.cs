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

            // 路径遍历防护：检查非法字符和路径遍历模式
            if (ContainsPathTraversal(strmPath))
            {
                return null;
            }

            // 确保路径是绝对路径
            if (!Path.IsPathRooted(strmPath))
            {
                return null;
            }

            var directory = Path.GetDirectoryName(strmPath);
            if (string.IsNullOrWhiteSpace(directory))
                return null;

            // 确保目录存在且合法
            if (!Directory.Exists(directory))
            {
                return null;
            }

            var fileName = Path.GetFileNameWithoutExtension(strmPath) + CacheFileSuffix;
            var cachePath = Path.Combine(directory, fileName);

            // 最终验证生成的缓存路径是否仍在合法目录下
            var cacheDirectory = Path.GetDirectoryName(cachePath);
            if (!string.Equals(directory, cacheDirectory, StringComparison.OrdinalIgnoreCase))
            {
                return null;
            }

            return cachePath;
        }

        /// <summary>
        /// 检查路径是否包含路径遍历攻击模式
        /// </summary>
        public static bool ContainsPathTraversal(string path)
        {
            if (string.IsNullOrEmpty(path))
                return false;

            // 检查空字符（注入攻击）
            if (path.Contains('\0'))
            {
                return true;
            }

            // 检查其他控制字符
            foreach (char c in path)
            {
                if (c < 32 && c != '\t' && c != '\n' && c != '\r')
                {
                    return true;
                }
            }

            // 规范化路径检查：使用 Path.GetFullPath 检测实际路径是否超出预期
            try
            {
                // 获取目录部分（因为 strm 文件路径必须是文件而非目录）
                var directory = Path.GetDirectoryName(path);
                if (string.IsNullOrEmpty(directory))
                {
                    return true;
                }

                // 检查 .. 路径遍历（规范化前）
                var normalized = path.Replace('/', '\\');
                
                // 检查 .. 后跟路径分隔符，或在开头的情况
                if (normalized.Contains(@"..\") || normalized.Contains(@"\..") || 
                    normalized.StartsWith(@"..") || normalized.EndsWith(@".."))
                {
                    return true;
                }

                // 检查 UNC 路径（潜在的 SMB 路径遍历）
                if (normalized.StartsWith(@"\\"))
                {
                    return true;
                }

                // 检查交替数据流（ADS）- Windows 特有攻击
                if (normalized.Contains(':') && !normalized.StartsWith(@"\\?\") && !normalized.StartsWith(@"\\.\"))
                {
                    // 排除驱动器盘符（如 C:\）
                    var colonIndex = normalized.IndexOf(':');
                    if (colonIndex > 1 || (colonIndex == 1 && !char.IsLetter(normalized[0])))
                    {
                        return true;
                    }
                }

                // 检查符号链接和快捷方式
                var extension = Path.GetExtension(path)?.ToLowerInvariant();
                if (extension == ".lnk" || extension == ".symlink" || extension == ".junction")
                {
                    return true;
                }
            }
            catch
            {
                // 如果路径规范化失败，视为可疑
                return true;
            }

            return false;
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
        /// 验证缓存路径并返回（通用验证方法）
        /// </summary>
        private (bool valid, string cachePath) ValidateCachePath(string strmPath)
        {
            if (string.IsNullOrWhiteSpace(strmPath))
            {
                _logger.LogDebug("Invalid strm path for cache lookup");
                return (false, null);
            }

            var cachePath = GetCachePath(strmPath);
            if (string.IsNullOrWhiteSpace(cachePath))
            {
                _logger.LogDebug("Failed to get cache path for: {Path}", strmPath);
                return (false, null);
            }

            if (!File.Exists(cachePath))
            {
                return (false, null);
            }

            return (true, cachePath);
        }

        /// <summary>
        /// 验证缓存数据是否有效
        /// </summary>
        private bool ValidateCacheData(MediaInfoCacheData cache, bool requireMediaStreams = true)
        {
            if (cache?.IsValid != true)
                return false;
            
            if (requireMediaStreams && cache.MediaStreams == null)
                return false;

            return true;
        }

        /// <summary>
        /// 检查并读取缓存（同步版本，保持兼容性）
        /// </summary>
        public bool TryGetCachedMediaStreams(string strmPath, out List<MediaStream> mediaStreams)
        {
            mediaStreams = null;

            var (valid, cachePath) = ValidateCachePath(strmPath);
            if (!valid)
                return false;

            try
            {
                var json = File.ReadAllText(cachePath);
                var cache = JsonSerializer.Deserialize<MediaInfoCacheData>(json, JsonOptions);

                if (!ValidateCacheData(cache))
                    return false;

                mediaStreams = cache.MediaStreams;
                _logger.LogDebug("Loaded cached media streams from {Path}", cachePath);
                return true;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Error reading cache from {Path}", strmPath);
                return false;
            }
        }

        /// <summary>
        /// 检查并读取缓存（异步版本）
        /// </summary>
        public async Task<(bool success, List<MediaStream> mediaStreams)> TryGetCachedMediaStreamsAsync(string strmPath, CancellationToken cancellationToken = default)
        {
            var (valid, cachePath) = ValidateCachePath(strmPath);
            if (!valid)
                return (false, null);

            try
            {
                var json = await File.ReadAllTextAsync(cachePath, cancellationToken).ConfigureAwait(false);
                var cache = JsonSerializer.Deserialize<MediaInfoCacheData>(json, JsonOptions);

                if (!ValidateCacheData(cache))
                    return (false, null);

                _logger.LogDebug("Loaded cached media streams from {Path}", cachePath);
                return (true, cache.MediaStreams);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Error reading cache from {Path}", strmPath);
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
                    _logger.LogWarning(cleanupEx, "Failed to cleanup temp cache file {Path}", tempPath);
                }
            }
        }

        /// <summary>
        /// 验证保存缓存的前置条件
        /// </summary>
        private (bool valid, string cachePath) ValidateSaveCache(string strmPath)
        {
            if (string.IsNullOrWhiteSpace(strmPath))
            {
                _logger.LogDebug("Invalid strm path for cache save");
                return (false, null);
            }

            var cachePath = GetCachePath(strmPath);
            if (string.IsNullOrWhiteSpace(cachePath))
            {
                _logger.LogDebug("Failed to get cache path for: {Path}", strmPath);
                return (false, null);
            }

            return (true, cachePath);
        }

        /// <summary>
        /// 保存缓存（原子写入，先写临时文件再重命名）
        /// </summary>
        public async Task SaveCacheAsync(string strmPath, IEnumerable<MediaStream> mediaStreams, CancellationToken cancellationToken = default)
        {
            try
            {
                var (valid, cachePath) = ValidateSaveCache(strmPath);
                if (!valid)
                    return;

                var cache = new MediaInfoCacheData
                {
                    Version = "1.0",
                    Timestamp = DateTime.UtcNow,
                    MediaStreams = mediaStreams.ToList(),
                    IsValid = true
                };

                var json = JsonSerializer.Serialize(cache, JsonOptions);
                await WriteFileAtomicallyAsync(cachePath, json, cancellationToken).ConfigureAwait(false);

                _logger.LogDebug("Saved cache to {Path}", cachePath);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error saving cache to {Path}", strmPath);
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
            CancellationToken cancellationToken = default)
        {
            try
            {
                var (valid, cachePath) = ValidateSaveCache(strmPath);
                if (!valid)
                    return;

                var cache = new MediaInfoCacheData
                {
                    Version = "1.0",
                    Timestamp = DateTime.UtcNow,
                    MediaStreams = mediaStreams?.ToList() ?? new List<MediaStream>(),
                    IsValid = true,
                    Size = size,
                    RunTimeTicks = runTimeTicks,
                    Container = container
                };

                var json = JsonSerializer.Serialize(cache, JsonOptions);
                await WriteFileAtomicallyAsync(cachePath, json, cancellationToken).ConfigureAwait(false);

                _logger.LogDebug("Saved full cache (Size={Size}) to {Path}", size, cachePath);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error saving full cache to {Path}", strmPath);
            }
        }

        /// <summary>
        /// 尝试读取完整缓存数据（包含元数据）
        /// </summary>
        public bool TryGetFullCache(string strmPath, out MediaInfoCacheData cacheData)
        {
            cacheData = null;

            var (valid, cachePath) = ValidateCachePath(strmPath);
            if (!valid)
                return false;

            try
            {
                var json = File.ReadAllText(cachePath);
                var cache = JsonSerializer.Deserialize<MediaInfoCacheData>(json, JsonOptions);

                if (!ValidateCacheData(cache, requireMediaStreams: false))
                    return false;

                cacheData = cache;
                _logger.LogDebug("Loaded full cache (Size={Size}) from {Path}", cache.Size, cachePath);
                return true;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Error reading full cache from {Path}", strmPath);
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
                    _logger.LogDebug("Invalid strm path for cache clear");
                    return;
                }

                var cachePath = GetCachePath(strmPath);
                if (string.IsNullOrWhiteSpace(cachePath))
                {
                    _logger.LogDebug("Failed to get cache path for: {Path}", strmPath);
                    return;
                }

                if (File.Exists(cachePath))
                {
                    File.Delete(cachePath);
                    _logger.LogInformation("Cleared cache: {Path}", cachePath);
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Error clearing cache: {Path}", strmPath);
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

                    // 从缓存文件名反推出 strm 文件名
                    var baseName = Path.GetFileNameWithoutExtension(fileName);
                    var strmPath = Path.Combine(Path.GetDirectoryName(jsonFile) ?? string.Empty, baseName + StrmMediaInfoService.StrmFileExtension);

                    // 使用 GetCachePath 验证路径安全性
                    var expectedCachePath = GetCachePath(strmPath);
                    if (expectedCachePath == null)
                    {
                        continue;
                    }

                    // 确保找到的 json 文件确实是对应的缓存文件
                    if (!string.Equals(expectedCachePath, jsonFile, StringComparison.OrdinalIgnoreCase))
                    {
                        continue;
                    }

                    // 安全删除缓存文件
                    try
                    {
                        File.Delete(jsonFile);
                        cleared++;
                    }
                    catch (Exception ex)
                    {
                        _logger.LogWarning(ex, "Error deleting cache file {Path}", jsonFile);
                    }
                }

                _logger.LogInformation("Cleared {Count} cache files in {Dir}", cleared, directoryPath);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error clearing caches in {Dir}", directoryPath);
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
    }
}
