using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Model.Logging;

namespace StrmTool.Common
{
    /// <summary>
    /// 集中备份目录校验、只读可写性探测与可逆路径映射解析器。
    /// </summary>
    internal static class BackupPathResolver
    {
        internal static readonly StringComparer PathComparer = RuntimeInformation.IsOSPlatform(OSPlatform.Windows)
            ? StringComparer.OrdinalIgnoreCase
            : StringComparer.Ordinal;

        private static readonly object CustomDirProbeLock = new object();
        private static readonly long CustomDirProbeTtlTicks = TimeSpan.FromSeconds(10).Ticks;
        private static string? _lastProbedCustomDir;
        private static long _lastProbeTicks;
        private static bool _lastProbeResult;
        private static string? _lastProbeError;
        private static string? _lastWarnedInvalidCustomDir;

        internal static void ClearCache()
        {
            Interlocked.Exchange(ref _lastWarnedInvalidCustomDir, null);
            lock (CustomDirProbeLock)
            {
                _lastProbedCustomDir = null;
                _lastProbeTicks = 0;
                _lastProbeResult = false;
                _lastProbeError = null;
            }
        }

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

        internal static bool TryValidateCustomBackupDirectory(
            string? configuredDir,
            out string normalizedRoot,
            out string? errorReason)
        {
            normalizedRoot = string.Empty;
            errorReason = null;

            if (string.IsNullOrWhiteSpace(configuredDir))
            {
                return false;
            }

            var trimmed = configuredDir.Trim();
            if (!Path.IsPathRooted(trimmed))
            {
                errorReason = "Custom backup directory must be an absolute path.";
                return false;
            }

            try
            {
                var fullPath = Path.GetFullPath(trimmed);
                var cleanPath = TrimEndingDirectorySeparatorPreservingRoot(fullPath);
                var rootPath = Path.GetPathRoot(fullPath);
                var cleanRoot = !string.IsNullOrEmpty(rootPath)
                    ? TrimEndingDirectorySeparatorPreservingRoot(rootPath)
                    : string.Empty;

                if (!string.IsNullOrEmpty(cleanRoot) &&
                    PathComparer.Equals(cleanPath, cleanRoot))
                {
                    errorReason = "Custom backup directory cannot be a filesystem root.";
                    return false;
                }

                normalizedRoot = cleanPath;
                return true;
            }
            catch (Exception ex)
            {
                errorReason = $"Invalid custom backup directory path: {ex.Message}";
                return false;
            }
        }

        /// <summary>
        /// 探测自定义集中备份目录是否可用（不主动创建缺失目录，避免只读状态查询掩盖未挂载的存储卷）。
        /// 结果按路径缓存 10 秒，避免状态接口轮询反复写入探测文件。
        /// </summary>
        internal static bool TryProbeCustomBackupDirectoryWritable(string? normalizedRoot, out string? errorReason)
        {
            errorReason = null;
            if (string.IsNullOrWhiteSpace(normalizedRoot))
            {
                errorReason = "Custom backup directory is empty.";
                return false;
            }

            lock (CustomDirProbeLock)
            {
                long nowTicks = DateTime.UtcNow.Ticks;
                if (string.Equals(_lastProbedCustomDir, normalizedRoot, StringComparison.Ordinal) &&
                    nowTicks - _lastProbeTicks < CustomDirProbeTtlTicks)
                {
                    errorReason = _lastProbeError;
                    return _lastProbeResult;
                }
            }

            bool writable;
            string? probeError = null;
            try
            {
                var fullPath = Path.GetFullPath(normalizedRoot);
                if (File.Exists(fullPath))
                {
                    writable = false;
                    probeError = "Custom backup directory points to an existing file, not a directory.";
                }
                else
                {
                    string? probeTargetDir = null;
                    if (Directory.Exists(fullPath))
                    {
                        probeTargetDir = fullPath;
                    }
                    else
                    {
                        var parentDir = Path.GetDirectoryName(fullPath);
                        if (string.IsNullOrEmpty(parentDir) || !Directory.Exists(parentDir))
                        {
                            writable = false;
                            probeError = "Custom backup directory (or its parent directory) does not exist.";
                        }
                        else
                        {
                            probeTargetDir = parentDir;
                        }
                    }

                    if (probeTargetDir != null)
                    {
                        var probePath = Path.Combine(probeTargetDir, $".strmtool-write-test-{Guid.NewGuid():N}");
                        File.WriteAllText(probePath, "ok");
                        AtomicFile.TryDelete(probePath);
                        writable = true;
                    }
                    else
                    {
                        writable = false;
                    }
                }
            }
            catch (Exception ex)
            {
                writable = false;
                probeError = $"Custom backup directory is not writable: {ex.Message}";
            }

            lock (CustomDirProbeLock)
            {
                _lastProbedCustomDir = normalizedRoot;
                _lastProbeTicks = DateTime.UtcNow.Ticks;
                _lastProbeResult = writable;
                _lastProbeError = probeError;
            }

            errorReason = probeError;
            return writable;
        }

