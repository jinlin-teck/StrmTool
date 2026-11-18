using System;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Net;
using MediaBrowser.Model.Logging;
using MediaBrowser.Model.Services;
using StrmTool.Common;

namespace StrmTool.Services
{
    [Route("/StrmTool/Status", "GET", Summary = "Gets StrmTool runtime status")]
    [Authenticated(Roles = "Admin")]
    public class GetStrmToolStatus : IReturn<StrmToolStatusResponse>
    {
    }

    public class StrmToolStatusResponse
    {
        public string PluginVersion { get; set; } = string.Empty;
        public int ScopedStrmCount { get; set; }
        public int QueuedCount { get; set; }
        public int LocalQueueCount { get; set; }
        public int ProbeQueueCount { get; set; }
        public int ActiveProbes { get; set; }
        public int InFlightCount { get; set; }
        public int MaxConcurrency { get; set; }
        public bool UsingCustomBackupDir { get; set; }
        public bool CustomBackupDirValid { get; set; }
        public bool CustomBackupDirFallbackToSidecar { get; set; }
        public string CustomBackupDirError { get; set; } = string.Empty;
    }

    [Authenticated(Roles = "Admin")]
    public class StrmToolApiService : IService
    {
        private readonly ILibraryManager _libraryManager;
        private readonly ILogger _logger;

        public StrmToolApiService(ILibraryManager libraryManager, ILogger logger)
        {
            _libraryManager = libraryManager ?? throw new ArgumentNullException(nameof(libraryManager));
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        }

        public object Get(GetStrmToolStatus request)
        {
            var config = Plugin.GetSafeConfiguration();
            var plugin = Plugin.Instance;
            var metrics = plugin != null
                ? plugin.GetEventQueueMetrics()
                : (QueuedCount: 0, LocalQueueCount: 0, ProbeQueueCount: 0);

            int scopedStrmCount = 0;
            try
            {
                scopedStrmCount = MediaInfoHelper.GetCachedScopedStrmCount(_libraryManager, config);
            }
            catch (Exception ex)
            {
                Common.LogHelper.Debug(_logger, $"Failed to count scoped STRM files for status API: {ex.Message}");
            }

            bool usingCustomBackupDir = !string.IsNullOrWhiteSpace(config.CustomBackupDirectory);
            bool customBackupDirValid = true;
            bool customBackupDirFallbackToSidecar = false;
            string? customBackupDirError = null;
            if (usingCustomBackupDir)
            {
                // 语法不合法时后端会回退同目录模式；目录存在性/可写性异常则不静默回退，避免误写入只读媒体目录
                customBackupDirValid = BackupPathResolver.TryValidateCustomBackupDirectory(
                    config.CustomBackupDirectory,
                    out var normalizedBackupRoot,
                    out customBackupDirError);

                if (!customBackupDirValid)
                {
                    customBackupDirFallbackToSidecar = true;
                }
                else
                {
                    customBackupDirValid = BackupPathResolver.TryProbeCustomBackupDirectoryWritable(
                        normalizedBackupRoot,
                        out customBackupDirError);
                }
            }

            var version = plugin?.Version?.ToString(3) ??
                          typeof(StrmToolApiService).Assembly.GetName().Version?.ToString(3) ??
                          string.Empty;

            return new StrmToolStatusResponse
            {
                PluginVersion = version,
                ScopedStrmCount = scopedStrmCount,
                QueuedCount = metrics.QueuedCount,
                LocalQueueCount = metrics.LocalQueueCount,
                ProbeQueueCount = metrics.ProbeQueueCount,
                ActiveProbes = ProcessingConcurrencyLimiter.ActiveCount,
                InFlightCount = StrmFileProcessor.InFlightCount,
                MaxConcurrency = ProcessingConcurrencyLimiter.MaxConcurrency,
                UsingCustomBackupDir = usingCustomBackupDir,
                CustomBackupDirValid = customBackupDirValid,
                CustomBackupDirFallbackToSidecar = customBackupDirFallbackToSidecar,
                CustomBackupDirError = customBackupDirError ?? string.Empty
            };
        }
    }
}
