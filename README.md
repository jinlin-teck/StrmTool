# StrmTool for Jellyfin

Jellyfin 插件，用于从 strm 文件中提取媒体技术信息（codec、分辨率、字幕），加速 strm 媒体文件的起播速度。

🎉 **v2.2.0 更新**：新增 Size 保护机制！当 strm 文件的 Size 等元数据被意外重置时，自动从缓存恢复；同时优化了代码结构和错误处理。

## 核心功能

1. **媒体信息提前提取**：在 strm 文件入库后立即向远程服务器请求并获取媒体技术信息（音视频编码、分辨率、字幕等）
2. **自动提取新文件**：新入库的 strm 文件可开启功能后自动在后台提取媒体信息，无需手动介入
3. **媒体信息缓存**：自动缓存提取的媒体信息为同名 `.strmtool.json` 文件（保存在 strm 文件同目录下），下次提取时可直接导入
4. **计划任务支持**：提供 `Extract Strm Media Info`计划任务，支持手动触发和定时执行
5. **配置界面**：提供插件设置页面，可调整自动提取开关、刷新延迟时间、持久化缓存开关和最大并发数以及强制刷新策略

适配 Jellyfin 10.11.0+（最新的 10.11.6 已测试通过，其他版本请自行测试）

## 安装方法

1. 在 Jellyfin 的 `plugin` 目录下新建文件夹 `StrmTool`
2. 将编译生成的 `StrmTool.dll` 放入该文件夹
3. 重启 Jellyfin 服务

## 使用方法

### 插件功能设置

在插件详情页点击"设置"按钮，可以调整以下配置项：

- **自动提取新入库 strm 文件**：启用后，新增的 strm 文件会自动在后台执行媒体信息提取（默认：启用）
- **启用媒体信息缓存**：启用后，提取的媒体信息会保存为 xxx.strmtool.json 文件（与 strm 文件同目录），避免重复探测（默认：启用）
- **刷新延迟（毫秒）**：每次刷新媒体信息后等待的毫秒数，用于避免对远程服务器造成压力（默认：1000ms）
- **最大并发数（需重启生效）**：媒体信息提取任务的最大并发数，范围: 1-50（默认：5）。修改后需要重启 Jellyfin 才能生效。
- **强制刷新选项**：
  - **无视是否已有媒体流**：勾选后无论是否已有媒体信息都执行刷新（仍可利用缓存）
  - **无视缓存**：勾选后直接从远程服务器获取，忽略缓存文件（仍会判断是否已有媒体信息）

**注意**：除"最大并发数"外，其他配置修改后会立即生效（在下次任务执行时自动应用），无需重启 Jellyfin。

### 计划任务

1. 进入 Jellyfin 后台 → 计划任务
2. 找到 `Strm Tool` 分类下的`Extract Strm Media Info`（提取Strm媒体信息）任务
3. 可手动运行或设置定时触发
4. 该任务可以通过设置页的强制刷新选项来执行刷新和缓存利用策略，一个任务即可完成提取、备份、恢复功能。

## 注意事项

- 请根据使用的 Jellyfin 版本选择对应版本的插件
- v1.0.0.3 相比之前版本不会调用任何第三方元数据服务，已有的元数据（标题、描述、海报等）不会被修改
- 媒体信息缓存文件格式为 `strm_filename.strmtool.json`，位于 strm 文件同目录

---

# StrmTool for Jellyfin

Jellyfin plugin for extracting media technical information (codec, resolution, subtitles) from strm files to accelerate playback startup speed.

🎉 **v2.2.0 Update**: New Size protection mechanism! Automatically restores from cache when metadata like Size of strm files is accidentally reset; also optimizes code structure and error handling.

## Core Features

1. **Early Media Information Extraction**: Immediately requests and obtains media technical information (audio/video codec, resolution, subtitles, etc.) from remote servers after strm files are added to the library
2. **Automatic Extraction for New Files**: Newly added strm files can automatically extract media information in the background when the feature is enabled, no manual intervention required
3. **Media Information Caching**: Automatically caches extracted media information as `.strmtool.json` files with the same name (saved in the same directory as the strm file), allowing direct import during next extraction
4. **Scheduled Task Support**: Provides an `Extract Strm Media Info` scheduled task that supports manual triggering and scheduled execution
5. **Configuration Interface**: Provides a plugin settings page to adjust automatic extraction toggle, refresh delay, persistent cache toggle, maximum concurrency, and force refresh strategies

Compatible with Jellyfin 10.11.0+ (latest 10.11.6 tested, other versions please test yourself)

## Installation

1. Create a new folder `StrmTool` in Jellyfin's `plugin` directory
2. Place the compiled `StrmTool.dll` into this folder
3. Restart the Jellyfin service

## Usage

### Plugin Settings

Click the "Settings" button on the plugin details page to adjust the following configuration items:

- **Automatically extract media info for new strm files**: When enabled, newly added strm files will automatically perform media information extraction in the background (Default: Enabled)
- **Enable media info caching**: When enabled, extracted media information will be saved as xxx.strmtool.json files (in the same directory as the strm file) to avoid repeated probing (Default: Enabled)
- **Refresh delay (ms)**: Milliseconds to wait after each media info refresh to avoid overwhelming remote servers (Default: 1000ms)
- **Maximum concurrent extractions (restart required)**: Maximum concurrency for media info extraction tasks, range: 1-50 (Default: 5). Requires Jellyfin restart to take effect.
- **Force Refresh Options**:
  - **Ignore existing media streams**: When enabled, will always execute refresh regardless of whether media stream info already exists (cache can still be used)
  - **Ignore cache**: When enabled, will always fetch from remote server directly, ignoring cache files (will still check if media streams exist)

**Note**: Except for "Maximum concurrent extractions", all other configuration changes take effect immediately (automatically applied on next task execution) without restarting Jellyfin.

### Scheduled Tasks

1. Go to Jellyfin admin → Scheduled Tasks
2. Find the `Extract Strm Media Info` task under the `Strm Tool` category
3. Can be run manually or set to trigger on a schedule
4. This task can use the force refresh options on the settings page to control refresh and cache usage strategies - one task can handle extraction, backup, and recovery functions.

## Notes

- Please select the corresponding plugin version based on your Jellyfin version
- Compared to previous versions, v1.0.0.3 does not call any third-party metadata services, and existing metadata (title, description, posters, etc.) will not be modified
- Media info cache file format is `strm_filename.strmtool.json`, located in the same directory as the strm file
