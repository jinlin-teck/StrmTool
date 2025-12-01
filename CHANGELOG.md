# Changelog

All notable changes to this project will be documented in this file.

The format is based on [Keep a Changelog](https://keepachangelog.com/en/1.0.0/),
and this project adheres to [Semantic Versioning](https://semver.org/spec/v2.0.0.html).

## [1.0.0] - 2025-12-01

### Added
- 🎬 自动提取 STRM 文件的媒体信息（视频流、音频流、字幕等）
- 📏 更新 STRM 文件大小为实际媒体文件大小
- ⚡ 智能跳过已处理的文件，提升扫描效率
- 💾 多层持久化机制，确保数据不丢失
- 🔧 设置 VideoType、IsShortcut、LocationType 属性，让 Jellyfin 正确识别文件

### Features
- 支持本地文件路径的 STRM 文件
- 自动检测文件是否需要处理（基于元数据完整性）
- 批量处理多个媒体库
- 详细的日志输出，方便调试

### Technical Details
- 基于 .NET 9.0
- 支持 Jellyfin 10.11.0+
- 使用 FFProbe 提取媒体信息
- 直接写入 Jellyfin 数据库，确保持久化

### Known Issues
- 手动刷新媒体库可能会清空部分媒体信息，需要重新运行任务
- 仅支持本地文件路径，不支持 HTTP/HTTPS URL

### Performance
- 首次扫描：5-10 分钟（187 个文件）
- 后续扫描：5-10 秒（自动跳过已处理文件）

---

## [Unreleased]

### Planned Features
- [ ] 支持 HTTP/HTTPS URL 的 STRM 文件
- [ ] 自定义配置选项（扫描间隔、跳过规则等）
- [ ] 更详细的统计信息
- [ ] 支持更多媒体格式

---

**注意**：版本号遵循语义化版本规范（Semantic Versioning）
- **主版本号**：不兼容的 API 修改
- **次版本号**：向下兼容的功能性新增
- **修订号**：向下兼容的问题修正

