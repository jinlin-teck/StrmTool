# StrmTool for Jellyfin

1. 功能：jellyfin插件，提取strm文件的媒体信息，补充完整的媒体信息并加速strm媒体文件的起播速度
2. 编译环境：.net9.0
3. 只测试了jellyfin `10.11.0`版本，其他版本请自行测试
4. 使用方法：在jellyfin的`plugin`目录新建一个插件文件夹，将`StrmTool.dll`放入，重启jellyfin server，在jellyfin后台计划任务可看到`Strm Tool`栏目下的`Extract Strm Media Info`计划任务
5. 手动刷新媒体库会清空之前提取的媒体信息，需要重新运行计划任务提取媒体信息
6. 下载dll文件注意版本，emby就下载emby版本的dll文件，jellyfin的下载jellyfin版本

## to do

- [ ] 新入库strm文件自动提取媒体信息，同时保存到同目录json文件
- [ ] 持久化保存媒体信息到json文件，在有需要的时候从json文件读取，避免每次都去解析strm文件
