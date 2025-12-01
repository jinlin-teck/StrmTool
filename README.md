# StrmTool for Jellyfin

[![GitHub release](https://img.shields.io/github/v/release/yourusername/StrmTool)](https://github.com/yourusername/StrmTool/releases)
[![License](https://img.shields.io/github/license/yourusername/StrmTool)](LICENSE)

Jellyfin 插件，用于提取 STRM 文件的媒体信息，补充完整的媒体信息并加速 STRM 媒体文件的起播速度。

## ✨ 功能特性

- 🎬 **自动提取媒体信息**：从 STRM 文件指向的实际媒体文件中提取完整的视频、音频流信息
- 📏 **更新文件大小**：将 STRM 文件的大小更新为实际媒体文件的大小，方便管理
- ⚡ **加速起播**：预先提取媒体信息，避免播放时重复探测，加快起播速度
- 🚀 **智能跳过**：已处理的文件自动跳过，提升扫描效率
- 💾 **持久化保存**：多层保存机制，确保数据不丢失

## 📋 系统要求

- **Jellyfin 版本**：10.11.0 或更高版本（已测试 10.11.4）
- **.NET 版本**：.NET 9.0
- **操作系统**：Windows / Linux / macOS

## 📦 安装方法

### 方法 1：手动安装

1. 从 [Releases](https://github.com/yourusername/StrmTool/releases) 页面下载最新版本的 `StrmTool-v1.0.0.zip`
2. 解压得到 `StrmTool.dll`
3. 在 Jellyfin 的 `plugins` 目录下创建文件夹 `Strm Tool`
   - Windows: `C:\ProgramData\Jellyfin\Server\plugins\Strm Tool\`
   - Linux: `/var/lib/jellyfin/plugins/Strm Tool/`
   - Docker: `/config/plugins/Strm Tool/`
4. 将 `StrmTool.dll` 复制到该文件夹
5. 重启 Jellyfin 服务器

### 方法 2：从源码编译

```bash
git clone https://github.com/yourusername/StrmTool.git
cd StrmTool
dotnet build -c Release
```

编译后的文件位于 `bin/Release/net9.0/StrmTool.dll`

## 🚀 使用方法

### 1. 运行计划任务

安装插件并重启 Jellyfin 后：

1. 进入 Jellyfin 控制台
2. 导航到 **控制台** → **计划任务**
3. 找到 **Strm Tool** 栏目下的 **Extract Strm Media Info** 任务
4. 点击 **运行** 按钮

### 2. 查看日志

在 Jellyfin 日志中可以看到处理进度：

```
[INF] StrmTool v1.0.0.0 - Starting scan...
[INF] Scanning 3 library folders
[INF] Found 187 strm files
[INF] Processing 187 files...
[INF] Processed: 罗小黑战记 2 (2025) - Size: 15.84 GB
[INF] Task completed successfully
```

### 3. 验证效果

- 在媒体库中查看 STRM 文件，文件大小应该显示为实际媒体文件的大小（GB 级别）
- 播放 STRM 文件，起播速度应该更快

## 🔧 工作原理

### STRM 文件处理流程

```
1. 扫描媒体库，找到所有 .strm 文件
   ↓
2. 检查文件是否需要处理（缺少视频/音频流或文件大小不正确）
   ↓
3. 读取 STRM 文件内容，获取实际媒体文件路径
   ↓
4. 使用 FFProbe 提取实际媒体文件的信息（视频流、音频流、字幕等）
   ↓
5. 获取实际媒体文件的大小
   ↓
6. 更新 Jellyfin 数据库中的媒体信息和文件大小
   ↓
7. 完成！
```

### 智能跳过机制

插件会自动跳过已经处理过的文件，判断条件：

- ✅ 有视频流
- ✅ 有音频流
- ✅ 文件大小 >= 1KB

如果以上条件都满足，文件将被跳过，大幅提升扫描速度。

## ⚙️ 配置选项

目前插件无需额外配置，安装后即可使用。

## 📝 注意事项

1. **STRM 文件格式**：STRM 文件应该包含本地文件路径，例如：
   ```
   /CloudNAS/115open(22099546)/媒体库/电影/罗小黑战记 2 (2025)/罗小黑战记 2.mkv
   ```

2. **文件访问权限**：确保 Jellyfin 有权限访问 STRM 文件指向的实际媒体文件

3. **首次运行**：首次运行会处理所有 STRM 文件，可能需要较长时间（取决于文件数量）

4. **后续运行**：后续运行会自动跳过已处理的文件，速度很快（几秒钟）

5. **库刷新**：手动刷新媒体库可能会清空部分媒体信息，建议刷新后重新运行任务

## 🐛 故障排除

### 问题 1：文件大小显示不正确

**原因**：STRM 文件路径不正确或 Jellyfin 无权限访问

**解决方法**：
1. 检查 STRM 文件内容，确保路径正确
2. 检查文件权限，确保 Jellyfin 可以访问
3. 查看 Jellyfin 日志，查找错误信息

### 问题 2：CPU 占用过高

**原因**：可能是 Jellyfin 在重复扫描文件

**解决方法**：
1. 停止 Jellyfin 服务
2. 删除插件
3. 重启 Jellyfin
4. 重新安装插件

### 问题 3：插件无法加载

**原因**：.NET 版本不匹配或 Jellyfin 版本不兼容

**解决方法**：
1. 确保 Jellyfin 版本 >= 10.11.0
2. 确保系统已安装 .NET 9.0 运行时
3. 查看 Jellyfin 日志，查找加载错误

## 📊 性能数据

基于 187 个 STRM 文件的测试：

| 操作 | 首次运行 | 后续运行 |
|------|---------|---------|
| 扫描时间 | 5-10 分钟 | 5-10 秒 |
| 处理文件数 | 187 | 0（已跳过） |
| CPU 占用 | 中等 | 极低 |

## 🤝 贡献

欢迎提交 Issue 和 Pull Request！

## 📄 许可证

本项目采用 MIT 许可证 - 详见 [LICENSE](LICENSE) 文件

## 🙏 致谢

- [Jellyfin](https://jellyfin.org/) - 开源媒体服务器
- 所有贡献者和用户

## 📮 联系方式

- GitHub Issues: [https://github.com/yourusername/StrmTool/issues](https://github.com/yourusername/StrmTool/issues)

---

**注意**：请将 `yourusername` 替换为你的 GitHub 用户名