        internal static string GetJsonPath(BaseItem item, string? customBackupDirectory, ILogger logger)
        {
            if (item == null)
            {
                throw new ArgumentNullException(nameof(item));
            }

            var primary = GetWriteJsonPath(item, customBackupDirectory, logger);
            var sidecar = GetSidecarJsonPath(item);

            if (!PathComparer.Equals(primary, sidecar) &&
                !File.Exists(primary) &&
                File.Exists(sidecar))
            {
                return sidecar;
            }

            return primary;
        }

        internal static string GetWriteJsonPath(BaseItem item, string? customBackupDirectory, ILogger logger)
        {
            if (item == null)
            {
                throw new ArgumentNullException(nameof(item));
            }

            var mirroredDir = TryResolveMirroredDirectoryForItem(item, customBackupDirectory, logger);
            if (!string.IsNullOrEmpty(mirroredDir))
            {
                var mediaDirectory = !string.IsNullOrWhiteSpace(item.Path) ? Path.GetDirectoryName(item.Path) : null;
                var fileName = BuildJsonBaseFileName(item, mediaDirectory);
                return Path.Combine(mirroredDir, $"{fileName}{CommonConfiguration.MediaInfoFileExtension}");
            }

            return GetSidecarJsonPath(item);
        }

        internal static string GetSidecarJsonPath(BaseItem item)
        {
            if (item == null)
            {
                throw new ArgumentNullException(nameof(item));
            }

            var mediaDirectory = !string.IsNullOrWhiteSpace(item.Path) ? Path.GetDirectoryName(item.Path) : null;
            var fileName = BuildJsonBaseFileName(item, mediaDirectory);
            if (string.IsNullOrEmpty(mediaDirectory))
            {
                var tempPath = Path.Combine(Path.GetTempPath(), "StrmTool");
                return Path.Combine(tempPath, $"{fileName}{CommonConfiguration.MediaInfoFileExtension}");
            }

            return Path.Combine(mediaDirectory, $"{fileName}{CommonConfiguration.MediaInfoFileExtension}");
        }

        internal static string GetAudioMetadataDirectory(BaseItem item, string? customBackupDirectory, ILogger logger)
        {
            if (item == null)
            {
                throw new ArgumentNullException(nameof(item));
            }

            var mirroredDir = TryResolveMirroredDirectoryForItem(item, customBackupDirectory, logger);
            if (!string.IsNullOrEmpty(mirroredDir))
            {
                return Path.Combine(mirroredDir, ".metadata");
            }

            return GetSidecarAudioMetadataDirectory(item);
        }

        internal static string GetSidecarAudioMetadataDirectory(BaseItem item)
        {
            if (item == null)
            {
                throw new ArgumentNullException(nameof(item));
            }

            var mediaDirectory = !string.IsNullOrWhiteSpace(item.Path) ? Path.GetDirectoryName(item.Path) : null;
            return Path.Combine(
                string.IsNullOrEmpty(mediaDirectory) ? Path.GetTempPath() : mediaDirectory,
                ".metadata");
        }

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

