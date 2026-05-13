#!/usr/bin/env pwsh

# MediaBrowser 一键构建脚本
# 自动构建项目并将输出复制到 General 文件夹

param(
    [string]$Configuration = "Release",
    [string]$Runtime = "win-x64",
    [switch]$Clean
)

$ProjectPath = "src/MediaBrowser.App/MediaBrowser.App.csproj"
$OutputDir = "General"
$PublishDir = "bin/publish"

Write-Host "=== MediaBrowser 构建脚本 ===" -ForegroundColor Green

# 清理构建目录
if ($Clean) {
    Write-Host "清理构建目录..." -ForegroundColor Yellow
    Remove-Item -Recurse -Force "bin", "obj" -ErrorAction SilentlyContinue
}

# 还原 NuGet 包
Write-Host "还原 NuGet 包..." -ForegroundColor Yellow
dotnet restore $ProjectPath

# 构建项目
Write-Host "构建项目..." -ForegroundColor Yellow
dotnet build $ProjectPath -c $Configuration --no-restore

# 发布项目（独立部署）
Write-Host "发布项目..." -ForegroundColor Yellow
dotnet publish $ProjectPath -c $Configuration -r $Runtime --self-contained true -p:PublishSingleFile=false -o $PublishDir

# 清理 General 文件夹
Write-Host "清理 General 文件夹..." -ForegroundColor Yellow
if (Test-Path $OutputDir) {
    Remove-Item -Recurse -Force "$OutputDir/*" -ErrorAction SilentlyContinue
}
else {
    New-Item -ItemType Directory -Path $OutputDir -Force | Out-Null
}

# 复制发布文件到 General 文件夹
Write-Host "复制文件到 General 文件夹..." -ForegroundColor Yellow
Copy-Item -Recurse -Path "$PublishDir/*" -Destination $OutputDir

# 创建启动脚本
$LaunchScript = @"
@echo off
echo Starting MediaBrowser...
MediaBrowser.App.exe
"@
Set-Content -Path "$OutputDir/start.bat" -Value $LaunchScript

Write-Host "=== 构建完成！ ===" -ForegroundColor Green
Write-Host "输出目录: $((Get-Item $OutputDir).FullName)" -ForegroundColor Cyan
Write-Host "运行方式: 双击 start.bat 或直接运行 MediaBrowser.App.exe" -ForegroundColor Cyan

# 显示构建结果
Get-ChildItem $OutputDir -Recurse | Select-Object Name, Length | Format-Table -AutoSize