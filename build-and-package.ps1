# StrmTool 编译和打包脚本
# 使用方法: .\build-and-package.ps1 -Version "1.0.0"

param(
    [Parameter(Mandatory=$true)]
    [string]$Version
)

Write-Host "========================================" -ForegroundColor Cyan
Write-Host "StrmTool 编译和打包脚本" -ForegroundColor Cyan
Write-Host "版本: $Version" -ForegroundColor Cyan
Write-Host "========================================" -ForegroundColor Cyan
Write-Host ""

# 1. 清理旧的编译文件
Write-Host "[1/6] 清理旧的编译文件..." -ForegroundColor Yellow
if (Test-Path "bin") {
    Remove-Item -Path "bin" -Recurse -Force
    Write-Host "  ✓ 已删除 bin 目录" -ForegroundColor Green
}
if (Test-Path "obj") {
    Remove-Item -Path "obj" -Recurse -Force
    Write-Host "  ✓ 已删除 obj 目录" -ForegroundColor Green
}
Write-Host ""

# 2. 更新版本号
Write-Host "[2/6] 更新版本号..." -ForegroundColor Yellow
$csprojPath = "StrmTool.csproj"
$csprojContent = Get-Content $csprojPath -Raw
$csprojContent = $csprojContent -replace '<AssemblyVersion>.*?</AssemblyVersion>', "<AssemblyVersion>$Version.0</AssemblyVersion>"
$csprojContent = $csprojContent -replace '<FileVersion>.*?</FileVersion>', "<FileVersion>$Version.0</FileVersion>"
Set-Content -Path $csprojPath -Value $csprojContent
Write-Host "  ✓ 版本号已更新为 $Version.0" -ForegroundColor Green
Write-Host ""

# 3. 编译项目
Write-Host "[3/6] 编译项目..." -ForegroundColor Yellow
$buildOutput = dotnet build -c Release 2>&1
if ($LASTEXITCODE -eq 0) {
    Write-Host "  ✓ 编译成功" -ForegroundColor Green
} else {
    Write-Host "  ✗ 编译失败" -ForegroundColor Red
    Write-Host $buildOutput
    exit 1
}
Write-Host ""

# 4. 创建 releases 目录
Write-Host "[4/6] 创建 releases 目录..." -ForegroundColor Yellow
if (-not (Test-Path "releases")) {
    New-Item -ItemType Directory -Path "releases" | Out-Null
    Write-Host "  ✓ 已创建 releases 目录" -ForegroundColor Green
} else {
    Write-Host "  ✓ releases 目录已存在" -ForegroundColor Green
}
Write-Host ""

# 5. 打包 DLL 文件
Write-Host "[5/6] 打包 DLL 文件..." -ForegroundColor Yellow
$zipPath = "releases\StrmTool-v$Version.zip"
if (Test-Path $zipPath) {
    Remove-Item -Path $zipPath -Force
    Write-Host "  ✓ 已删除旧的 ZIP 文件" -ForegroundColor Green
}
Compress-Archive -Path "bin\Release\net9.0\StrmTool.dll" -DestinationPath $zipPath -Force
Write-Host "  ✓ 已创建 $zipPath" -ForegroundColor Green
Write-Host ""

# 6. 显示文件信息
Write-Host "[6/6] 文件信息..." -ForegroundColor Yellow
$dllFile = Get-Item "bin\Release\net9.0\StrmTool.dll"
$zipFile = Get-Item $zipPath
Write-Host "  DLL 文件: $($dllFile.FullName)" -ForegroundColor Cyan
Write-Host "  DLL 大小: $([math]::Round($dllFile.Length / 1KB, 2)) KB" -ForegroundColor Cyan
Write-Host "  ZIP 文件: $($zipFile.FullName)" -ForegroundColor Cyan
Write-Host "  ZIP 大小: $([math]::Round($zipFile.Length / 1KB, 2)) KB" -ForegroundColor Cyan
Write-Host ""

# 完成
Write-Host "========================================" -ForegroundColor Green
Write-Host "✓ 编译和打包完成！" -ForegroundColor Green
Write-Host "========================================" -ForegroundColor Green
Write-Host ""
Write-Host "下一步操作：" -ForegroundColor Yellow
Write-Host "1. 测试插件：将 bin\Release\net9.0\StrmTool.dll 复制到 Jellyfin 插件目录" -ForegroundColor White
Write-Host "2. 提交代码：git add . && git commit -m 'Release v$Version'" -ForegroundColor White
Write-Host "3. 创建标签：git tag -a v$Version -m 'Release version $Version'" -ForegroundColor White
Write-Host "4. 推送代码：git push && git push origin v$Version" -ForegroundColor White
Write-Host "5. 创建 Release：gh release create v$Version releases\StrmTool-v$Version.zip --title 'StrmTool v$Version' --notes-file releases\RELEASE_NOTES_v$Version.md" -ForegroundColor White
Write-Host ""

