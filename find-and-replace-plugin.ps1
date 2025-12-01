# 查找并替换 Jellyfin 插件脚本

Write-Host "========================================" -ForegroundColor Cyan
Write-Host "查找并替换 StrmTool 插件" -ForegroundColor Cyan
Write-Host "========================================" -ForegroundColor Cyan
Write-Host ""

# 1. 检查编译后的文件
$sourceDll = "bin\Release\net9.0\StrmTool.dll"
if (-not (Test-Path $sourceDll)) {
    Write-Host "❌ 错误: 找不到编译后的文件: $sourceDll" -ForegroundColor Red
    Write-Host "请先运行: dotnet build -c Release" -ForegroundColor Yellow
    exit 1
}

$sourceFile = Get-Item $sourceDll
Write-Host "✓ 找到源文件:" -ForegroundColor Green
Write-Host "  路径: $($sourceFile.FullName)" -ForegroundColor White
Write-Host "  大小: $([math]::Round($sourceFile.Length / 1KB, 2)) KB" -ForegroundColor White
Write-Host "  修改时间: $($sourceFile.LastWriteTime)" -ForegroundColor White
Write-Host ""

# 2. 查找 Jellyfin 插件目录
Write-Host "🔍 查找 Jellyfin 插件目录..." -ForegroundColor Yellow
Write-Host ""

$possiblePaths = @(
    "C:\ProgramData\Jellyfin\Server\plugins",
    "C:\Program Files\Jellyfin\Server\plugins",
    "$env:APPDATA\Jellyfin\plugins",
    "D:\Jellyfin\plugins",
    "E:\Jellyfin\plugins"
)

$foundPaths = @()

foreach ($path in $possiblePaths) {
    if (Test-Path $path) {
        Write-Host "  ✓ 找到: $path" -ForegroundColor Green
        $foundPaths += $path
    }
}

# 3. 在所有驱动器中搜索
Write-Host ""
Write-Host "🔍 在所有驱动器中搜索 StrmTool.dll..." -ForegroundColor Yellow
$drives = Get-PSDrive -PSProvider FileSystem | Where-Object { $_.Used -gt 0 }

foreach ($drive in $drives) {
    Write-Host "  搜索 $($drive.Name):\ ..." -ForegroundColor Gray
    try {
        $found = Get-ChildItem -Path "$($drive.Name):\" -Recurse -Filter "StrmTool.dll" -ErrorAction SilentlyContinue | 
                 Where-Object { $_.FullName -like "*Jellyfin*plugin*" -or $_.FullName -like "*plugin*Strm*" }
        
        foreach ($file in $found) {
            Write-Host "  ✓ 找到: $($file.FullName)" -ForegroundColor Green
            Write-Host "    大小: $([math]::Round($file.Length / 1KB, 2)) KB" -ForegroundColor White
            Write-Host "    修改时间: $($file.LastWriteTime)" -ForegroundColor White
            
            $targetPath = $file.FullName
            
            # 询问是否替换
            Write-Host ""
            $confirm = Read-Host "是否替换此文件? (Y/N)"
            if ($confirm -eq "Y" -or $confirm -eq "y") {
                try {
                    # 备份旧文件
                    $backupPath = "$($file.FullName).backup_$(Get-Date -Format 'yyyyMMdd_HHmmss')"
                    Copy-Item -Path $file.FullName -Destination $backupPath -Force
                    Write-Host "  ✓ 已备份到: $backupPath" -ForegroundColor Green
                    
                    # 复制新文件
                    Copy-Item -Path $sourceDll -Destination $file.FullName -Force
                    Write-Host "  ✓ 已替换插件文件" -ForegroundColor Green
                    
                    # 验证
                    $newFile = Get-Item $file.FullName
                    Write-Host "  ✓ 新文件大小: $([math]::Round($newFile.Length / 1KB, 2)) KB" -ForegroundColor Green
                    Write-Host ""
                    Write-Host "========================================" -ForegroundColor Green
                    Write-Host "✓ 替换成功！" -ForegroundColor Green
                    Write-Host "========================================" -ForegroundColor Green
                    Write-Host ""
                    Write-Host "下一步: 重启 Jellyfin 服务器" -ForegroundColor Yellow
                    exit 0
                }
                catch {
                    Write-Host "  ❌ 替换失败: $_" -ForegroundColor Red
                }
            }
        }
    }
    catch {
        # 忽略访问被拒绝的错误
    }
}

Write-Host ""
Write-Host "========================================" -ForegroundColor Yellow
Write-Host "⚠️ 未找到 StrmTool.dll" -ForegroundColor Yellow
Write-Host "========================================" -ForegroundColor Yellow
Write-Host ""
Write-Host "请手动查找 Jellyfin 插件目录，通常在以下位置之一：" -ForegroundColor White
Write-Host "  - C:\ProgramData\Jellyfin\Server\plugins\Strm Tool\" -ForegroundColor Gray
Write-Host "  - [Jellyfin安装目录]\plugins\Strm Tool\" -ForegroundColor Gray
Write-Host "  - [Docker容器]\config\plugins\Strm Tool\" -ForegroundColor Gray
Write-Host ""
Write-Host "然后手动复制文件：" -ForegroundColor White
Write-Host "  源文件: $sourceDll" -ForegroundColor Cyan
Write-Host "  目标: [Jellyfin插件目录]\Strm Tool\StrmTool.dll" -ForegroundColor Cyan
Write-Host ""

