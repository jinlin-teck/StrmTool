# StrmExtract for Jellyfin

1. 功能：jellyfin插件，提取strm文件的媒体信息，补充完整的媒体信息并加速strm媒体文件的起播速度
2. 编译环境：net8.0
3. 没有找到jellyfin适配的NuGet包，使用了本地部署jellyfin的dll文件
4. 只测试了jellyfin 10.10.7版本，其他版本未测试
5. 使用方法：在jellyfin的`plugin`目录新建一个插件文件夹，将`StrmExtract.dll`放入，重启jellyfin server
