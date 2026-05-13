<#
.SYNOPSIS
    一键打包脚本：构建 MediaBrowser 单文件可移植 EXE

.DESCRIPTION
    本脚本会执行以下步骤：
        1. 清理 General/ 目录中的旧产物（保留白名单文件）
        2. dotnet restore
        3. dotnet publish (PublishSingleFile + SelfContained + win-x64)
        4. 将最终的 MediaBrowser.exe 拷贝到 General/ 目录
    最终用户拿到 General/MediaBrowser.exe 后即可在任意 Windows 10/11 (x64)
    电脑上直接双击运行，无需预装 .NET 运行时。

.PARAMETER Clean
    可选。指定时会在构建前额外清理项目的 bin/obj 目录。

.EXAMPLE
    .\build.ps1
    标准打包流程。

.EXAMPLE
    .\build.ps1 -Clean
    先清理 bin/obj 再打包。
#>

[CmdletBinding()]
param(
    [switch]$Clean
)

$ErrorActionPreference = 'Stop'

# ==== 路径常量 ====
$RepoRoot       = $PSScriptRoot
$ProjectPath    = Join-Path $RepoRoot 'src\MediaBrowser.App\MediaBrowser.App.csproj'
$ProjectDir     = Split-Path $ProjectPath
$OutputDir      = Join-Path $RepoRoot 'General'
$PublishDir     = Join-Path $RepoRoot 'artifacts\publish'
$ExeName        = 'MediaBrowser.exe'

# General 目录中需要保留的白名单文件
$KeepFiles      = @('README.txt', '.gitkeep')

function Write-Step {
    param([string]$Message)
    Write-Host ''
    Write-Host "==> $Message" -ForegroundColor Cyan
}

function Fail {
    param([string]$Message)
    Write-Host "[ERROR] $Message" -ForegroundColor Red
    exit 1
}

# ==== 检查 dotnet ====
Write-Step '检查 dotnet SDK'
try {
    $dotnetVersion = (& dotnet --version) 2>&1
    Write-Host "  dotnet $dotnetVersion"
} catch {
    Fail '未找到 dotnet 命令，请先安装 .NET 8 SDK：https://dotnet.microsoft.com/download'
}

# ==== 步骤 1：清理 ====
Write-Step '清理旧产物'

if (-not (Test-Path $OutputDir)) {
    New-Item -ItemType Directory -Path $OutputDir -Force | Out-Null
    Write-Host "  已创建目录：$OutputDir"
}

Get-ChildItem -Path $OutputDir -Force | ForEach-Object {
    if ($KeepFiles -notcontains $_.Name) {
        Remove-Item -Path $_.FullName -Recurse -Force -ErrorAction SilentlyContinue
        Write-Host "  已删除：$($_.Name)"
    }
}

if (Test-Path $PublishDir) {
    Remove-Item -Path $PublishDir -Recurse -Force -ErrorAction SilentlyContinue
    Write-Host "  已清理：$PublishDir"
}

if ($Clean) {
    $binDir = Join-Path $ProjectDir 'bin'
    $objDir = Join-Path $ProjectDir 'obj'
    if (Test-Path $binDir) { Remove-Item -Path $binDir -Recurse -Force; Write-Host "  已清理：$binDir" }
    if (Test-Path $objDir) { Remove-Item -Path $objDir -Recurse -Force; Write-Host "  已清理：$objDir" }
}

# ==== 步骤 2：dotnet restore ====
Write-Step 'dotnet restore'
& dotnet restore $ProjectPath
if ($LASTEXITCODE -ne 0) { Fail "dotnet restore 失败 (exit $LASTEXITCODE)" }

# ==== 步骤 3：dotnet publish ====
Write-Step 'dotnet publish (单文件 / 自包含 / win-x64)'

$publishArgs = @(
    'publish', $ProjectPath,
    '-c', 'Release',
    '-r', 'win-x64',
    '--self-contained', 'true',
    '-p:PublishSingleFile=true',
    '-p:IncludeNativeLibrariesForSelfExtract=true',
    '-p:IncludeAllContentForSelfExtract=true',
    '-p:EnableCompressionInSingleFile=true',
    '-p:DebugType=None',
    '-p:DebugSymbols=false',
    '-o', $PublishDir,
    '--nologo'
)

& dotnet @publishArgs
if ($LASTEXITCODE -ne 0) { Fail "dotnet publish 失败 (exit $LASTEXITCODE)" }

# ==== 步骤 4：拷贝最终 exe 到 General ====
Write-Step '拷贝产物到 General/'

$publishedExe = Join-Path $PublishDir $ExeName
if (-not (Test-Path $publishedExe)) {
    Fail "未找到发布产物：$publishedExe（请检查 csproj 中的 AssemblyName 是否为 MediaBrowser）"
}

$targetExe = Join-Path $OutputDir $ExeName
Copy-Item -Path $publishedExe -Destination $targetExe -Force

# ==== 完成报告 ====
$exeInfo  = Get-Item $targetExe
$sizeMB   = [Math]::Round($exeInfo.Length / 1MB, 2)

Write-Host ''
Write-Host '======================================================' -ForegroundColor Green
Write-Host ' 打包完成！' -ForegroundColor Green
Write-Host '======================================================' -ForegroundColor Green
Write-Host (" 产物路径：{0}" -f $exeInfo.FullName)
Write-Host (" 文件大小：{0} MB ({1:N0} bytes)" -f $sizeMB, $exeInfo.Length)
Write-Host ''
Write-Host ' 这是一个完全独立的可执行文件，可以直接复制到任意' -ForegroundColor Yellow
Write-Host ' Windows 10/11 (x64) 电脑上双击运行，无需安装 .NET。' -ForegroundColor Yellow
Write-Host ''

exit 0