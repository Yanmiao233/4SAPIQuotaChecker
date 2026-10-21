# 构建便携版压缩包：publish -> 裁剪体积 -> 组装目录 -> 打包 zip
#
# 用法：
#   powershell -ExecutionPolicy Bypass -File tools\build-portable.ps1
#   powershell -ExecutionPolicy Bypass -File tools\build-portable.ps1 -Platform ARM64
#   powershell -ExecutionPolicy Bypass -File tools\build-portable.ps1 -NoTrim      # 关闭裁剪（排错用）
#   powershell -ExecutionPolicy Bypass -File tools\build-portable.ps1 -KeepAllLocales
#
# 注意：本文件保存为 UTF-8 with BOM（Windows PowerShell 5.1 才能正确读取中文）。

[CmdletBinding()]
param(
    [ValidateSet('x64', 'ARM64')][string]$Platform = 'x64',
    [string]$OutputDir = '',
    [switch]$NoTrim,
    [switch]$KeepAllLocales
)

$ErrorActionPreference = 'Stop'
$scriptDir = Split-Path -Parent $MyInvocation.MyCommand.Path
$repoRoot = (Resolve-Path (Join-Path $scriptDir '..')).Path
if ([string]::IsNullOrWhiteSpace($OutputDir)) { $OutputDir = Join-Path $repoRoot 'dist' }
$project = Join-Path $repoRoot 'src\QuotaChecker\QuotaChecker.csproj'
$packaging = Join-Path $repoRoot 'packaging'
$rid = if ($Platform -eq 'ARM64') { 'win-arm64' } else { 'win-x64' }
$packageName = "QuotaChecker-portable-$rid"

Write-Host "==> 发布 $rid (Trim=$(-not $NoTrim))" -ForegroundColor Cyan

$staging = Join-Path $env:TEMP "qcportable-$rid"
if (Test-Path $staging) { Remove-Item $staging -Recurse -Force }
$publishDir = Join-Path $staging '_publish'
$appDir = Join-Path $staging "$packageName\app"
New-Item -ItemType Directory -Force $appDir | Out-Null

$publishArgs = @(
    'publish', $project,
    '-c', 'Release',
    "-p:Platform=$Platform",
    '-o', $publishDir,
    '--nologo'
)
if ($NoTrim) { $publishArgs += '-p:PublishTrimmed=false' }

& dotnet @publishArgs
if ($LASTEXITCODE -ne 0) { throw "dotnet publish 失败（exit $LASTEXITCODE）" }

Write-Host "==> 复制到 app\" -ForegroundColor Cyan
Copy-Item (Join-Path $publishDir '*') $appDir -Recurse -Force

# ---------- 精简：去掉用不到的语言资源与组件 ----------
Write-Host "==> 精简体积" -ForegroundColor Cyan

if (-not $KeepAllLocales) {
    # 保留的界面语言；WinUI 内建控件文案在缺失语言时会回退到 en-US
    $keepLocales = @('en-us', 'en-gb', 'zh-cn', 'zh-tw', 'zh-hans', 'zh-hant')
    # 这两个目录名长得像语言代码，但其实是功能目录，必须保留
    $notLocales = @('microsoft.ui.xaml', 'npudetect')

    $removed = 0
    Get-ChildItem $appDir -Directory | Where-Object {
        $n = $_.Name.ToLowerInvariant()
        ($n -match '^[a-z]{2,3}(-[a-z0-9]{2,8})*$') -and
        ($notLocales -notcontains $n) -and
        ($keepLocales -notcontains $n)
    } | ForEach-Object {
        Remove-Item $_.FullName -Recurse -Force
        $removed++
    }
    Write-Host "    移除 $removed 个语言目录"
}

# 仅调试器与未使用的组件
$prunePatterns = @(
    'Microsoft.DiaSymReader.Native.*.dll',   # 读取 PDB 用，Release 无 PDB
    'Microsoft.Windows.Widgets.*',           # 小组件，未使用
    'Microsoft.Web.WebView2.Core*.dll',      # WebView2，未使用
    'WebView2Loader.dll'
)
foreach ($pattern in $prunePatterns) {
    Get-ChildItem $appDir -Recurse -File -Filter $pattern | ForEach-Object {
        Write-Host "    移除 $($_.Name)"
        Remove-Item $_.FullName -Force
    }
}

# ---------- 组装根目录 ----------
Write-Host "==> 组装目录结构" -ForegroundColor Cyan
$rootDir = Join-Path $staging $packageName
Copy-Item (Join-Path $packaging '*') $rootDir -Recurse -Force

# ---------- 打包 ----------
$sizeMb = [math]::Round(((Get-ChildItem $rootDir -Recurse -File | Measure-Object Length -Sum).Sum) / 1MB, 1)
$fileCount = (Get-ChildItem $rootDir -Recurse -File | Measure-Object).Count

New-Item -ItemType Directory -Force $OutputDir | Out-Null
$zipPath = Join-Path $OutputDir "$packageName.zip"
if (Test-Path $zipPath) { Remove-Item $zipPath -Force }

Write-Host "==> 压缩（$sizeMb MB / $fileCount 文件）…" -ForegroundColor Cyan
Add-Type -AssemblyName System.IO.Compression.FileSystem
[System.IO.Compression.ZipFile]::CreateFromDirectory(
    $rootDir, $zipPath, [System.IO.Compression.CompressionLevel]::Optimal, $true)

$zipMb = [math]::Round((Get-Item $zipPath).Length / 1MB, 1)
$sha = (Get-FileHash $zipPath -Algorithm SHA256).Hash

Remove-Item $staging -Recurse -Force

Write-Host ""
Write-Host "完成" -ForegroundColor Green
Write-Host "  包内大小 : $sizeMb MB ($fileCount 个文件)"
Write-Host "  压缩包   : $zipPath"
Write-Host "  压缩后   : $zipMb MB"
Write-Host "  SHA256   : $sha"
Write-Host ""
Write-Host "解压后根目录只有 3 项："
Write-Host "  启动 QuotaChecker.cmd   双击即用"
Write-Host "  使用说明.txt"
Write-Host "  app\                    程序文件"
