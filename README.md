# StrmTool for Emby

一款专为 Emby 媒体服务器设计的插件，用于优化 strm 文件的媒体信息管理和播放体验。

如果你想用在Jellyfin上，请移步Jellyfin分支：https://github.com/jinlin-teck/StrmTool/tree/jellyfin

## 功能特性

### 🚀 媒体信息提取与加速

- **自动提取**：当新的 strm 文件入库时，自动解析音视频编码、章节、字幕、图片等媒体信息
- **智能处理**：仅处理缺少完整媒体信息的 strm 文件，避免重复操作
- **播放加速**：通过预提取媒体信息，显著提升 strm 文件的起播速度
- **兜底扫描**：内置计划任务（默认凌晨3点，可自定义）确保无遗漏

### 💾 备份与恢复

- **自动备份**：提取媒体信息后自动导出为 JSON 备份文件（自动跳过已有的同名json文件，自动提取和手动任务提取均生效）
- **文件位置**：备份文件保存在 strm 文件同目录下，格式为 `{filename}-mediainfo.json`
- **一键恢复**：支持从 JSON 备份文件快速恢复媒体信息
- **智能检测**：恢复时自动识别需要处理的文件（缺少音视频信息的文件）

## 安装与使用

### 安装步骤

1. 将 `StrmTool.dll` 文件放入 Emby 插件目录
2. 重启 Emby 服务器
3. 插件将自动启用并开始工作

### 计划任务

在 Emby 管理后台的「计划任务」中可找到以下功能：

| 任务名称               | 功能说明                             | 默认执行时间 |
| ---------------------- | ------------------------------------ | ------------ |
| **提取 Strm 媒体信息** | 兜底扫描，确保所有 strm 文件信息完整 | 每天凌晨3点  |
| **导出 STRM 媒体信息** | 手动导出所有 strm 文件的媒体信息     | 手动执行     |
| **恢复 STRM 媒体信息** | 从 JSON 备份文件恢复媒体信息         | 手动执行     |

## 配置说明

插件提供以下设置，如需修改，需要在 `plugins/configurations/StrmTool.xml` 中配置

| 配置项               | 说明                                     | 默认值 |
| -------------------- | ---------------------------------------- | ------ |
| **启用自动提取**     | 检测到新 STRM 文件时是否自动提取媒体信息 | true   |
| **处理延迟（毫秒）** | 每个文件处理之间的延迟时间（毫秒）       | 2000   |
| **最大并发数**       | 同时处理的最大文件数量                   | 3      |

其他说明：

- **计划任务**：可根据需求调整执行时间
- **备份策略**：自动备份，无需手动干预

## 系统要求

- **Emby 版本**：已在 4.8.11.0 和 4.9.1.80 版本测试通过，其他版本请自行测试

## 注意事项

- 插件自动注册事件处理器，安装后立即生效
- 手动刷新媒体库可能导致已提取信息丢失，建议使用备份恢复功能
- 恢复功能仅在需要时手动执行，不会自动运行

---

**使用提示**：建议定期检查计划任务执行情况，确保媒体信息始终保持最新状态，另外下载dll文件注意版本，emby就下载emby版本的dll文件，jellyfin的下载jellyfin版本

---

---

# StrmTool for Emby

A plugin designed specifically for Emby media server to optimize strm file media information management and playback experience.

If you want to use it with Jellyfin, please go to the Jellyfin branch: https://github.com/jinlin-teck/StrmTool/tree/jellyfin

## Features

### 🚀 Media Info Extraction & Acceleration

- **Auto-extraction**: When new strm files are added to the library, automatically parse audio/video codecs, chapters, subtitles, images and other media information
- **Smart processing**: Only process strm files that lack complete media information, avoiding redundant operations
- **Playback acceleration**: Pre-extracting media information significantly improves the startup speed of strm files
- **Scheduled scan**: Built-in scheduled task (default 3 AM, customizable) ensures no files are missed

### 💾 Backup & Restore

- **Auto backup**: Automatically export to JSON backup file after extracting media info (auto-skip existing same-name json files, works for both auto-extraction and manual task extraction)
- **File location**: Backup files are saved in the same directory as strm files, format is `{filename}-mediainfo.json`
- **One-click restore**: Quickly restore media information from JSON backup files
- **Smart detection**: Automatically identify files that need processing during restore (files lacking audio/video info)

## Installation & Usage

### Installation Steps

1. Place `StrmTool.dll` into the Emby plugins directory
2. Restart Emby server
3. The plugin will automatically enable and start working

### Scheduled Tasks

Find the following features in "Scheduled Tasks" in Emby admin dashboard:

| Task Name                   | Description                                                | Default Execution Time |
| --------------------------- | ---------------------------------------------------------- | ---------------------- |
| **Extract Strm Media Info** | Scheduled scan to ensure all strm files have complete info | Daily at 3 AM          |
| **Export STRM Media Info**  | Manually export media info for all strm files              | Manual execution       |
| **Restore STRM Media Info** | Restore media info from JSON backup files                  | Manual execution       |

## Configuration

The plugin provides the following settings. To modify them, edit the file `plugins/configurations/StrmTool.xml`.

| Configuration Option      | Description                                                         | Default Value |
| ------------------------- | ------------------------------------------------------------------- | ------------- |
| **Enable Auto Extract**   | Whether to auto-extract media info when new strm files are detected | true          |
| **Processing Delay (ms)** | Delay between processing each file (ms)                             | 2000          |
| **Max Concurrency**       | Maximum number of files to process simultaneously                   | 3             |

Additional notes:

- **Scheduled tasks**: Execution time can be adjusted as needed
- **Backup strategy**: Auto backup, no manual intervention needed

## System Requirements

- **Emby version**: Tested on 4.8.11.0 and 4.9.1.80, please test other versions yourself

## Notes

- The plugin automatically registers event handlers and takes effect immediately after installation
- Manually refreshing the media library may cause extracted info to be lost, recommend using backup/restore feature
- Restore function only runs manually when needed, it won't run automatically

---

**Usage tips**: It is recommended to regularly check the scheduled task execution to ensure media information is always up to date. Also pay attention to the DLL version when downloading - use the emby version for emby and jellyfin version for jellyfin.
