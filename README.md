# StrmExtract for Jellyfin

1. 编译环境：net8.0环境
2. 没有找到jellyfin适配的NuGet包，使用了本地部署jellyfin的dll文件
3. 只测试了jellyfin 10.10.7版本，其他版本未测试
4. 使用方法：在jellyfin的`plugin`目录新建一个插件文件夹，将`StrmExtract.dll`放入，重启jellyfin server
