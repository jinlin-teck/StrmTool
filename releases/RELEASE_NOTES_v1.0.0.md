# StrmTool v1.0.0 Release Notes

🎉 **首个正式版本发布！**

## 📦 下载

- [StrmTool-v1.0.0.zip](https://github.com/yourusername/StrmTool/releases/download/v1.0.0/StrmTool-v1.0.0.zip)

## ✨ 主要功能

### 1. 自动提取媒体信息
- 从 STRM 文件指向的实际媒体文件中提取完整的视频、音频流信息
- 支持多种媒体格式（MP4, MKV, AVI 等）
- 自动识别编解码器、分辨率、比特率等信息

### 2. 更新文件大小
- 将 STRM 文件的大小更新为实际媒体文件的大小
- 方便在媒体库中查看真实的文件大小
- 支持 GB 级别的大文件

### 3. 智能跳过机制
- 自动检测文件是否已处理
- 跳过已有正确元数据的文件
- 大幅提升扫描效率（从 5-10 分钟降至 5-10 秒）

### 4. 持久化保存
- 多层保存机制，确保数据不丢失
- 直接写入 Jellyfin 数据库
- 重启后数据保持不变

## 🚀 安装方法

### 步骤 1：下载插件
下载 `StrmTool-v1.0.0.zip` 并解压得到 `StrmTool.dll`

### 步骤 2：安装插件
将 `StrmTool.dll` 复制到 Jellyfin 插件目录：

**Windows:**
```
C:\ProgramData\Jellyfin\Server\plugins\Strm Tool\StrmTool.dll
```

**Linux:**
```
/var/lib/jellyfin/plugins/Strm Tool/StrmTool.dll
```

**Docker:**
```
/config/plugins/Strm Tool/StrmTool.dll
```

### 步骤 3：重启 Jellyfin
重启 Jellyfin 服务器以加载插件

### 步骤 4：运行任务
1. 进入 Jellyfin 控制台
2. 导航到 **控制台** → **计划任务**
3. 找到 **Strm Tool** → **Extract Strm Media Info**
4. 点击 **运行**

## 📊 性能数据

基于 187 个 STRM 文件的测试：

| 指标 | 首次运行 | 后续运行 |
|------|---------|---------|
| 扫描时间 | 5-10 分钟 | 5-10 秒 |
| 处理文件数 | 187 | 0（已跳过） |
| CPU 占用 | 中等 | 极低 |
| 内存占用 | < 100 MB | < 50 MB |

## 📋 系统要求

- **Jellyfin 版本**：10.11.0 或更高版本
- **.NET 版本**：.NET 9.0
- **操作系统**：Windows / Linux / macOS

## 📝 使用说明

### STRM 文件格式
STRM 文件应该包含本地文件路径，例如：
```
/CloudNAS/115open(22099546)/媒体库/电影/罗小黑战记 2 (2025)/罗小黑战记 2.mkv
```

### 日志示例
```
[INF] StrmTool v1.0.0.0 - Starting scan...
[INF] Scanning 3 library folders
[INF] Found 187 strm files
[INF] Size stats: 0 correct, 187 small (<1KB), 0 null, 0 skipped (cached)
[INF] 187 need metadata refresh, 0 need size update
[INF] Processing 187 files...
[INF] Processed: 罗小黑战记 2 (2025) - Size: 15.84 GB
[INF] Task completed successfully
```

## ⚠️ 注意事项

1. **首次运行时间较长**：首次运行会处理所有 STRM 文件，可能需要 5-10 分钟
2. **文件访问权限**：确保 Jellyfin 有权限访问 STRM 文件指向的实际媒体文件
3. **库刷新影响**：手动刷新媒体库可能会清空部分媒体信息，建议刷新后重新运行任务
4. **仅支持本地路径**：目前仅支持本地文件路径，不支持 HTTP/HTTPS URL

## 🐛 已知问题

- 手动刷新媒体库可能会清空部分媒体信息
- 仅支持本地文件路径的 STRM 文件

## 🔧 故障排除

### 问题：文件大小显示不正确
**解决方法**：
1. 检查 STRM 文件内容，确保路径正确
2. 检查文件权限，确保 Jellyfin 可以访问
3. 查看 Jellyfin 日志，查找错误信息

### 问题：插件无法加载
**解决方法**：
1. 确保 Jellyfin 版本 >= 10.11.0
2. 确保系统已安装 .NET 9.0 运行时
3. 查看 Jellyfin 日志，查找加载错误

## 🤝 反馈与支持

如果遇到问题或有建议，请：
- 提交 Issue: https://github.com/yourusername/StrmTool/issues
- 查看文档: https://github.com/yourusername/StrmTool

## 📄 许可证

本项目采用 MIT 许可证

---

**感谢使用 StrmTool！** 🎉

如果觉得有用，请给项目点个 ⭐ Star！

