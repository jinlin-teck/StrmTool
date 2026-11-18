define([], function () {
    "use strict";

    // 必须与 CommonConfiguration.PluginId 保持一致
    var pluginId = "a1b2c3d4-e5f6-7890-abcd-ef1234567891";
    var LANG_STORAGE_KEY = "strmtool_ui_lang";

    var DEFAULTS = {
        EnableAutoExtract: true,
        ProcessingDelayMs: 2000,
        ProcessingDelayJitterMs: 0,
        MaxConcurrency: 3,
        EnableAllLibraries: true,
        EnabledLibraryIds: [],
        ExtractVideoChapters: true,
        IncludeExtraVideos: true,
        EnableMusicMetadata: true,
        EnableExternalLyrics: true,
        CustomBackupDirectory: "",
        EnableDebugLogging: false
    };

    var PRESETS = {
        strict: {
            MaxConcurrency: 1,
            ProcessingDelayMs: 5000,
            ProcessingDelayJitterMs: 2000
        },
        balanced: {
            MaxConcurrency: 3,
            ProcessingDelayMs: 2000,
            ProcessingDelayJitterMs: 500
        },
        fast: {
            MaxConcurrency: 6,
            ProcessingDelayMs: 200,
            ProcessingDelayJitterMs: 0
        }
    };

    var I18N = {
        zh: {
            langToggleLabel: "English",
            heroSubtitle: "STRM 媒体信息预提取、JSON 本地备份恢复、音乐标签补全与外挂歌词增强工具。",
            statusAutoOn: "自动提取运行中",
            statusAutoOff: "自动提取已暂停",
            overviewTitle: "运行状态与计划任务",
            overviewDesc: "实时查看库内 STRM 处理队列、当前并发负载，并可在此直接启动或停止维护任务。",
            btnRefresh: "刷新状态",
            metricScopedStrm: "生效范围 STRM",
            metricScopedSub: "当前过滤规则匹配数",
            metricQueue: "事件队列排队",
            metricQueueSubTpl: "快车道 {local} · 慢车道 {probe}",
            metricProbes: "活跃探测 / 并发上限",
            metricConcurrencySubTpl: "在途任务 {inflight} · 并发上限 {max}",
            metricBackupMode: "备份存储模式",
            metricModeSidecar: "同目录模式",
            metricModeSidecarSub: "保存于 .strm 同目录",
            metricModeCustom: "集中目录模式",
            metricModeCustomValidSub: "路径编码映射已启用",
            metricModeCustomInvalidSub: "路径无效，已回退同目录",
            metricModeCustomUnavailableSub: "目录不可用，写入将失败",
            metricUnavailable: "暂不可用",
            taskExtractName: "提取 Strm 媒体信息",
            taskExportName: "导出 STRM 媒体信息",
            taskRestoreName: "恢复 STRM 媒体信息",
            taskStateIdle: "空闲",
            taskStateRunning: "运行中",
            taskStateCancelling: "停止中",
            taskActionRun: "立即运行",
            taskActionStop: "停止任务",
            taskActionNotFound: "未注册任务",
            taskLastNever: "尚未运行过",
            taskLastPrefix: "上次: ",
            taskResultCompleted: "已完成",
            taskResultFailed: "失败",
            taskResultCancelled: "已取消",
            taskResultAborted: "已中止",
            coreTitle: "核心提取与防风控节流",
            coreDesc: "控制新入库自动处理与远程探测节奏；命中本地 JSON 备份时始终零延迟恢复。",
            enableAutoExtractLabel: "启用新入库自动提取",
            enableAutoExtractDesc: "新 STRM 文件入库时自动在后台触发媒体信息恢复或远程探测。",
            presetTitle: "场景预设档位（点击快速填充延迟与并发）",
            presetBadgeCustom: "自定义参数",
            presetBadgeStrict: "当前：严格防风控",
            presetBadgeBalanced: "当前：均衡推荐",
            presetBadgeFast: "当前：局域网高速",
            presetStrictTitle: "🛡️ 严格防风控",
            presetStrictParams: "1 并发 · 5000ms + 2000ms 抖动",
            presetStrictDesc: "适合 115/阿里云盘等限流敏感网盘",
            presetBalancedTitle: "⚖️ 均衡推荐",
            presetBalancedParams: "3 并发 · 2000ms + 500ms 抖动",
            presetBalancedDesc: "兼顾起播准备速度与网盘稳定性",
            presetFastTitle: "⚡ 局域网高速",
            presetFastParams: "6 并发 · 200ms + 0ms 抖动",
            presetFastDesc: "适合自建 WebDAV / NAS 内网源",
            delayLabel: "基础处理延迟（毫秒）",
            delayDesc: "每次远程探测前的固定等待，范围 0 - 60000。",
            jitterLabel: "随机延迟抖动（毫秒）",
            jitterDesc: "额外叠加 [0, 抖动] 随机毫秒打散请求规律，范围 0 - 30000。",
            concurrencyLabel: "最大并发数",
            concurrencyDesc: "同时处理的任务与远程探测上限，范围 1 - 10。",
            delayPreviewZero: "每次远程探测前等待：0 ms（无延迟；命中本地 JSON 同样零延迟）。",
            delayPreviewFixed: "每次远程探测前等待：固定 {min} ms（纯本地 JSON 恢复与导出零延迟）。",
            delayPreviewRange: "每次远程探测前等待：{min} ~ {max} ms 随机区间（纯本地 JSON 恢复零延迟）。",
            libraryScopeTitle: "媒体库生效范围",
            libraryScopeDesc: "同时作用于新入库事件与所有计划任务，可用于排除高风控或无需探测的特定媒体库。",
            enableAllLibrariesLabel: "对所有媒体库生效",
            enableAllLibrariesDesc: "关闭后仅处理下方勾选的媒体库中的 STRM 文件。",
            btnSelectAll: "全选",
            btnClearAll: "清空",
            noLibraryWarning: "当前未勾选任何媒体库：入库自动提取与计划任务将暂时不会处理任何 STRM 文件。",
            selectedLibSummaryTpl: "已选择 {selected} / {total} 个媒体库",
            libraryLoadEmpty: "未检测到媒体库或暂无权限读取。",
            libraryLoadError: "读取媒体库列表失败，请点击“刷新状态”重试。",
            libraryPathEmpty: "未配置路径",
            featuresStorageTitle: "媒体特性、备份存储与排障",
            featuresStorageDesc: "按需开关特定媒体类型的处理行为、配置集中备份目录或开启独立调试日志。",
            extractChaptersLabel: "提取视频章节信息",
            extractChaptersDesc: "远程探测视频时同时读取并保存章节信息（库内已有章节时不覆盖）。",
            includeExtrasLabel: "处理预告片与附加视频",
            includeExtrasDesc: "包含 Trailer、MusicVideo 及 extras 等附加片段；关闭后仅处理正片与音频。",
            enableMusicMetaLabel: "音乐标签与专辑歌手补全",
            enableMusicMetaDesc: "为音乐库 STRM 提取内嵌音频标签并结合目录结构补全专辑、歌手与封面。",
            enableLyricsLabel: "自动关联同目录外挂歌词",
            enableLyricsDesc: "自动扫描并合并 STRM 同目录下的同名 .lrc / .elrc 歌词流。",
            customBackupDirLabel: "自定义集中备份目录（留空则保存在 .strm 同目录）",
            customBackupDirPlaceholder: "/config/strmtool-backups",
            customBackupDirDesc: "适合媒体盘只读挂载场景。启用后按原目录结构编码映射存放 -mediainfo.json 与音乐 .metadata 封面；读取时仍自动兼容同目录历史旧备份。",
            btnBrowse: "浏览目录",
            btnClearPath: "清空",
            backupNoticeDefault: "当前模式：经典同目录模式，-mediainfo.json 与音频封面保存在 .strm 所在目录。",
            backupNoticeCustomOk: "集中备份已启用：新备份与导出文件将按原路径编码映射保存至该目录；读取时若集中目录尚无文件，仍会自动兼容读取 .strm 同目录旧备份。如需批量补全至集中目录，保存后运行一次“导出 STRM 媒体信息”即可。",
            backupNoticeErrRelative: "路径无效：自定义集中备份目录必须是绝对路径（例如 /config/strmtool-backups 或 D:\\StrmBackups）。",
            backupNoticeErrRoot: "路径无效：不能直接使用文件系统根目录（如 / 或 C:\\）作为集中备份目录，请指定子目录。",
            backupNoticeErrServerTpl: "服务端路径格式校验未通过（已自动回退同目录模式）：{reason}",
            backupNoticeErrUnwritableTpl: "集中备份目录当前不可用（不会回退同目录，导出/迁移将失败）：{reason}",
            backupNoticeBrowserMissing: "当前 Emby 客户端环境不支持目录浏览器，请直接手动输入绝对路径。",
            backupPickerHeader: "选择集中备份根目录",
            enableDebugLogLabel: "启用插件独立调试日志",
            enableDebugLogDesc: "开启后无需打开 Emby 全局调试日志，即可在 embyserver.txt 中输出 StrmTool [Debug] 详细排障信息。",
            statusSynced: "所有更改已保存",
            statusDirty: "有未保存的更改（点击保存后立即生效）",
            btnResetDefaults: "恢复默认设置",
            btnSave: "保存设置",
            saveErrorMsg: "保存设置失败，请检查网络连接或 Emby 服务日志后重试。",
            colTypeMovies: "电影",
            colTypeTvshows: "剧集",
            colTypeMusic: "音乐",
            colTypeMusicvideos: "MV",
            colTypeHomevideos: "家庭视频",
            colTypeBoxsets: "合集",
            colTypeMixed: "混合内容"
        },
        en: {
            langToggleLabel: "中文",
            heroSubtitle: "STRM media info pre-extraction, JSON backup/restore, music tag enrichment and external lyrics helper.",
            statusAutoOn: "Auto-Extract Active",
            statusAutoOff: "Auto-Extract Paused",
            overviewTitle: "Runtime Status & Scheduled Tasks",
            overviewDesc: "Monitor live STRM queues and probing concurrency, and trigger maintenance tasks directly from here.",
            btnRefresh: "Refresh",
            metricScopedStrm: "Scoped STRM Items",
            metricScopedSub: "Matched by current scope",
            metricQueue: "Event Queue",
            metricQueueSubTpl: "Local {local} · Probe {probe}",
            metricProbes: "Active Probes / Limit",
            metricConcurrencySubTpl: "In-Flight {inflight} · Limit {max}",
            metricBackupMode: "Backup Mode",
            metricModeSidecar: "Sidecar Mode",
            metricModeSidecarSub: "Stored next to .strm",
            metricModeCustom: "Centralized Dir",
            metricModeCustomValidSub: "Encoded path mapping enabled",
            metricModeCustomInvalidSub: "Invalid path, fell back",
            metricModeCustomUnavailableSub: "Directory unavailable",
            metricUnavailable: "Unavailable",
            taskExtractName: "Strm File Media Info Extraction",
            taskExportName: "Export Strm File Media Info",
            taskRestoreName: "Restore STRM Media Info",
            taskStateIdle: "Idle",
            taskStateRunning: "Running",
            taskStateCancelling: "Stopping",
            taskActionRun: "Run Now",
            taskActionStop: "Stop Task",
            taskActionNotFound: "Not Found",
            taskLastNever: "Never run yet",
            taskLastPrefix: "Last: ",
            taskResultCompleted: "Completed",
            taskResultFailed: "Failed",
            taskResultCancelled: "Cancelled",
            taskResultAborted: "Aborted",
            coreTitle: "Core Extraction & Anti-Throttle",
            coreDesc: "Control new item auto-processing and remote probing pace; local JSON restores always run with zero delay.",
            enableAutoExtractLabel: "Enable Auto-Extract on Library Scan",
            enableAutoExtractDesc: "Automatically restore or probe media info in the background when new STRM files are added.",
            presetTitle: "Throttle Presets (one-click delay & concurrency profiles)",
            presetBadgeCustom: "Custom",
            presetBadgeStrict: "Active: Strict Anti-Throttle",
            presetBadgeBalanced: "Active: Balanced",
            presetBadgeFast: "Active: LAN High Speed",
            presetStrictTitle: "🛡️ Strict Anti-Throttle",
            presetStrictParams: "1 Worker · 5000ms + 2000ms Jitter",
            presetStrictDesc: "Best for rate-limited cloud drives",
            presetBalancedTitle: "⚖️ Balanced",
            presetBalancedParams: "3 Workers · 2000ms + 500ms Jitter",
            presetBalancedDesc: "Balanced startup speed & API safety",
            presetFastTitle: "⚡ LAN High Speed",
            presetFastParams: "6 Workers · 200ms + 0ms Jitter",
            presetFastDesc: "For self-hosted WebDAV / local NAS",
            delayLabel: "Base Processing Delay (ms)",
            delayDesc: "Fixed wait before each remote probe, range 0 - 60000.",
            jitterLabel: "Random Delay Jitter (ms)",
            jitterDesc: "Adds [0, jitter] random ms to break fixed request intervals, range 0 - 30000.",
            concurrencyLabel: "Max Concurrency",
            concurrencyDesc: "Max parallel tasks and remote media probes, range 1 - 10.",
            delayPreviewZero: "Delay before remote probe: 0 ms (no delay; local JSON restore is always instant).",
            delayPreviewFixed: "Delay before remote probe: fixed {min} ms (local JSON restore & export have zero delay).",
            delayPreviewRange: "Delay before remote probe: {min} ~ {max} ms random window (local JSON restore is instant).",
            libraryScopeTitle: "Library Scope Filter",
            libraryScopeDesc: "Applies to both new-item events and scheduled tasks. Exclude rate-limited or unneeded libraries.",
            enableAllLibrariesLabel: "Apply to All Libraries",
            enableAllLibrariesDesc: "When disabled, only STRM files inside the checked libraries below are processed.",
            btnSelectAll: "Select All",
            btnClearAll: "Clear",
            noLibraryWarning: "No library is currently selected: auto-extract and scheduled tasks will skip all STRM files.",
            selectedLibSummaryTpl: "{selected} / {total} libraries selected",
            libraryLoadEmpty: "No libraries detected or insufficient permissions.",
            libraryLoadError: "Failed to load libraries. Click Refresh to retry.",
            libraryPathEmpty: "No paths configured",
            featuresStorageTitle: "Features, Backup Storage & Diagnostics",
            featuresStorageDesc: "Toggle specific media handlers, configure a centralized backup folder, or enable plugin debug logs.",
            extractChaptersLabel: "Extract Video Chapters",
            extractChaptersDesc: "Probe and store video chapters when missing in the Emby database.",
            includeExtrasLabel: "Process Trailers & Extra Videos",
            includeExtrasDesc: "Include Trailer, MusicVideo and extras; disable to process main features & audio only.",
            enableMusicMetaLabel: "Music Tag & Artist/Album Enrichment",
            enableMusicMetaDesc: "Probe embedded audio tags and resolve fallback Album/Artist metadata & covers for music STRMs.",
            enableLyricsLabel: "Auto-Merge Sidecar Lyrics",
            enableLyricsDesc: "Scan and attach matching .lrc / .elrc lyric files alongside music STRM items.",
            customBackupDirLabel: "Custom Centralized Backup Directory (leave empty for sidecar mode)",
            customBackupDirPlaceholder: "/config/strmtool-backups",
            customBackupDirDesc: "Ideal for read-only media mounts. Uses encoded path mapping for -mediainfo.json & .metadata covers while preserving folder hierarchy and falling back to existing sidecar backups on read.",
            btnBrowse: "Browse",
            btnClearPath: "Clear",
            backupNoticeDefault: "Mode: Classic Sidecar — -mediainfo.json and audio covers are saved alongside each .strm file.",
            backupNoticeCustomOk: "Centralized backup active: new backups & exports use encoded path mapping under this directory, with automatic fallback to existing sidecar JSONs. Run the Export task after saving to populate this folder.",
            backupNoticeErrRelative: "Invalid path: Custom backup directory must be an absolute path (e.g. /config/strmtool-backups or D:\\StrmBackups).",
            backupNoticeErrRoot: "Invalid path: Cannot use a filesystem root (such as / or C:\\) directly. Choose a subdirectory.",
            backupNoticeErrServerTpl: "Server rejected path format (fell back to sidecar mode): {reason}",
            backupNoticeErrUnwritableTpl: "Centralized backup directory is unavailable (no sidecar fallback; exports will fail): {reason}",
            backupNoticeBrowserMissing: "Directory browser is unavailable in this client. Please type an absolute path manually.",
            backupPickerHeader: "Select Centralized Backup Root",
            enableDebugLogLabel: "Enable Plugin Debug Logging",
            enableDebugLogDesc: "Emit StrmTool [Debug] trace lines into embyserver.txt without turning on Emby's global debug mode.",
            statusSynced: "All changes saved",
            statusDirty: "Unsaved changes (click Save to apply immediately)",
            btnResetDefaults: "Reset Defaults",
            btnSave: "Save Settings",
            saveErrorMsg: "Failed to save settings. Please check your connection or Emby server logs.",
            colTypeMovies: "Movies",
            colTypeTvshows: "TV Shows",
            colTypeMusic: "Music",
            colTypeMusicvideos: "Music Videos",
            colTypeHomevideos: "Home Videos",
            colTypeBoxsets: "Collections",
            colTypeMixed: "Mixed"
        }
    };

    function detectInitialLanguage() {
        try {
            var saved = window.localStorage && window.localStorage.getItem(LANG_STORAGE_KEY);
            if (saved === "zh" || saved === "en") {
                return saved;
            }
        } catch (err) {
            // 忽略隐私模式 localStorage 异常
        }

        var browserLang = (navigator.language || navigator.userLanguage || "").toLowerCase();
        return browserLang.indexOf("zh") === 0 ? "zh" : "en";
    }

    function clamp(value, min, max, fallbackValue) {
        if (isNaN(value)) {
            return fallbackValue;
        }

        return Math.max(min, Math.min(max, value));
    }

    function formatTemplate(template, params) {
        var output = String(template || "");
        if (!params) {
            return output;
        }

        Object.keys(params).forEach(function (key) {
            output = output.split("{" + key + "}").join(String(params[key]));
        });
        return output;
    }

    function normalizePathValidation(rawPath) {
        var trimmed = String(rawPath || "").trim();
        if (!trimmed) {
            return { isEmpty: true, isValid: true, errorKey: null };
        }

        var isUnixAbs = trimmed.charAt(0) === "/";
        var isUnc = trimmed.indexOf("\\\\") === 0 || trimmed.indexOf("//") === 0;
        var isWinDriveAbs = /^[A-Za-z]:[\\/]/.test(trimmed);
        if (!isUnixAbs && !isUnc && !isWinDriveAbs) {
            return { isEmpty: false, isValid: false, errorKey: "backupNoticeErrRelative" };
        }

        if (/^\/+$/.test(trimmed) || /^[A-Za-z]:[\\/]*$/.test(trimmed) || /^(?:\\\\|\/\/)[^\\/]+(?:[\\/]+[^\\/]+)?[\\/]*$/.test(trimmed)) {
            return { isEmpty: false, isValid: false, errorKey: "backupNoticeErrRoot" };
        }

        return { isEmpty: false, isValid: true, errorKey: null };
    }

    function getVirtualFolderPrimaryId(vf) {
        if (!vf) {
            return "";
        }

        // 优先持久化 Guid：InternalId 在库数据库重建后可能变化甚至被新库复用，
        // 会导致作用域静默失效或指向错误的媒体库。
        return String(vf.Guid || vf.ItemId || vf.Id || "").trim();
    }

    function getVirtualFolderCandidateIds(vf) {
        var ids = [];
        if (!vf) {
            return ids;
        }

        [vf.ItemId, vf.Id, vf.Guid].forEach(function (val) {
            if (val != null) {
                var str = String(val).trim();
                if (str) {
                    ids.push(str.toLowerCase());
                    ids.push(str.replace(/-/g, "").toLowerCase());
                }
            }
        });
        return ids;
    }

    function buildIdLookupSet(idList) {
        var set = {};
        (idList || []).forEach(function (id) {
            var str = String(id || "").trim();
            if (str) {
                set[str.toLowerCase()] = true;
                set[str.replace(/-/g, "").toLowerCase()] = true;
            }
        });
        return set;
    }

    function normalizeVirtualFoldersResponse(data) {
        if (Array.isArray(data)) {
            return data;
        }

        if (data && Array.isArray(data.Items)) {
            return data.Items;
        }

        return [];
    }

    function getVirtualFoldersApi() {
        if (typeof ApiClient.getVirtualFolders === "function") {
            return ApiClient.getVirtualFolders().then(normalizeVirtualFoldersResponse);
        }

        return ApiClient.getJSON(ApiClient.getUrl("Library/VirtualFolders")).then(normalizeVirtualFoldersResponse);
    }

    function getScheduledTasksApi() {
        if (typeof ApiClient.getScheduledTasks === "function") {
            return ApiClient.getScheduledTasks();
        }

        return ApiClient.getJSON(ApiClient.getUrl("ScheduledTasks"));
    }

    function startScheduledTaskApi(taskId) {
        if (typeof ApiClient.startScheduledTask === "function") {
            return ApiClient.startScheduledTask(taskId);
        }

        return ApiClient.ajax({
            type: "POST",
            url: ApiClient.getUrl("ScheduledTasks/Running/" + encodeURIComponent(taskId))
        });
    }

    function stopScheduledTaskApi(taskId) {
        if (typeof ApiClient.stopScheduledTask === "function") {
            return ApiClient.stopScheduledTask(taskId);
        }

        return ApiClient.ajax({
            type: "DELETE",
            url: ApiClient.getUrl("ScheduledTasks/Running/" + encodeURIComponent(taskId))
        });
    }

    function getPluginStatusApi() {
        return ApiClient.getJSON(ApiClient.getUrl("StrmTool/Status"));
    }

    return function (view) {
        var state = {
            lang: detectInitialLanguage(),
            submitting: false,
            isViewActive: true,
            needsReloadOnShow: false,
            pollTimer: null,
            refreshSeq: 0,
            refreshPromise: null,
            virtualFolders: [],
            virtualFoldersLoaded: false,
            virtualFoldersError: false,
            virtualFoldersSeq: 0,
            savedLibraryIdsFallback: [],
            savedCustomBackupDir: "",
            lastTasks: [],
            lastStatus: null,
            baselineSnapshot: ""
        };

        function t(key) {
            var dict = I18N[state.lang] || I18N.zh;
            return dict[key] != null ? dict[key] : (I18N.zh[key] || key);
        }

        function setInputBool(selector, value) {
            var el = view.querySelector(selector);
            if (el) {
                el.checked = !!value;
            }
        }

        function setInputValue(selector, value) {
            var el = view.querySelector(selector);
            if (el) {
                el.value = value;
            }
        }

        function getBool(selector, fallbackValue) {
            var el = view.querySelector(selector);
            return el ? !!el.checked : fallbackValue;
        }

        function getInt(selector, min, max, fallbackValue) {
            var el = view.querySelector(selector);
            if (!el) {
                return fallbackValue;
            }

            return clamp(parseInt(el.value, 10), min, max, fallbackValue);
        }

        function getSelectedLibraryIds() {
            if (!state.virtualFoldersLoaded) {
                return state.savedLibraryIdsFallback.slice();
            }

            var checkboxes = view.querySelectorAll("#LibraryListContainer input[type='checkbox'][data-lib-id]");
            var ids = [];
            for (var i = 0; i < checkboxes.length; i++) {
                if (checkboxes[i].checked) {
                    var id = checkboxes[i].getAttribute("data-lib-id");
                    if (id) {
                        ids.push(id);
                    }
                }
            }
            return ids;
        }

        function readFormConfig() {
            var backupInput = view.querySelector("#CustomBackupDirectory");
            var customDir = backupInput ? String(backupInput.value || "").trim() : "";

            return {
                EnableAutoExtract: getBool("#EnableAutoExtract", DEFAULTS.EnableAutoExtract),
                ProcessingDelayMs: getInt("#ProcessingDelayMs", 0, 60000, DEFAULTS.ProcessingDelayMs),
                ProcessingDelayJitterMs: getInt("#ProcessingDelayJitterMs", 0, 30000, DEFAULTS.ProcessingDelayJitterMs),
                MaxConcurrency: getInt("#MaxConcurrency", 1, 10, DEFAULTS.MaxConcurrency),
                EnableAllLibraries: getBool("#EnableAllLibraries", DEFAULTS.EnableAllLibraries),
                EnabledLibraryIds: getSelectedLibraryIds(),
                ExtractVideoChapters: getBool("#ExtractVideoChapters", DEFAULTS.ExtractVideoChapters),
                IncludeExtraVideos: getBool("#IncludeExtraVideos", DEFAULTS.IncludeExtraVideos),
                EnableMusicMetadata: getBool("#EnableMusicMetadata", DEFAULTS.EnableMusicMetadata),
                EnableExternalLyrics: getBool("#EnableExternalLyrics", DEFAULTS.EnableExternalLyrics),
                CustomBackupDirectory: customDir,
                EnableDebugLogging: getBool("#EnableDebugLogging", DEFAULTS.EnableDebugLogging)
            };
        }

        function normalizeSnapshot(cfg) {
            var sortedLibs = (cfg.EnabledLibraryIds || []).slice().sort();
            return JSON.stringify({
                EnableAutoExtract: !!cfg.EnableAutoExtract,
                ProcessingDelayMs: Number(cfg.ProcessingDelayMs),
                ProcessingDelayJitterMs: Number(cfg.ProcessingDelayJitterMs),
                MaxConcurrency: Number(cfg.MaxConcurrency),
                EnableAllLibraries: !!cfg.EnableAllLibraries,
                EnabledLibraryIds: sortedLibs,
                ExtractVideoChapters: !!cfg.ExtractVideoChapters,
                IncludeExtraVideos: !!cfg.IncludeExtraVideos,
                EnableMusicMetadata: !!cfg.EnableMusicMetadata,
                EnableExternalLyrics: !!cfg.EnableExternalLyrics,
                CustomBackupDirectory: String(cfg.CustomBackupDirectory || "").trim(),
                EnableDebugLogging: !!cfg.EnableDebugLogging
            });
        }

        function isFormDirty() {
            return !!state.baselineSnapshot && normalizeSnapshot(readFormConfig()) !== state.baselineSnapshot;
        }

        function updateDirtyState() {
            var badge = view.querySelector("#UnsavedChangesBadge");
            var textEl = view.querySelector("#UnsavedChangesText");
            if (!badge || !textEl || !state.baselineSnapshot) {
                return;
            }

            var isDirty = isFormDirty();
            badge.classList.toggle("is-dirty", isDirty);
            textEl.textContent = isDirty ? t("statusDirty") : t("statusSynced");
        }

        function updateAutoExtractStatusBadge() {
            var badge = view.querySelector("#StrmToolAutoStatusBadge");
            if (!badge) {
                return;
            }

            var enabled = getBool("#EnableAutoExtract", true);
            badge.classList.toggle("is-paused", !enabled);
            var span = badge.querySelector("[data-i18n]");
            if (span) {
                span.setAttribute("data-i18n", enabled ? "statusAutoOn" : "statusAutoOff");
                span.textContent = enabled ? t("statusAutoOn") : t("statusAutoOff");
            }
        }

        function updatePresetAndDelayPreview() {
            var delay = getInt("#ProcessingDelayMs", 0, 60000, DEFAULTS.ProcessingDelayMs);
            var jitter = getInt("#ProcessingDelayJitterMs", 0, 30000, DEFAULTS.ProcessingDelayJitterMs);
            var concurrency = getInt("#MaxConcurrency", 1, 10, DEFAULTS.MaxConcurrency);

            var matchedPreset = null;
            Object.keys(PRESETS).forEach(function (key) {
                var p = PRESETS[key];
                if (p.MaxConcurrency === concurrency &&
                    p.ProcessingDelayMs === delay &&
                    p.ProcessingDelayJitterMs === jitter) {
                    matchedPreset = key;
                }
            });

            var cards = view.querySelectorAll(".strm-preset-card[data-preset]");
            for (var i = 0; i < cards.length; i++) {
                var presetName = cards[i].getAttribute("data-preset");
                cards[i].classList.toggle("is-active", presetName === matchedPreset);
            }

            var activeLabel = view.querySelector("#PresetActiveLabel");
            if (activeLabel) {
                var badgeKey = matchedPreset === "strict"
                    ? "presetBadgeStrict"
                    : (matchedPreset === "balanced"
                        ? "presetBadgeBalanced"
                        : (matchedPreset === "fast" ? "presetBadgeFast" : "presetBadgeCustom"));
                activeLabel.textContent = t(badgeKey);
                activeLabel.classList.toggle("is-running", !!matchedPreset);
            }

            var previewText = view.querySelector("#DelayPreviewText");
            if (previewText) {
                if (delay === 0 && jitter === 0) {
                    previewText.textContent = t("delayPreviewZero");
                } else if (jitter === 0) {
                    previewText.textContent = formatTemplate(t("delayPreviewFixed"), { min: delay });
                } else {
                    previewText.textContent = formatTemplate(t("delayPreviewRange"), {
                        min: delay,
                        max: delay + jitter
                    });
                }
            }
        }

        function updateBackupDirPreview() {
            var input = view.querySelector("#CustomBackupDirectory");
            var notice = view.querySelector("#BackupDirNotice");
            if (!input || !notice) {
                return;
            }

            var result = normalizePathValidation(input.value);
            if (!result.isValid && result.errorKey) {
                input.setCustomValidity(t(result.errorKey));
                notice.className = "strm-notice-error";
                notice.textContent = t(result.errorKey);
                return;
            }

            input.setCustomValidity("");

            var trimmedInput = String(input.value || "").trim();
            if (!result.isEmpty &&
                trimmedInput === state.savedCustomBackupDir &&
                state.lastStatus &&
                state.lastStatus.UsingCustomBackupDir &&
                !state.lastStatus.CustomBackupDirValid) {
                notice.className = "strm-notice-error";
                var errTpl = state.lastStatus.CustomBackupDirFallbackToSidecar
                    ? "backupNoticeErrServerTpl"
                    : "backupNoticeErrUnwritableTpl";
                notice.textContent = formatTemplate(t(errTpl), {
                    reason: state.lastStatus.CustomBackupDirError || t("metricModeCustomUnavailableSub")
                });
                return;
            }

            notice.className = "strm-notice-info";
            notice.textContent = result.isEmpty ? t("backupNoticeDefault") : t("backupNoticeCustomOk");
        }

        function translateCollectionType(collectionType) {
            var map = {
                movies: "colTypeMovies",
                tvshows: "colTypeTvshows",
                music: "colTypeMusic",
                musicvideos: "colTypeMusicvideos",
                homevideos: "colTypeHomevideos",
                boxsets: "colTypeBoxsets"
            };
            var key = map[String(collectionType || "").toLowerCase()] || "colTypeMixed";
            return t(key);
        }

        function updateLibraryScopeVisibilityAndSummary() {
            var allLibs = getBool("#EnableAllLibraries", true);
            var container = view.querySelector("#LibraryScopeContainer");
            if (container) {
                container.classList.toggle("strm-hidden", allLibs);
            }

            var checkboxes = view.querySelectorAll("#LibraryListContainer input[type='checkbox'][data-lib-id]");
            var total = checkboxes.length;
            var selected = 0;
            for (var i = 0; i < checkboxes.length; i++) {
                var checked = !!checkboxes[i].checked;
                if (checked) {
                    selected++;
                }
                var card = checkboxes[i].closest(".strm-lib-card");
                if (card) {
                    card.classList.toggle("is-selected", checked);
                }
            }

            var summaryEl = view.querySelector("#SelectedLibrarySummary");
            if (summaryEl) {
                if (state.virtualFoldersError) {
                    summaryEl.textContent = t("libraryLoadError");
                } else if (state.virtualFoldersLoaded && total === 0) {
                    summaryEl.textContent = t("libraryLoadEmpty");
                } else {
                    summaryEl.textContent = formatTemplate(t("selectedLibSummaryTpl"), {
                        selected: selected,
                        total: total
                    });
                }
            }

            var warnEl = view.querySelector("#NoLibrarySelectedWarning");
            if (warnEl) {
                warnEl.classList.toggle("strm-hidden", allLibs || !state.virtualFoldersLoaded || total === 0 || selected > 0);
            }
        }

        function renderLibraryList(selectedIds) {
            var listEl = view.querySelector("#LibraryListContainer");
            if (!listEl) {
                return;
            }

            while (listEl.firstChild) {
                listEl.removeChild(listEl.firstChild);
            }

            if (!state.virtualFoldersLoaded || state.virtualFolders.length === 0) {
                updateLibraryScopeVisibilityAndSummary();
                return;
            }

            var lookup = buildIdLookupSet(selectedIds);
            state.virtualFolders.forEach(function (vf) {
                var primaryId = getVirtualFolderPrimaryId(vf);
                if (!primaryId) {
                    return;
                }

                var candidates = getVirtualFolderCandidateIds(vf);
                var isChecked = candidates.some(function (c) {
                    return !!lookup[c];
                });

                var card = document.createElement("div");
                card.className = "strm-lib-card" + (isChecked ? " is-selected" : "");

                var head = document.createElement("div");
                head.className = "strm-lib-card-head";

                var chkLabel = document.createElement("label");
                chkLabel.className = "checkboxContainer";

                var input;
                try {
                    input = document.createElement("input", { is: "emby-checkbox" });
                } catch (err) {
                    input = document.createElement("input");
                }
                input.type = "checkbox";
                input.setAttribute("is", "emby-checkbox");
                input.setAttribute("data-lib-id", primaryId);
                input.checked = isChecked;
                input.addEventListener("change", function () {
                    updateLibraryScopeVisibilityAndSummary();
                    updateDirtyState();
                });

                var nameSpan = document.createElement("span");
                nameSpan.textContent = vf.Name || primaryId;

                chkLabel.appendChild(input);
                chkLabel.appendChild(nameSpan);

                var pill = document.createElement("span");
                pill.className = "strm-lib-type-pill";
                pill.textContent = translateCollectionType(vf.CollectionType);

                head.appendChild(chkLabel);
                head.appendChild(pill);

                var pathDiv = document.createElement("div");
                pathDiv.className = "strm-lib-path";
                var locs = Array.isArray(vf.Locations) ? vf.Locations.filter(Boolean) : [];
                pathDiv.textContent = locs.length > 0 ? locs.join(" · ") : t("libraryPathEmpty");

                card.addEventListener("click", function (e) {
                    if (e.target && e.target.closest && e.target.closest(".checkboxContainer")) {
                        return;
                    }
                    input.checked = !input.checked;
                    input.dispatchEvent(new Event("change", { bubbles: true }));
                });

                card.appendChild(head);
                card.appendChild(pathDiv);
                listEl.appendChild(card);
            });

            updateLibraryScopeVisibilityAndSummary();
        }

        function renderStatusMetrics(status) {
            state.lastStatus = status || null;
            var versionEl = view.querySelector("#StrmToolVersionBadge");
            var scopedEl = view.querySelector("#MetricScopedStrmCount");
            var queueEl = view.querySelector("#MetricQueueCount");
            var queueSubEl = view.querySelector("#MetricQueueSub");
            var concEl = view.querySelector("#MetricConcurrency");
            var concSubEl = view.querySelector("#MetricConcurrencySub");
            var modeEl = view.querySelector("#MetricBackupMode");
            var modeSubEl = view.querySelector("#MetricBackupModeSub");

            if (!status) {
                if (scopedEl) scopedEl.textContent = "--";
                if (queueEl) queueEl.textContent = "--";
                if (queueSubEl) queueSubEl.textContent = t("metricUnavailable");
                if (concEl) concEl.textContent = "--";
                if (concSubEl) concSubEl.textContent = t("metricUnavailable");
                if (modeEl) modeEl.textContent = "--";
                if (modeSubEl) modeSubEl.textContent = t("metricUnavailable");
                updateBackupDirPreview();
                return;
            }

            if (versionEl && status.PluginVersion) {
                versionEl.textContent = "v" + status.PluginVersion;
                versionEl.classList.remove("strm-hidden");
            }
            if (scopedEl) {
                scopedEl.textContent = String(status.ScopedStrmCount != null ? status.ScopedStrmCount : 0);
            }
            if (queueEl) {
                queueEl.textContent = String(status.QueuedCount != null ? status.QueuedCount : 0);
            }
            if (queueSubEl) {
                queueSubEl.textContent = formatTemplate(t("metricQueueSubTpl"), {
                    local: status.LocalQueueCount != null ? status.LocalQueueCount : 0,
                    probe: status.ProbeQueueCount != null ? status.ProbeQueueCount : 0
                });
            }
            if (concEl) {
                var activeProbes = status.ActiveProbes != null ? status.ActiveProbes : 0;
                var maxConc = status.MaxConcurrency != null ? status.MaxConcurrency : 3;
                concEl.textContent = activeProbes + " / " + maxConc;
            }
            if (concSubEl) {
                concSubEl.textContent = formatTemplate(t("metricConcurrencySubTpl"), {
                    inflight: status.InFlightCount != null ? status.InFlightCount : 0,
                    max: status.MaxConcurrency != null ? status.MaxConcurrency : 3
                });
            }
            if (modeEl && modeSubEl) {
                if (status.UsingCustomBackupDir) {
                    modeEl.textContent = t("metricModeCustom");
                    if (status.CustomBackupDirValid) {
                        modeSubEl.textContent = t("metricModeCustomValidSub");
                    } else {
                        modeSubEl.textContent = status.CustomBackupDirFallbackToSidecar
                            ? t("metricModeCustomInvalidSub")
                            : t("metricModeCustomUnavailableSub");
                    }
                } else {
                    modeEl.textContent = t("metricModeSidecar");
                    modeSubEl.textContent = t("metricModeSidecarSub");
                }
            }

            updateBackupDirPreview();
        }

        function formatLastResult(task) {
            if (!task || !task.LastExecutionResult) {
                return t("taskLastNever");
            }

            var res = task.LastExecutionResult;
            var statusMap = {
                Completed: "taskResultCompleted",
                Failed: "taskResultFailed",
                Cancelled: "taskResultCancelled",
                Aborted: "taskResultAborted"
            };
            var statusLabel = t(statusMap[res.Status] || "taskResultCompleted");
            var endTime = res.EndTimeUtc ? new Date(res.EndTimeUtc) : null;
            var timeStr = (endTime && !isNaN(endTime.getTime()))
                ? endTime.toLocaleString()
                : "";
            return timeStr ? (t("taskLastPrefix") + statusLabel + " · " + timeStr) : (t("taskLastPrefix") + statusLabel);
        }

        function renderTasks(tasks) {
            state.lastTasks = Array.isArray(tasks) ? tasks : [];
            var byKey = {};
            state.lastTasks.forEach(function (task) {
                if (task && task.Key) {
                    byKey[task.Key] = task;
                }
            });

            var cards = view.querySelectorAll("#StrmTasksGrid .strm-task-card[data-task-key]");

            for (var i = 0; i < cards.length; i++) {
                var card = cards[i];
                var key = card.getAttribute("data-task-key");
                var task = byKey[key];

                var titleEl = card.querySelector("[data-task-title]");
                var stateEl = card.querySelector("[data-task-state]");
                var lastEl = card.querySelector("[data-task-last]");
                var progressWrap = card.querySelector("[data-task-progress-wrap]");
                var progressBar = card.querySelector("[data-task-progress-bar]");
                var actionBtn = card.querySelector("[data-task-action]");

                if (!task) {
                    card.classList.remove("is-running");
                    if (stateEl) {
                        stateEl.className = "strm-task-pill";
                        stateEl.textContent = "--";
                    }
                    if (lastEl) {
                        lastEl.textContent = t("taskActionNotFound");
                    }
                    if (progressWrap) {
                        progressWrap.classList.add("strm-hidden");
                    }
                    if (actionBtn) {
                        actionBtn.disabled = true;
                        actionBtn.className = "strm-task-btn";
                        actionBtn.removeAttribute("data-task-id");
                        actionBtn.textContent = t("taskActionNotFound");
                    }
                    continue;
                }

                if (titleEl) {
                    var titleI18nKey = titleEl.getAttribute("data-i18n");
                    if (titleI18nKey) {
                        titleEl.textContent = t(titleI18nKey);
                    } else if (task.Name) {
                        titleEl.textContent = task.Name;
                    }
                }

                var taskState = String(task.State || "Idle");
                var isRunning = taskState === "Running";
                var isCancelling = taskState === "Cancelling";

                card.classList.toggle("is-running", isRunning || isCancelling);

                var pct = task.CurrentProgressPercentage != null
                    ? Math.max(0, Math.min(100, Number(task.CurrentProgressPercentage)))
                    : 0;

                if (stateEl) {
                    stateEl.className = "strm-task-pill";
                    if (isRunning) {
                        stateEl.classList.add("is-running");
                        stateEl.textContent = t("taskStateRunning") + " " + Math.round(pct) + "%";
                    } else if (isCancelling) {
                        stateEl.classList.add("is-warn");
                        stateEl.textContent = t("taskStateCancelling");
                    } else {
                        var lastStatus = task.LastExecutionResult && task.LastExecutionResult.Status;
                        if (lastStatus === "Failed") {
                            stateEl.classList.add("is-failed");
                        } else if (lastStatus === "Cancelled" || lastStatus === "Aborted") {
                            stateEl.classList.add("is-warn");
                        }
                        stateEl.textContent = t("taskStateIdle");
                    }
                }

                if (lastEl) {
                    lastEl.textContent = formatLastResult(task);
                }

                if (progressWrap && progressBar) {
                    progressWrap.classList.toggle("strm-hidden", !(isRunning || isCancelling));
                    progressBar.style.width = pct.toFixed(1) + "%";
                }

                if (actionBtn) {
                    actionBtn.disabled = isCancelling;
                    actionBtn.setAttribute("data-task-id", task.Id);
                    actionBtn.setAttribute("data-running", (isRunning || isCancelling) ? "1" : "0");
                    actionBtn.className = "strm-task-btn" + ((isRunning || isCancelling) ? " is-stop" : "");
                    actionBtn.textContent = (isRunning || isCancelling) ? t("taskActionStop") : t("taskActionRun");
                }
            }
        }

        function clearPollTimer() {
            if (state.pollTimer) {
                window.clearInterval(state.pollTimer);
                state.pollTimer = null;
            }
        }

        function ensurePollTimer() {
            if (state.pollTimer || !state.isViewActive) {
                return;
            }

            state.pollTimer = window.setInterval(function () {
                if (!state.isViewActive) {
                    clearPollTimer();
                    return;
                }

                if (typeof document !== "undefined" && document.hidden) {
                    return;
                }

                refreshOverview(true);
            }, 2500);
        }

        function applyTranslations() {
            var nodes = view.querySelectorAll("[data-i18n]");
            for (var i = 0; i < nodes.length; i++) {
                var key = nodes[i].getAttribute("data-i18n");
                if (key) {
                    nodes[i].textContent = t(key);
                }
            }

            var langBtnLabel = view.querySelector("#LanguageToggleLabel");
            if (langBtnLabel) {
                langBtnLabel.textContent = t("langToggleLabel");
            }

            var customDirInput = view.querySelector("#CustomBackupDirectory");
            if (customDirInput) {
                customDirInput.setAttribute("placeholder", t("customBackupDirPlaceholder"));
            }

            updateAutoExtractStatusBadge();
            updatePresetAndDelayPreview();
            updateBackupDirPreview();
            updateDirtyState();

            if (state.virtualFoldersLoaded) {
                var currentSelected = getSelectedLibraryIds();
                renderLibraryList(currentSelected);
            } else {
                updateLibraryScopeVisibilityAndSummary();
            }

            renderStatusMetrics(state.lastStatus);
            renderTasks(state.lastTasks);
        }

        function applyConfigToForm(config) {
            var merged = {
                EnableAutoExtract: config.EnableAutoExtract != null ? !!config.EnableAutoExtract : DEFAULTS.EnableAutoExtract,
                ProcessingDelayMs: config.ProcessingDelayMs != null ? Number(config.ProcessingDelayMs) : DEFAULTS.ProcessingDelayMs,
                ProcessingDelayJitterMs: config.ProcessingDelayJitterMs != null ? Number(config.ProcessingDelayJitterMs) : DEFAULTS.ProcessingDelayJitterMs,
                MaxConcurrency: config.MaxConcurrency != null ? Number(config.MaxConcurrency) : DEFAULTS.MaxConcurrency,
                EnableAllLibraries: config.EnableAllLibraries != null ? !!config.EnableAllLibraries : DEFAULTS.EnableAllLibraries,
                EnabledLibraryIds: Array.isArray(config.EnabledLibraryIds) ? config.EnabledLibraryIds.slice() : [],
                ExtractVideoChapters: config.ExtractVideoChapters != null ? !!config.ExtractVideoChapters : DEFAULTS.ExtractVideoChapters,
                IncludeExtraVideos: config.IncludeExtraVideos != null ? !!config.IncludeExtraVideos : DEFAULTS.IncludeExtraVideos,
                EnableMusicMetadata: config.EnableMusicMetadata != null ? !!config.EnableMusicMetadata : DEFAULTS.EnableMusicMetadata,
                EnableExternalLyrics: config.EnableExternalLyrics != null ? !!config.EnableExternalLyrics : DEFAULTS.EnableExternalLyrics,
                CustomBackupDirectory: config.CustomBackupDirectory != null ? String(config.CustomBackupDirectory) : "",
                EnableDebugLogging: config.EnableDebugLogging != null ? !!config.EnableDebugLogging : DEFAULTS.EnableDebugLogging
            };

            state.savedLibraryIdsFallback = merged.EnabledLibraryIds.slice();
            state.savedCustomBackupDir = String(merged.CustomBackupDirectory || "").trim();

            setInputBool("#EnableAutoExtract", merged.EnableAutoExtract);
            setInputValue("#ProcessingDelayMs", clamp(merged.ProcessingDelayMs, 0, 60000, DEFAULTS.ProcessingDelayMs));
            setInputValue("#ProcessingDelayJitterMs", clamp(merged.ProcessingDelayJitterMs, 0, 30000, DEFAULTS.ProcessingDelayJitterMs));
            setInputValue("#MaxConcurrency", clamp(merged.MaxConcurrency, 1, 10, DEFAULTS.MaxConcurrency));
            setInputBool("#EnableAllLibraries", merged.EnableAllLibraries);
            setInputBool("#ExtractVideoChapters", merged.ExtractVideoChapters);
            setInputBool("#IncludeExtraVideos", merged.IncludeExtraVideos);
            setInputBool("#EnableMusicMetadata", merged.EnableMusicMetadata);
            setInputBool("#EnableExternalLyrics", merged.EnableExternalLyrics);
            setInputValue("#CustomBackupDirectory", merged.CustomBackupDirectory);
            setInputBool("#EnableDebugLogging", merged.EnableDebugLogging);

            if (state.virtualFoldersLoaded) {
                renderLibraryList(merged.EnabledLibraryIds);
            } else {
                updateLibraryScopeVisibilityAndSummary();
            }

            updateAutoExtractStatusBadge();
            updatePresetAndDelayPreview();
            updateBackupDirPreview();

            state.baselineSnapshot = normalizeSnapshot(readFormConfig());
            updateDirtyState();
        }

        function refreshOverview(silent, force) {
            var refreshBtn = view.querySelector("#BtnRefreshOverview");
            if (refreshBtn && !silent) {
                refreshBtn.disabled = true;
            }

            // 常规轮询复用尚未完成的一轮；保存设置等强制刷新时直接发起新请求并作废旧响应。
            if (state.refreshPromise && !force) {
                return state.refreshPromise;
            }

            var seq = ++state.refreshSeq;
            var statusPromise = getPluginStatusApi().then(function (data) {
                if (seq === state.refreshSeq && state.isViewActive) {
                    renderStatusMetrics(data);
                }
            }, function () {
                if (seq === state.refreshSeq && state.isViewActive && !silent) {
                    renderStatusMetrics(null);
                }
            });

            var tasksPromise = getScheduledTasksApi().then(function (tasks) {
                if (seq === state.refreshSeq && state.isViewActive) {
                    renderTasks(tasks);
                }
            }, function () {
                // 保持已有任务列表不闪烁
            });

            var currentPromise;
            function completeRefresh() {
                if (state.refreshPromise === currentPromise) {
                    state.refreshPromise = null;
                }
                if (refreshBtn) {
                    refreshBtn.disabled = false;
                }
            }

            currentPromise = Promise.all([statusPromise, tasksPromise]).then(completeRefresh, function (error) {
                completeRefresh();
                throw error;
            });
            state.refreshPromise = currentPromise;
            return currentPromise;
        }

        function loadVirtualFolders() {
            var seq = ++state.virtualFoldersSeq;
            return getVirtualFoldersApi().then(function (folders) {
                if (seq !== state.virtualFoldersSeq) {
                    return;
                }

                var selectedIds = getSelectedLibraryIds();
                state.virtualFolders = Array.isArray(folders) ? folders : [];
                state.virtualFoldersLoaded = true;
                state.virtualFoldersError = false;
                renderLibraryList(selectedIds);
                updateDirtyState();
            }, function () {
                if (seq !== state.virtualFoldersSeq) {
                    return;
                }

                state.virtualFoldersError = true;
                updateLibraryScopeVisibilityAndSummary();
            });
        }

        function loadAllData() {
            Dashboard.showLoadingMsg();

            var configPromise = ApiClient.getPluginConfiguration(pluginId);
            var foldersPromise = loadVirtualFolders();

            Promise.all([configPromise, foldersPromise]).then(function (results) {
                var config = results[0] || {};
                applyConfigToForm(config);
                Dashboard.hideLoadingMsg();
            }, function () {
                Dashboard.hideLoadingMsg();
            });

            refreshOverview(false);
        }

        function onSubmit(e) {
            var form = this;
            updateBackupDirPreview();

            if (form.reportValidity && !form.reportValidity()) {
                e.preventDefault();
                e.stopPropagation();
                return false;
            }

            e.preventDefault();
            e.stopPropagation();

            if (state.submitting) {
                return false;
            }

            var config = readFormConfig();
            state.submitting = true;
            Dashboard.showLoadingMsg();

            ApiClient.updatePluginConfiguration(pluginId, config).then(function (result) {
                state.submitting = false;
                state.savedLibraryIdsFallback = config.EnabledLibraryIds.slice();
                state.savedCustomBackupDir = String(config.CustomBackupDirectory || "").trim();
                state.baselineSnapshot = normalizeSnapshot(config);
                updateDirtyState();
                Dashboard.processPluginConfigurationUpdateResult(result);
                refreshOverview(true, true);
            }, function () {
                state.submitting = false;
                Dashboard.hideLoadingMsg();
                if (Dashboard && typeof Dashboard.alert === "function") {
                    Dashboard.alert(t("saveErrorMsg"));
                }
            });

            return false;
        }

        function bindEvents() {
            var form = view.querySelector("#StrmToolConfigForm");
            var submitButton = view.querySelector(".button-submit");

            if (form) {
                form.addEventListener("submit", onSubmit);
            }
            if (submitButton && form) {
                submitButton.addEventListener("click", function (e) {
                    onSubmit.call(form, e);
                });
            }

            var langBtn = view.querySelector("#BtnToggleLanguage");
            if (langBtn) {
                langBtn.addEventListener("click", function () {
                    state.lang = state.lang === "zh" ? "en" : "zh";
                    try {
                        if (window.localStorage) {
                            window.localStorage.setItem(LANG_STORAGE_KEY, state.lang);
                        }
                    } catch (err) {
                        // 忽略存储受限异常
                    }
                    applyTranslations();
                });
            }

            var refreshBtn = view.querySelector("#BtnRefreshOverview");
            if (refreshBtn) {
                refreshBtn.addEventListener("click", function () {
                    refreshOverview(false, true);
                    loadVirtualFolders();
                });
            }

            var presetCards = view.querySelectorAll(".strm-preset-card[data-preset]");
            for (var i = 0; i < presetCards.length; i++) {
                presetCards[i].addEventListener("click", function () {
                    var presetKey = this.getAttribute("data-preset");
                    var preset = PRESETS[presetKey];
                    if (!preset) {
                        return;
                    }

                    setInputValue("#ProcessingDelayMs", preset.ProcessingDelayMs);
                    setInputValue("#ProcessingDelayJitterMs", preset.ProcessingDelayJitterMs);
                    setInputValue("#MaxConcurrency", preset.MaxConcurrency);
                    updatePresetAndDelayPreview();
                    updateDirtyState();
                });
            }

            ["#ProcessingDelayMs", "#ProcessingDelayJitterMs", "#MaxConcurrency"].forEach(function (selector) {
                var el = view.querySelector(selector);
                if (el) {
                    el.addEventListener("input", function () {
                        updatePresetAndDelayPreview();
                        updateDirtyState();
                    });
                    el.addEventListener("change", function () {
                        updatePresetAndDelayPreview();
                        updateDirtyState();
                    });
                }
            });

            var autoExtractEl = view.querySelector("#EnableAutoExtract");
            if (autoExtractEl) {
                autoExtractEl.addEventListener("change", function () {
                    updateAutoExtractStatusBadge();
                    updateDirtyState();
                });
            }

            var allLibsEl = view.querySelector("#EnableAllLibraries");
            if (allLibsEl) {
                allLibsEl.addEventListener("change", function () {
                    updateLibraryScopeVisibilityAndSummary();
                    updateDirtyState();
                });
            }

            var selectAllBtn = view.querySelector("#BtnSelectAllLibraries");
            if (selectAllBtn) {
                selectAllBtn.addEventListener("click", function () {
                    var checkboxes = view.querySelectorAll("#LibraryListContainer input[type='checkbox'][data-lib-id]");
                    for (var j = 0; j < checkboxes.length; j++) {
                        checkboxes[j].checked = true;
                    }
                    updateLibraryScopeVisibilityAndSummary();
                    updateDirtyState();
                });
            }

            var clearAllBtn = view.querySelector("#BtnClearAllLibraries");
            if (clearAllBtn) {
                clearAllBtn.addEventListener("click", function () {
                    var checkboxes = view.querySelectorAll("#LibraryListContainer input[type='checkbox'][data-lib-id]");
                    for (var j = 0; j < checkboxes.length; j++) {
                        checkboxes[j].checked = false;
                    }
                    updateLibraryScopeVisibilityAndSummary();
                    updateDirtyState();
                });
            }

            ["#ExtractVideoChapters", "#IncludeExtraVideos", "#EnableMusicMetadata", "#EnableExternalLyrics", "#EnableDebugLogging"]
                .forEach(function (selector) {
                    var el = view.querySelector(selector);
                    if (el) {
                        el.addEventListener("change", updateDirtyState);
                    }
                });

            var backupDirEl = view.querySelector("#CustomBackupDirectory");
            if (backupDirEl) {
                backupDirEl.addEventListener("input", function () {
                    updateBackupDirPreview();
                    updateDirtyState();
                });
                backupDirEl.addEventListener("change", function () {
                    updateBackupDirPreview();
                    updateDirtyState();
                });
            }

            var browseBtn = view.querySelector("#BtnBrowseBackupDir");
            if (browseBtn) {
                browseBtn.addEventListener("click", function () {
                    if (typeof window.require !== "function") {
                        Dashboard.alert(t("backupNoticeBrowserMissing"));
                        return;
                    }

                    window.require(["directorybrowser"], function (directoryBrowser) {
                        var DirectoryBrowser = typeof directoryBrowser === "function"
                            ? directoryBrowser
                            : directoryBrowser && directoryBrowser.default;
                        if (typeof DirectoryBrowser !== "function") {
                            Dashboard.alert(t("backupNoticeBrowserMissing"));
                            return;
                        }

                        var picker = new DirectoryBrowser();
                        picker.show({
                            header: t("backupPickerHeader"),
                            includeFiles: false,
                            callback: function (selectedPath) {
                                if (selectedPath) {
                                    setInputValue("#CustomBackupDirectory", selectedPath);
                                    updateBackupDirPreview();
                                    updateDirtyState();
                                }
                                if (typeof picker.close === "function") {
                                    picker.close();
                                }
                            }
                        });
                    }, function () {
                        Dashboard.alert(t("backupNoticeBrowserMissing"));
                    });
                });
            }

            var clearPathBtn = view.querySelector("#BtnClearBackupDir");
            if (clearPathBtn) {
                clearPathBtn.addEventListener("click", function () {
                    setInputValue("#CustomBackupDirectory", "");
                    updateBackupDirPreview();
                    updateDirtyState();
                });
            }

            var resetBtn = view.querySelector("#BtnResetDefaults");
            if (resetBtn) {
                resetBtn.addEventListener("click", function () {
                    setInputBool("#EnableAutoExtract", DEFAULTS.EnableAutoExtract);
                    setInputValue("#ProcessingDelayMs", DEFAULTS.ProcessingDelayMs);
                    setInputValue("#ProcessingDelayJitterMs", DEFAULTS.ProcessingDelayJitterMs);
                    setInputValue("#MaxConcurrency", DEFAULTS.MaxConcurrency);
                    setInputBool("#EnableAllLibraries", DEFAULTS.EnableAllLibraries);
                    setInputBool("#ExtractVideoChapters", DEFAULTS.ExtractVideoChapters);
                    setInputBool("#IncludeExtraVideos", DEFAULTS.IncludeExtraVideos);
                    setInputBool("#EnableMusicMetadata", DEFAULTS.EnableMusicMetadata);
                    setInputBool("#EnableExternalLyrics", DEFAULTS.EnableExternalLyrics);
                    setInputValue("#CustomBackupDirectory", DEFAULTS.CustomBackupDirectory);
                    setInputBool("#EnableDebugLogging", DEFAULTS.EnableDebugLogging);

                    var checkboxes = view.querySelectorAll("#LibraryListContainer input[type='checkbox'][data-lib-id]");
                    for (var j = 0; j < checkboxes.length; j++) {
                        checkboxes[j].checked = false;
                    }
                    state.savedLibraryIdsFallback = [];

                    updateAutoExtractStatusBadge();
                    updatePresetAndDelayPreview();
                    updateLibraryScopeVisibilityAndSummary();
                    updateBackupDirPreview();
                    updateDirtyState();
                });
            }

            var taskButtons = view.querySelectorAll("#StrmTasksGrid [data-task-action]");
            for (var k = 0; k < taskButtons.length; k++) {
                taskButtons[k].addEventListener("click", function () {
                    var btn = this;
                    var taskId = btn.getAttribute("data-task-id");
                    if (!taskId) {
                        return;
                    }

                    var isRunning = btn.getAttribute("data-running") === "1";
                    btn.disabled = true;

                    var req = isRunning ? stopScheduledTaskApi(taskId) : startScheduledTaskApi(taskId);
                    Promise.resolve(req).then(function () {
                        refreshOverview(true, true);
                    }, function () {
                        refreshOverview(true, true);
                    });
                });
            }
        }

        bindEvents();
        applyTranslations();
        ensurePollTimer();
        loadAllData();

        view.addEventListener("viewshow", function () {
            state.isViewActive = true;
            ensurePollTimer();
            if (!state.needsReloadOnShow) {
                return;
            }

            state.needsReloadOnShow = false;
            if (isFormDirty()) {
                refreshOverview(true);
            } else {
                loadAllData();
            }
        });

        view.addEventListener("viewhide", function () {
            state.isViewActive = false;
            state.needsReloadOnShow = true;
            clearPollTimer();
        });

        view.addEventListener("viewdestroy", function () {
            state.isViewActive = false;
            clearPollTimer();
        });
    };
});
