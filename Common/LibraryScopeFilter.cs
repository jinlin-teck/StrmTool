using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Entities;

namespace StrmTool.Common
{
    /// <summary>
    /// 媒体库生效范围与附加视频过滤器：
    /// 负责附加视频识别、媒体库范围预计算匹配、SQLite AncestorIds 下推解析，以及轻量级范围计数短缓存。
    /// </summary>
    public static class LibraryScopeFilter
    {
        private sealed class ScopedCountCacheEntry
        {
            public ScopedCountCacheEntry(string scopeKey, int count, long expiresAtUtcTicks, int version)
            {
                ScopeKey = scopeKey;
                Count = count;
                ExpiresAtUtcTicks = expiresAtUtcTicks;
                Version = version;
            }

            public string ScopeKey { get; }
            public int Count { get; }
            public long ExpiresAtUtcTicks { get; }
            public int Version { get; }
        }

        /// <summary>
        /// 单次遍历复用的媒体库范围匹配器，避免对每个条目重复分配 ID 集合和重复解析物理路径。
        /// </summary>
        public sealed class ScopeMatcher
        {
            private readonly bool _enableAllLibraries;
            private readonly bool _includeExtraVideos;
            private readonly ILibraryManager? _libraryManager;
            private readonly HashSet<string>? _enabledSet;
            private readonly object _locationsLock = new object();
            private bool _locationsResolved;
            private List<string>? _normalizedEnabledLocations;

            internal ScopeMatcher(ILibraryManager? libraryManager, PluginConfiguration config)
            {
                _enableAllLibraries = config.EnableAllLibraries;
                _includeExtraVideos = config.IncludeExtraVideos;
                _libraryManager = libraryManager;

                if (!_enableAllLibraries &&
                    config.EnabledLibraryIds != null &&
                    config.EnabledLibraryIds.Length > 0)
                {
                    _enabledSet = BuildNormalizedLibraryIdSet(config.EnabledLibraryIds);
                }
            }

            public bool IsItemInLibraries(BaseItem? item)
            {
                if (_enableAllLibraries)
                {
                    return true;
                }

                if (item == null ||
                    _libraryManager == null ||
                    _enabledSet == null ||
                    _enabledSet.Count == 0)
                {
                    return false;
                }

                var collectionFolders = _libraryManager.GetCollectionFolders(item);
                if (collectionFolders != null && collectionFolders.Length > 0)
                {
                    bool hasNonNullFolder = false;
                    foreach (var folder in collectionFolders)
                    {
                        if (folder == null)
                        {
                            continue;
                        }

                        hasNonNullFolder = true;
                        if (MatchesLibraryItem(_enabledSet, folder))
                        {
                            return true;
                        }
                    }

                    if (hasNonNullFolder)
                    {
                        return false;
                    }
                }

                var topParent = item.GetTopParent();
                if (topParent != null && MatchesLibraryItem(_enabledSet, topParent))
                {
                    return true;
                }

                if (!string.IsNullOrWhiteSpace(item.Path))
                {
                    var locations = GetNormalizedEnabledLocations();
                    if (locations.Count > 0)
                    {
                        string fullItemPath;
                        try
                        {
                            fullItemPath = Path.GetFullPath(item.Path);
                        }
                        catch
                        {
                            return false;
                        }

                        foreach (var fullLoc in locations)
                        {
                            if (IsFullItemPathWithinNormalizedLocation(fullItemPath, fullLoc))
                            {
                                return true;
                            }
                        }
                    }
                }

                return false;
            }

            public bool IsItemAllowed(BaseItem? item)
            {
                if (item == null)
                {
                    return false;
                }

                if (!_includeExtraVideos && IsExtraVideoItem(item))
                {
                    return false;
                }

                return IsItemInLibraries(item);
            }

