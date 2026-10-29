using System;
using System.Threading;
using System.Threading.Tasks;

namespace StrmTool.Common
{
    /// <summary>
    /// 进程内统一的“远程探测”并发上限。
    /// 事件处理与批量提取任务在真正发起远程 probe 前共享该信号量，
    /// 纯本地操作（JSON 恢复/导出、目录扫描）不受它限制，避免批量任务把事件处理饿死。
    /// 并发上限按当前配置动态调整：配置保存后新的等待者立即使用新容量。
    /// </summary>
    public static class ProcessingConcurrencyLimiter
    {
        private static readonly object Sync = new object();
        private static SemaphoreSlim _limiter;
        private static int _capacity;

        static ProcessingConcurrencyLimiter()
        {
            _capacity = GetConfiguredMaxConcurrency();
            _limiter = CreateLimiter(_capacity);
        }

        /// <summary>
        /// 当前配置的最大并发数（供批量任务决定 worker 数量）
        /// </summary>
        public static int MaxConcurrency => GetConfiguredMaxConcurrency();

        /// <summary>
        /// 获取一个并发槽。调用方必须在 finally 中把返回的信号量传回 <see cref="Release"/>，
        /// 这样配置变更导致信号量实例被替换时仍能正确释放到原实例。
        /// </summary>
        public static async Task<SemaphoreSlim> AcquireAsync(CancellationToken cancellationToken)
        {
            var limiter = GetCurrentLimiter();
            await limiter.WaitAsync(cancellationToken).ConfigureAwait(false);
            return limiter;
        }

        public static void Release(SemaphoreSlim limiter)
        {
            limiter.Release();
        }

        private static SemaphoreSlim GetCurrentLimiter()
        {
            var configured = GetConfiguredMaxConcurrency();
            lock (Sync)
            {
                if (configured != _capacity)
                {
                    // 旧等待者仍会在旧实例上完成，新请求使用新容量（过渡期短暂超出新上限是可接受的）
                    _limiter = CreateLimiter(configured);
                    _capacity = configured;
                }

                return _limiter;
            }
        }

        private static int GetConfiguredMaxConcurrency()
        {
            return Math.Clamp(Plugin.GetSafeConfiguration().MaxConcurrency, 1, 10);
        }

        private static SemaphoreSlim CreateLimiter(int capacity)
        {
            return new SemaphoreSlim(capacity, capacity);
        }
    }
}