        private static string? TryResolveMirroredDirectoryForItem(
            BaseItem item,
            string? customBackupDirectory,
            ILogger logger)
        {
            var configuredCustomDir = customBackupDirectory ?? Plugin.GetSafeConfiguration().CustomBackupDirectory;
            if (string.IsNullOrWhiteSpace(configuredCustomDir))
            {
                return null;
            }

            if (TryValidateCustomBackupDirectory(configuredCustomDir, out var customRoot, out var errorReason))
            {
                var mediaDirectory = !string.IsNullOrWhiteSpace(item.Path) ? Path.GetDirectoryName(item.Path) : null;
                return TryBuildMirroredDirectory(customRoot, mediaDirectory);
            }

            WarnInvalidCustomDirectoryOnce(logger, configuredCustomDir, errorReason);
            return null;
        }

        private static string BuildJsonBaseFileName(BaseItem item, string? mediaDirectory)
        {
            if (string.IsNullOrEmpty(mediaDirectory))
            {
                string safeName = MakeSafeFilename(item.Name);
                var suffix = item.InternalId > 0 ? $"_{item.InternalId}" : string.Empty;
                return $"{safeName}{suffix}";
            }

            var mediaFileName = Path.GetFileNameWithoutExtension(item.Path);
            return string.IsNullOrEmpty(mediaFileName)
                ? MakeSafeFilename(item.Name)
                : mediaFileName;
        }

        private static string? TryBuildMirroredDirectory(string normalizedCustomRoot, string? mediaDirectory)
        {
            if (string.IsNullOrEmpty(mediaDirectory))
            {
                return Path.Combine(normalizedCustomRoot, "_unmapped");
            }

            try
            {
                var fullMediaDirectory = Path.GetFullPath(mediaDirectory);
                var root = Path.GetPathRoot(fullMediaDirectory) ?? string.Empty;
                var segments = fullMediaDirectory.Substring(root.Length).Split(
                    new[] { Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar },
                    StringSplitOptions.RemoveEmptyEntries);

                var parts = new List<string> { normalizedCustomRoot, "_paths_v2" };
                AppendEncodedPathSegment(parts, root);
                foreach (var segment in segments)
                {
                    AppendEncodedPathSegment(parts, segment);
                }

                var combined = Path.Combine(parts.ToArray());
                var fullMirrored = TrimEndingDirectorySeparatorPreservingRoot(Path.GetFullPath(combined));
                var expectedPrefix = normalizedCustomRoot + Path.DirectorySeparatorChar;
                var comparison = RuntimeInformation.IsOSPlatform(OSPlatform.Windows)
                    ? StringComparison.OrdinalIgnoreCase
                    : StringComparison.Ordinal;

                if (!fullMirrored.StartsWith(expectedPrefix, comparison))
                {
                    return null;
                }

                return fullMirrored;
            }
            catch
            {
                return null;
            }
        }

        private static void AppendEncodedPathSegment(List<string> parts, string segment)
        {
            const string hex = "0123456789abcdef";
            var encoded = new StringBuilder();
            foreach (var value in Encoding.UTF8.GetBytes(segment))
            {
                if ((value >= (byte)'a' && value <= (byte)'z') ||
                    (value >= (byte)'0' && value <= (byte)'9') || value == (byte)'-' || value == (byte)'_')
                {
                    encoded.Append((char)value);
                }
                else
                {
                    encoded.Append('~');
                    encoded.Append(hex[value >> 4]);
                    encoded.Append(hex[value & 15]);
                }
            }

            const int chunkLength = 180;
            if (encoded.Length == 0)
            {
                parts.Add("s");
                return;
            }

            for (int offset = 0; offset < encoded.Length; offset += chunkLength)
            {
                parts.Add((offset == 0 ? "s" : "c") +
                    encoded.ToString(offset, Math.Min(chunkLength, encoded.Length - offset)));
            }
        }

        private static void WarnInvalidCustomDirectoryOnce(ILogger logger, string configuredDir, string? errorReason)
        {
            var previous = Interlocked.Exchange(ref _lastWarnedInvalidCustomDir, configuredDir);
            if (!string.Equals(previous, configuredDir, StringComparison.Ordinal))
            {
                Common.LogHelper.Warn(
                    logger,
                    $"CustomBackupDirectory '{configuredDir}' is invalid ({errorReason ?? "unknown reason"}), falling back to STRM sidecar directory.");
            }
        }
    }
}