            private List<string> GetNormalizedEnabledLocations()
            {
                lock (_locationsLock)
                {
                    if (_locationsResolved && _normalizedEnabledLocations != null)
                    {
                        return _normalizedEnabledLocations;
                    }

                    var resolved = new List<string>();
                    var virtualFolders = _libraryManager?.GetVirtualFolders();
                    if (virtualFolders != null && _enabledSet != null)
                    {
                        foreach (var vf in virtualFolders)
                        {
                            if (vf == null || !MatchesVirtualFolder(_enabledSet, vf) || vf.Locations == null)
                            {
                                continue;
                            }

                            foreach (var location in vf.Locations)
                            {
                                if (string.IsNullOrWhiteSpace(location))
                                {
                                    continue;
                                }

                                try
                                {
                                    var normalized = MediaInfoJsonStore.TrimEndingDirectorySeparatorPreservingRoot(
                                        Path.GetFullPath(location));
                                    if (!string.IsNullOrEmpty(normalized))
                                    {
                                        resolved.Add(normalized);
                                    }
                                }
                                catch
                                {
                                    // 忽略非法路径配置
                                }
                            }
                        }
                    }

                    _normalizedEnabledLocations = resolved;
                    _locationsResolved = true;
                    return _normalizedEnabledLocations;
                }
            }
        }

        private static readonly long ScopedCountCacheTtlTicks = TimeSpan.FromSeconds(15).Ticks;

        /// <summary>
        /// 计数缓存的失效节流窗口：库扫描期间每个条目都会触发失效，
        /// 节流后最多每 5 秒真正失效一次，避免状态接口反复执行全库查询。
        /// </summary>
        private static readonly long ScopedCountInvalidationThrottleTicks = TimeSpan.FromSeconds(5).Ticks;

        private static readonly object ScopedCountCacheLock = new object();
        private static ScopedCountCacheEntry? _scopedCountCache;
        private static int _scopedCountCacheVersion;
        private static long _lastScopedCountInvalidationTicks;
        private static string? _lastWarnedScopeSignature;

        private static readonly StringComparison PathComparison = RuntimeInformation.IsOSPlatform(OSPlatform.Windows)
            ? StringComparison.OrdinalIgnoreCase
            : StringComparison.Ordinal;

        public static ScopeMatcher CreateMatcher(ILibraryManager? libraryManager, PluginConfiguration? config = null)
        {
            return new ScopeMatcher(libraryManager, config ?? Plugin.GetSafeConfiguration());
        }

