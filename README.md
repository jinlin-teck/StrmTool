# StrmTool for Jellyfin

Jellyfin 插件，用于从 strm 文件中提取媒体技术信息（codec、分辨率、字幕），加速 strm 媒体文件的起播速度。

🎉 **重磅** 祝贺！已完成项目初期设定的所有目标，功能已完全跟上emby分支，版本号更新到 v2.0.0.0！祝大家情人节快乐、预祝春节快乐！

## 核心功能

1. **媒体信息提前提取**：在 strm 文件入库后立即向远程服务器请求并获取媒体技术信息（音视频编码、分辨率、字幕等）
2. **自动提取新文件**：新入库的 strm 文件可开启功能后自动在后台提取媒体信息，无需手动介入
3. **媒体信息缓存**：自动缓存提取的媒体信息为同名 `.strmtool.json` 文件（保存在 strm 文件同目录下），下次提取时可直接导入
4. **计划任务支持**：提供 `Extract Strm Media Info`（提取）和 `Backup Strm Media Info Cache`（缓存备份）两个计划任务，支持手动触发和定时执行
5. **配置界面**：提供插件设置页面，可调整自动提取开关、刷新延迟时间、持久化缓存开关和最大并发数

适配 Jellyfin 10.11.0+（最新的 10.11.6 都已测试通过，其他版本请自行测试）

## 安装方法

1. 在 Jellyfin 的 `plugin` 目录下新建文件夹 `StrmTool`
2. 将编译生成的 `StrmTool.dll` 放入该文件夹
3. 重启 Jellyfin 服务

## 使用方法

### 自动提取

在插件详情页点击"设置"按钮，可以调整以下配置项：

- **自动提取新入库 strm 文件**：启用后，新增的 strm 文件会自动在后台执行媒体信息提取（默认：启用）
- **启用媒体信息缓存**：启用后，提取的媒体信息会保存为 xxx.strmtool.json 文件（与 strm 文件同目录），避免重复探测（默认：启用）
- **刷新延迟（毫秒）**：每次刷新媒体信息后等待的毫秒数，用于避免对远程服务器造成压力（默认：1000ms）
- **最大并发数**：媒体信息提取任务的最大并发数，范围: 1-50（默认：5）。合理设置可避免服务器过载。

### 手动执行

1. 进入 Jellyfin 后台 → 计划任务
2. 找到 `Strm Tool` 分类下的以下任务：
   - `Extract Strm Media Info`：提取/补全媒体信息
   - `Backup Strm Media Info Cache`：将媒体信息写入缓存文件（已有缓存则跳过；若缺少媒体流会先远程探测）
3. 可手动运行或设置定时触发

## 注意事项

- 请根据使用的 Jellyfin 版本选择对应版本的插件
- v1.0.0.3 相比之前版本不会调用任何第三方元数据服务，已有的元数据（标题、描述、海报等）不会被修改
- 配置变更后需要重启 Jellyfin 服务器才能完全生效
- 媒体信息缓存文件格式为 `strm_filename.strmtool.json`，位于 strm 文件同目录

---

# StrmTool for Jellyfin

Jellyfin plugin for extracting media technical information (codec, resolution, subtitles) from strm files, accelerating the playback speed of strm media files.

🎉 **Major Update** Congratulations! All initial project goals have been completed, functionality is fully aligned with the emby branch, version updated to v2.0.0.0! Happy Valentine's Day and early Spring Festival wishes!

## Core Features

1. **Early Media Information Extraction**: Immediately requests and obtains media technical information (audio/video codec, resolution, subtitles, etc.) from remote servers after strm files are added to the library
2. **Automatic Extraction for New Files**: Newly added strm files can automatically extract media information in the background after enabling the feature, no manual intervention required
3. **Media Information Caching**: Automatically caches extracted media information as JSON files with the same name (saved in the same directory as the strm file), allowing direct import during next extraction
4. **Scheduled Task Support**: Provides two scheduled tasks: `Extract Strm Media Info` (extraction) and `Backup Strm Media Info Cache` (cache backup), both supporting manual triggering and scheduled execution
5. **Configuration Interface**: Provides plugin settings page to adjust automatic extraction switch, refresh delay time, persistent cache switch, and maximum concurrency

Compatible with Jellyfin 10.11.0+ (tested with latest 10.11.6, other versions please test yourself)

## Installation Method

1. Create a new folder `StrmTool` in Jellyfin's `plugin` directory
2. Place the compiled `StrmTool.dll` into this folder
3. Restart Jellyfin service

## Usage Instructions

### Automatic Extraction

Click the "Settings" button on the plugin details page to adjust the following configuration items:

- **Automatically Extract Newly Added Strm Files**: When enabled, newly added strm files will automatically perform media information extraction in the background (Default: Enabled)
- **Enable Media Information Cache**: When enabled, extracted media information will be saved as xxx.strmtool.json files (in the same directory as the strm file) to avoid repeated probing (Default: Enabled)
- **Refresh Delay (ms)**: Milliseconds to wait after each media information refresh, used to avoid putting pressure on remote servers (Default: 1000ms)
- **Maximum Concurrency**: Maximum concurrency for media information extraction tasks, range: 1-50 (Default: 5). Reasonable settings can avoid server overload.

### Manual Execution

1. Go to Jellyfin backend → Scheduled Tasks
2. Find the following tasks under the `Strm Tool` category:
   - `Extract Strm Media Info`: Extract/complete media information
   - `Backup Strm Media Info Cache`: Write media information to cache files (skip if cache already exists; if streams are missing, probe remotely first)
3. Can be run manually or set to trigger on schedule

## Notes

- Please select the corresponding plugin version based on your Jellyfin version
- v1.0.0.3 compared to previous versions will not call any third-party metadata services, existing metadata (title, description, posters, etc.) will not be modified
- Configuration changes require restarting the Jellyfin server to take full effect
- Media information cache file format is `strm_filename.strmtool.json`, located in the same directory as the strm file
