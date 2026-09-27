# StrmTool for Emby

[中文文档 (README.md)](./README.md)

**StrmTool** is a `.strm` media enhancement plugin built for Emby media server. By pre-extracting technical media information (video/audio codecs, resolution, subtitles, images, etc.) and exporting it to local JSON backups, it dramatically improves library presentation and playback startup speed for cloud and remote media streams. Lost media info can also be restored from backups in one click.

> **Current Version**: `v2.6.0` (Targets **Emby 4.8+**; tested on 4.8.11.0 and 4.9.1.80, please test other versions yourself. If you are using Jellyfin, please go to [StrmTool-Jellyfin](https://github.com/jinlin-teck/StrmTool-Jellyfin) — the Jellyfin version now lives in a separate repository, built on .NET 10 / Jellyfin 12, and is no longer updated here.)
>
> **Recommended Companion**: Need to batch-generate `.strm` files from OpenList / Alist cloud drives? Check out my companion project [openlist-strm](https://github.com/jinlin-teck/openlist-strm) — a lightweight `.strm` generator service with WebUI that pairs seamlessly with this plugin.

---

## ✨ Key Features

### 1. 🚀 Pre-Extraction & Faster Playback Startup

- **Automatic Background Extraction**: When new `.strm` files are added to the library, technical media info (video/audio codecs, resolution, subtitles, images, etc.) is extracted automatically in the background, and complete media spec badges show up on item detail pages immediately.
- **Faster Playback Startup**: Eliminates on-the-fly remote probing when starting playback, so streams start faster.
- **Smart Deduplication**: Only processes `.strm` files that lack complete media information; items with complete info are skipped to avoid redundant operations.
- **Scheduled Safety Net**: A built-in scheduled task (default daily at 3 AM, customizable) scans the whole library to ensure nothing is missed.

### 2. 💾 Media Info Backup & One-Click Restore

- **Automatic Backup**: After extracting media info, a same-name `{filename}-mediainfo.json` backup file is automatically exported next to each `.strm` file (existing backups are skipped; applies to both auto-extraction and manual tasks).
- **One-Click Restore**: Batch-restore media info from JSON backups. During restore, only files missing audio/video info are processed — existing data is never overwritten.
- **Corrupt File Self-Healing**: Corrupt or invalid backup JSON files are automatically renamed to `.bak` for isolation, then the plugin falls back to remote probing, re-extracts, and re-exports.

### 3. 📦 Batch Export & Offline Restore Scheduled Tasks

- Dedicated **Export** and **Restore** scheduled tasks (restore is a pure local disk operation with zero remote network requests), ideal for initializing backups on existing libraries or restoring all technical media info after a migration or library rebuild.

---

## 📦 Installation

### Manual Installation

1. Download the latest `StrmTool.dll` from the [Releases](https://github.com/jinlin-teck/StrmTool/releases) page (choose the file with the `-emby` suffix, e.g., `StrmTool_2.6.0.0-emby.zip`).
2. Create a `StrmTool` folder inside your Emby `plugins` directory (e.g., `/config/plugins/StrmTool` in Docker).
3. Copy `StrmTool.dll` into the `StrmTool` folder and restart the Emby server.
4. Go to **Emby Dashboard → Plugins** and verify `StrmTool` shows as `Active`. The plugin starts working automatically.

> ⚠️ **Version Note**: Make sure to download the Emby build (`-emby` suffix). The Jellyfin build has moved to the [StrmTool-Jellyfin](https://github.com/jinlin-teck/StrmTool-Jellyfin) repository.

---

## ⚙️ Plugin Configuration

Go to **Emby Dashboard → Plugins**, find `StrmTool`, and click the plugin name to open the configuration page (available since v2.5.1).

| Option | Default | Description |
| :--- | :---: | :--- |
| **Enable Auto Extract** | On | Whether to automatically extract media info in the background when new `.strm` files are added to the library. |
| **Processing Delay (ms)** | `2000` ms | Milliseconds to wait before each real remote probe, used to smooth request pacing and avoid cloud drive / WebDAV rate limiting (restoring from local JSON backup is a local operation and is not delayed). |
| **Max Concurrency** | `3` | Maximum number of files processed simultaneously; shared by background probing and batch tasks. |

Additional notes:

- All settings take effect immediately after saving — no Emby restart required.
- Scheduled task execution times can be adjusted as needed on the Emby "Scheduled Tasks" page.

---

## 🕒 Scheduled Tasks Guide

Go to **Emby Dashboard → Scheduled Tasks** to use the following three tasks:

| Task Name | Default Trigger | Use Case & Behavior |
| :--- | :---: | :--- |
| **Extract Strm Media Info** | Daily at 3 AM | **Primary task**. Scans the whole library for `.strm` items missing media info: restores from the sidecar JSON backup first (pure local); probes remotely only when no backup exists, then auto-exports a backup. Runs on a timer as a safety net. |
| **Export STRM Media Info** | Manual | **Initialize backups for existing libraries**. Batch-exports items that already have media info in the Emby database but no local `-mediainfo.json` backup yet; existing backups are skipped. |
| **Restore STRM Media Info** | Manual | **Restore after migration/rebuild**. Scans the whole library and batch-restores missing audio/video stream info from sidecar JSON backups (pure local operation, zero remote network requests). |

---

## 💡 Common Usage Scenarios

### Scenario 1: Fresh Install & Daily Use

Just keep the default settings. New `.strm` items are automatically processed in the background with JSON backups generated, and the daily 3 AM safety-net scan keeps everything complete — no manual work needed.

### Scenario 2: First-Time Setup on an Existing Large Library

1. Run **"Export STRM Media Info"** once to batch-export the media info already stored in the Emby database into local JSON backups (existing backups are skipped).
2. Then run **"Extract Strm Media Info"** once to fill in media info and generate backups for the remaining `.strm` items that have never been probed.

### Scenario 3: Library Rebuild, Emby Reinstall, or Lost Media Info

- **Backups still exist**: Manually refreshing the media library may cause extracted info to be lost. As long as the `-mediainfo.json` backup files remain next to your media files, running **"Restore STRM Media Info"** once restores everything in batch — with zero remote network requests.
- **Migrating to a new server**: Copy the backup files along with the media directories, then run "Restore STRM Media Info" after the new library scan completes.

---

## 📌 Notes & FAQ

1. **Will the restore task overwrite existing media info?**
   - No. Restore only fills in data for items missing audio/video info; items with complete info are skipped automatically.
2. **Can I move or rename the JSON backup files freely?**
   - Backup files must stay in the same directory as the `.strm` file and keep the same base name (only the suffix differs), e.g., `Movie.strm` corresponds to `Movie-mediainfo.json`. Moving the whole media directory does not affect restore.
3. **My cloud drive has strict rate limiting — what if probing is too frequent?**
   - Increase the **"Processing Delay (ms)"** (e.g., to 5000) and consider lowering **"Max Concurrency"**. Items served from local JSON backups never trigger remote requests and are not delayed.
4. **The plugin doesn't seem to work after installation?**
   - Make sure you downloaded the Emby build (`-emby` suffix) DLL and restarted the Emby server; the plugin should show as `Active` in the plugin list.