        /// <summary>
        /// 判断条目是否为附加视频（Trailer / MusicVideo / 带 ExtraType 的视频）
        /// </summary>
        public static bool IsExtraVideoItem(BaseItem? item)
        {
            if (!(item is Video))
            {
                return false;
            }

            if (item.ExtraType.HasValue)
            {
                return true;
            }

            var typeName = item.GetType().Name;
            return string.Equals(typeName, "Trailer", StringComparison.OrdinalIgnoreCase) ||
                   string.Equals(typeName, "MusicVideo", StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>
        /// 判断媒体项是否位于当前插件配置允许处理的媒体库范围内
        /// </summary>
        public static bool IsItemInConfiguredLibraries(
            BaseItem? item,
            ILibraryManager? libraryManager,
            PluginConfiguration? config = null)
        {
            return CreateMatcher(libraryManager, config).IsItemInLibraries(item);
        }

        /// <summary>
        /// 将配置的媒体库 ID 解析为 Emby InternalId，供 InternalItemsQuery.AncestorIds 下推 SQLite 过滤。
        /// 已删除的历史媒体库残留 ID 会被安全忽略，不破坏其余有效媒体库的过滤；
        /// 仅当存在活跃媒体库无法解析为数字 InternalId 时才将 allResolvedToAncestors 置为 false。
        /// </summary>
        public static bool TryResolveEnabledLibraryAncestorIds(
            ILibraryManager libraryManager,
            PluginConfiguration config,
            out long[] internalIds,
            out bool allResolvedToAncestors)
        {
            internalIds = Array.Empty<long>();
            if (libraryManager == null ||
                config.EnabledLibraryIds == null ||
                config.EnabledLibraryIds.Length == 0)
            {
                allResolvedToAncestors = true;
                return false;
            }

            var resolved = new HashSet<long>();
            var virtualFolders = libraryManager.GetVirtualFolders();
            bool hasUnresolvableActiveFolder = false;
            var staleIds = new List<string>();
            var activeItemIds = BuildActiveVirtualFolderIdSet(virtualFolders);

            foreach (var rawId in config.EnabledLibraryIds)
            {
                if (string.IsNullOrWhiteSpace(rawId))
                {
                    continue;
                }

                var trimmed = rawId.Trim();
                if (long.TryParse(trimmed, NumberStyles.Integer, CultureInfo.InvariantCulture, out var numericId) &&
                    numericId > 0)
                {
                    // 数字 ID 来自 InternalId，库数据库重建后会失效甚至被新库复用。
                    // 仅在能对应到当前活跃媒体库时才信任，避免作用域静默错配到别的媒体库。
                    if (activeItemIds.Count == 0 || activeItemIds.Contains(numericId))
                    {
                        resolved.Add(numericId);
                    }
                    else
                    {
                        staleIds.Add(trimmed);
                    }

                    continue;
                }

                bool resolvedToInternalId = false;
                bool matchedActiveFolder = false;

                if (Guid.TryParse(trimmed, out var guid))
                {
                    var folderItem = libraryManager.GetItemById(guid);
                    if (folderItem != null)
                    {
                        matchedActiveFolder = true;
                        if (folderItem.InternalId > 0)
                        {
                            resolved.Add(folderItem.InternalId);
                            resolvedToInternalId = true;
                        }
                    }
                }

                if (!resolvedToInternalId && virtualFolders != null)
                {
                    var singleSet = BuildNormalizedLibraryIdSet(new[] { trimmed });
                    foreach (var vf in virtualFolders)
                    {
                        if (vf != null && MatchesVirtualFolder(singleSet, vf))
                        {
                            matchedActiveFolder = true;
                            if (long.TryParse(vf.ItemId, NumberStyles.Integer, CultureInfo.InvariantCulture, out var vfId) &&
                                vfId > 0)
                            {
                                resolved.Add(vfId);
                                resolvedToInternalId = true;
                            }
                        }
                    }
                }

                if (matchedActiveFolder && !resolvedToInternalId)
                {
                    hasUnresolvableActiveFolder = true;
                }
                else if (!matchedActiveFolder)
                {
                    staleIds.Add(trimmed);
                }
            }

            if (staleIds.Count > 0)
            {
                var logger = Plugin.Logger;
                if (logger != null)
                {
                    Common.LogHelper.Debug(
                        logger,
                        $"Ignoring configured library ids that no longer match any active library: {string.Join(", ", staleIds)}");
                }
            }

            internalIds = resolved.ToArray();
            allResolvedToAncestors = !hasUnresolvableActiveFolder;

            if (internalIds.Length == 0)
            {
                WarnEmptyScopeOnce(config.EnabledLibraryIds);
            }

            return internalIds.Length > 0;
        }

        private static HashSet<long> BuildActiveVirtualFolderIdSet(List<VirtualFolderInfo>? virtualFolders)
        {
            var ids = new HashSet<long>();
            if (virtualFolders == null)
            {
                return ids;
            }

            foreach (var vf in virtualFolders)
            {
                if (vf != null &&
                    long.TryParse(vf.ItemId, NumberStyles.Integer, CultureInfo.InvariantCulture, out var vfId) &&
                    vfId > 0)
                {
                    ids.Add(vfId);
                }
            }

            return ids;
        }

        private static void WarnEmptyScopeOnce(string[] configuredIds)
        {
            var logger = Plugin.Logger;
            if (logger == null)
            {
                return;
            }

            var signature = string.Join(",", configuredIds.OrderBy(id => id, StringComparer.OrdinalIgnoreCase));
            lock (ScopedCountCacheLock)
            {
                if (string.Equals(_lastWarnedScopeSignature, signature, StringComparison.Ordinal))
                {
                    return;
                }

                _lastWarnedScopeSignature = signature;
            }

            Common.LogHelper.Warn(
                logger,
                $"Library scope is enabled but none of the configured ids ({signature}) matches an active library. " +
                "Auto-extract and scheduled tasks will skip all STRM files until the scope is re-selected.");
        }

        /// <summary>
        /// 获取或按回调计算当前配置范围下的 STRM 数量（带 15 秒短 TTL 缓存，避免前端轮询反复全表物化）。
        /// </summary>
        internal static int GetOrAddScopedCount(PluginConfiguration config, Func<int> countProvider)
        {
            if (countProvider == null)
            {
                throw new ArgumentNullException(nameof(countProvider));
            }

            var scopeKey = BuildScopeCacheKey(config);
            long nowTicks = DateTime.UtcNow.Ticks;
            int startVersion;

            lock (ScopedCountCacheLock)
            {
                var cached = _scopedCountCache;
                if (cached != null &&
                    cached.Version == _scopedCountCacheVersion &&
                    string.Equals(cached.ScopeKey, scopeKey, StringComparison.Ordinal) &&
                    nowTicks <= cached.ExpiresAtUtcTicks)
                {
                    return cached.Count;
                }

                startVersion = _scopedCountCacheVersion;
            }

            int count = countProvider();
            long expiresAt = DateTime.UtcNow.Ticks + ScopedCountCacheTtlTicks;

            lock (ScopedCountCacheLock)
            {
                if (_scopedCountCacheVersion == startVersion)
                {
                    _scopedCountCache = new ScopedCountCacheEntry(scopeKey, count, expiresAt, startVersion);
                }
            }

            return count;
        }

        public static void InvalidateScopedCountCache(bool force = false)
        {
            lock (ScopedCountCacheLock)
            {
                long nowTicks = DateTime.UtcNow.Ticks;
                if (!force && nowTicks - _lastScopedCountInvalidationTicks < ScopedCountInvalidationThrottleTicks)
                {
                    return;
                }

                _lastScopedCountInvalidationTicks = nowTicks;
                _scopedCountCacheVersion++;
                _scopedCountCache = null;
            }
        }

        private static string BuildScopeCacheKey(PluginConfiguration config)
        {
            var ids = config.EnabledLibraryIds;
            var idsPart = ids == null || ids.Length == 0
                ? string.Empty
                : string.Join(",", ids.OrderBy(id => id, StringComparer.OrdinalIgnoreCase));
            return $"{config.EnableAllLibraries}|{config.IncludeExtraVideos}|{idsPart}";
        }

        private static HashSet<string> BuildNormalizedLibraryIdSet(string[] configuredIds)
        {
            var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var rawId in configuredIds)
            {
                if (string.IsNullOrWhiteSpace(rawId))
                {
                    continue;
                }

                var trimmed = rawId.Trim();
                set.Add(trimmed);
                if (Guid.TryParse(trimmed, out var guid))
                {
                    set.Add(guid.ToString("N"));
                    set.Add(guid.ToString("D"));
                }
            }

            return set;
        }

        private static bool MatchesLibraryItem(HashSet<string> enabledSet, BaseItem folder)
        {
            if (folder.InternalId > 0 &&
                enabledSet.Contains(folder.InternalId.ToString(CultureInfo.InvariantCulture)))
            {
                return true;
            }

            return folder.Id != Guid.Empty &&
                   (enabledSet.Contains(folder.Id.ToString("N")) ||
                    enabledSet.Contains(folder.Id.ToString("D")));
        }

        private static bool MatchesVirtualFolder(HashSet<string> enabledSet, VirtualFolderInfo folder)
        {
            if (!string.IsNullOrWhiteSpace(folder.ItemId) && enabledSet.Contains(folder.ItemId))
            {
                return true;
            }

            if (!string.IsNullOrWhiteSpace(folder.Id) && enabledSet.Contains(folder.Id))
            {
                return true;
            }

            if (!string.IsNullOrWhiteSpace(folder.Guid))
            {
                if (enabledSet.Contains(folder.Guid))
                {
                    return true;
                }

                if (Guid.TryParse(folder.Guid, out var guid) &&
                    (enabledSet.Contains(guid.ToString("N")) || enabledSet.Contains(guid.ToString("D"))))
                {
                    return true;
                }
            }

            return false;
        }

        private static bool IsFullItemPathWithinNormalizedLocation(string fullItem, string fullLoc)
        {
            var rootLoc = Path.GetPathRoot(fullLoc);
            if (!string.IsNullOrEmpty(rootLoc) &&
                MediaInfoJsonStore.PathStringComparer.Equals(fullLoc, rootLoc))
            {
                return fullItem.StartsWith(fullLoc, PathComparison);
            }

            return MediaInfoJsonStore.PathStringComparer.Equals(fullItem, fullLoc) ||
                   fullItem.StartsWith(fullLoc + Path.DirectorySeparatorChar, PathComparison);
        }
    }
}
