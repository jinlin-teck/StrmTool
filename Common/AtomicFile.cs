using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace StrmTool.Common
{
    /// <summary>
    /// 原子文件写入：先写同目录临时文件，再用重命名替换目标文件，
    /// 避免并发读取方观察到半截内容。
    /// </summary>
    internal static class AtomicFile
    {
        /// <summary>
        /// 原子写入文本文件。
        /// reuseTempFile 为 true 时使用固定临时文件名（调用方需已按路径串行化），
        /// 使崩溃遗留的临时文件在下一次写入时被复用/覆盖，而不是在媒体目录里累积。
        /// 固定临时名要求同一目标文件只由单个进程写入；多实例共享同一媒体目录时不可复用该模式。
        /// </summary>
        public static async Task WriteAllTextAsync(
            string path, string contents, CancellationToken cancellationToken, bool reuseTempFile = false)
        {
            var tempPath = BuildTempPath(path, reuseTempFile);
            try
            {
                await File.WriteAllTextAsync(tempPath, contents, cancellationToken).ConfigureAwait(false);
                Replace(tempPath, path);
            }
            catch
            {
                TryDelete(tempPath);
                throw;
            }
        }

        public static async Task WriteAllBytesAsync(string path, byte[] contents, CancellationToken cancellationToken)
        {
            var tempPath = BuildTempPath(path, reuseTempFile: false);
            try
            {
                await File.WriteAllBytesAsync(tempPath, contents, cancellationToken).ConfigureAwait(false);
                Replace(tempPath, path);
            }
            catch
            {
                TryDelete(tempPath);
                throw;
            }
        }

        public static void TryDelete(string? path)
        {
            if (string.IsNullOrEmpty(path))
            {
                return;
            }

            try
            {
                if (File.Exists(path))
                {
                    File.Delete(path);
                }
            }
            catch
            {
                // 清理临时文件失败不影响主流程
            }
        }

        private static void Replace(string sourcePath, string destinationPath)
        {
            if (!File.Exists(destinationPath))
            {
                try
                {
                    File.Move(sourcePath, destinationPath);
                    return;
                }
                catch (IOException) when (File.Exists(destinationPath))
                {
                    // 并发场景下目标文件刚被创建，继续走下方的原子替换分支
                }
            }

            try
            {
                // Windows 与 Unix (.NET Core) 均支持以 null 备份文件进行原子替换
                File.Replace(sourcePath, destinationPath, null);
            }
            catch (PlatformNotSupportedException)
            {
                // 退化为删除后重命名：仅作为不支持 File.Replace 的平台兜底
                File.Delete(destinationPath);
                File.Move(sourcePath, destinationPath);
            }
        }

        private static string BuildTempPath(string path, bool reuseTempFile)
        {
            return reuseTempFile
                ? $"{path}.tmp"
                : $"{path}.{Guid.NewGuid():N}.tmp";
        }
    }
}
