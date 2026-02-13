# StrmTool

Jellyfin 插件，用于从 strm 文件中提取媒体技术信息（codec、分辨率、字幕），加速 strm 媒体文件的起播速度。

## 环境要求

- .NET 9.0
- Jellyfin 10.11.0+（其他版本请自行测试）

## 安装方法

1. 在 Jellyfin 的 `plugin` 目录下新建文件夹 `StrmTool`
2. 将编译生成的 `StrmTool.dll` 放入该文件夹
3. 重启 Jellyfin 服务

## 使用方法

1. 进入 Jellyfin 后台 → 计划任务
2. 找到 `Strm Tool` 分类下的 `Extract Strm Media Info` 任务
3. 可手动运行或设置定时触发

## 配置说明

在插件设置中可以调整刷新延迟时间（毫秒），默认 1000ms。延迟用于避免对远程服务器造成压力。

## 注意事项

- 手动刷新媒体库会清空之前提取的媒体信息，需要重新运行计划任务
- 请根据使用的 Jellyfin/Emby 版本选择对应版本的插件

## 待实现功能

- [ ] 新入库 strm 文件自动提取媒体信息，同时保存到同目录 json 文件
- [ ] 持久化保存媒体信息到 json 文件，在有需要的时候从 json 文件读取，避免每次都去解析 strm 文件

## 编译

```bash
dotnet build -c Release
```

发布的 DLL 文件位于 `./publish` 目录。
