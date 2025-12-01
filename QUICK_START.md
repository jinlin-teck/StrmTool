# StrmTool 快速开始指南

## 🚀 5 分钟快速上手

### 1️⃣ 下载插件
从 [Releases](https://github.com/yourusername/StrmTool/releases/latest) 下载 `StrmTool-v1.0.0.zip`

### 2️⃣ 安装插件
解压并复制 `StrmTool.dll` 到 Jellyfin 插件目录：

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

### 3️⃣ 重启 Jellyfin
重启 Jellyfin 服务器

### 4️⃣ 运行任务
1. 打开 Jellyfin 控制台
2. 进入 **计划任务**
3. 找到 **Strm Tool** → **Extract Strm Media Info**
4. 点击 **运行**

### 5️⃣ 查看结果
在媒体库中查看 STRM 文件，文件大小应该显示为实际媒体文件的大小（GB 级别）

---

## 📋 STRM 文件格式

STRM 文件应该包含本地文件路径，例如：

```
/CloudNAS/115open(22099546)/媒体库/电影/罗小黑战记 2 (2025)/罗小黑战记 2.mkv
```

或 Windows 路径：

```
D:\Media\Movies\Movie Name (2025)\Movie Name.mkv
```

---

## 🔍 验证安装

### 检查插件是否加载
在 Jellyfin 日志中查找：
```
[INF] Loaded plugin: Strm Tool 1.0.0.0
```

### 检查任务是否可用
在 **控制台** → **计划任务** 中应该能看到 **Strm Tool** 栏目

### 检查处理结果
运行任务后，在日志中查找：
```
[INF] StrmTool v1.0.0.0 - Starting scan...
[INF] Found X strm files
[INF] Processing X files...
[INF] Task completed successfully
```

---

## ⚠️ 常见问题

### Q: 文件大小显示不正确？
**A:** 检查 STRM 文件路径是否正确，确保 Jellyfin 有权限访问实际媒体文件

### Q: 插件无法加载？
**A:** 确保 Jellyfin 版本 >= 10.11.0，系统已安装 .NET 9.0 运行时

### Q: 任务运行很慢？
**A:** 首次运行会处理所有文件，可能需要 5-10 分钟。后续运行会自动跳过已处理的文件，只需几秒钟

### Q: 手动刷新媒体库后文件大小又变回去了？
**A:** 这是正常现象，重新运行任务即可恢复

---

## 📚 更多信息

- **完整文档**: [README.md](README.md)
- **更新日志**: [CHANGELOG.md](CHANGELOG.md)
- **发布说明**: [releases/RELEASE_NOTES_v1.0.0.md](releases/RELEASE_NOTES_v1.0.0.md)
- **问题反馈**: [GitHub Issues](https://github.com/yourusername/StrmTool/issues)

---

**祝你使用愉快！** 🎉

