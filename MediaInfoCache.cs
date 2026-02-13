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
    private readonly ILogger<ExtractTask> _logger;
    private int _cacheExpirationDays;
    
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
      Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
      WriteIndented = true,
      DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    public MediaInfoCache(ILogger<ExtractTask> logger, int cacheExpirationDays = 7)
    {
      _logger = logger;
      _cacheExpirationDays = cacheExpirationDays;
    }

    /// <summary>
    /// 获取缓存文件路径
    /// </summary>
    private static string GetCachePath(string strmPath)
    {
      if (string.IsNullOrWhiteSpace(strmPath))
        throw new ArgumentException("Invalid strm path", nameof(strmPath));
      
      var directory = Path.GetDirectoryName(strmPath);
      if (string.IsNullOrWhiteSpace(directory))
        directory = Directory.GetCurrentDirectory();
        
      var fileName = Path.GetFileNameWithoutExtension(strmPath) + ".json";
      return Path.Combine(directory, fileName);
    }

    /// <summary>
    /// 检查并读取缓存
    /// </summary>
    public bool TryGetCachedMediaStreams(string strmPath, out List<MediaStream> mediaStreams)
    {
      mediaStreams = null;

      try
      {
        var cachePath = GetCachePath(strmPath);

        if (!File.Exists(cachePath))
        {
          return false;
        }

        var fileInfo = new FileInfo(cachePath);
        // 检查缓存是否过期
        if (DateTime.UtcNow - fileInfo.LastWriteTimeUtc > TimeSpan.FromDays(_cacheExpirationDays))
        {
          _logger.LogDebug("StrmTool - Cache file expired: {Path}", cachePath);
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
    /// 保存缓存
    /// </summary>
    public async Task SaveCacheAsync(string strmPath, IEnumerable<MediaStream> mediaStreams, string sourceUrl, CancellationToken cancellationToken = default)
    {
      try
      {
        var cachePath = GetCachePath(strmPath);

        var cache = new MediaInfoCacheData
        {
          Version = "1.0",
          Timestamp = DateTime.UtcNow,
          MediaStreams = mediaStreams.ToList(),
          SourceUrl = sourceUrl,
          IsValid = true
        };

        var json = JsonSerializer.Serialize(cache, JsonOptions);

        // 使用异步写入避免阻塞
        await File.WriteAllTextAsync(cachePath, json, cancellationToken);

        _logger.LogDebug("StrmTool - Saved cache to {Path}", cachePath);
      }
      catch (Exception ex)
      {
        _logger.LogError(ex, "StrmTool - Error saving cache to {Path}", strmPath);
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
        var cachePath = GetCachePath(strmPath);

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

        var jsonFiles = Directory.GetFiles(directoryPath, "*.json", SearchOption.AllDirectories);
        int cleared = 0;

        foreach (var jsonFile in jsonFiles)
        {
          // 检查是否是 strm 的缓存文件（对应的 strm 文件存在）
          var strmPath = Path.Combine(
            Path.GetDirectoryName(jsonFile),
            Path.GetFileNameWithoutExtension(jsonFile) + ".strm"
          );

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

    [JsonPropertyName("sourceUrl")]
    public string SourceUrl { get; set; }

    [JsonPropertyName("isValid")]
    public bool IsValid { get; set; }
  }
}
