# 生成 Release 附件清单 update.json
#
# 客户端通过 https://github.com/{owner}/{repo}/releases/latest/download/update.json
# 读取它（普通网页下载，不消耗 GitHub API 配额）。
#
# 用法：
#   powershell -ExecutionPolicy Bypass -File tools\make-update-manifest.ps1 `
#       -ZipPath dist\QuotaChecker-portable-win-x64-v1.5.0.zip -NotesFile notes.md -OutFile dist\update.json

param(
    [Parameter(Mandatory = $true)][string]$ZipPath,
    [string]$NotesFile = '',
    [string]$OutFile = '',
    [string]$Tag = '',
    [string]$Owner = 'Yanmiao233',
    [string]$Repo = '4SAPIQuotaChecker'
)

$ErrorActionPreference = 'Stop'

if (-not (Test-Path -LiteralPath $ZipPath)) { throw "找不到 zip：$ZipPath" }
$zip = Get-Item -LiteralPath $ZipPath
$name = $zip.Name

# 从文件名里取版本：QuotaChecker-portable-win-x64-v1.5.0.zip
if ([string]::IsNullOrWhiteSpace($Tag)) {
    if ($name -notmatch 'v(\d+\.\d+\.\d+)') { throw "无法从文件名解析版本号：$name" }
    $version = $Matches[1]
    $Tag = "v$version"
}
else {
    $version = $Tag.TrimStart('v', 'V')
}

$sha = (Get-FileHash -LiteralPath $ZipPath -Algorithm SHA256).Hash
$notes = ''
if (-not [string]::IsNullOrWhiteSpace($NotesFile) -and (Test-Path -LiteralPath $NotesFile)) {
    $notes = Get-Content -LiteralPath $NotesFile -Raw -Encoding UTF8
}

$manifest = [ordered]@{
    version   = $version
    tag       = $Tag
    notes     = $notes
    asset     = $name
    size      = $zip.Length
    sha256    = $sha
    asset_url = "https://github.com/$Owner/$Repo/releases/download/$Tag/$name"
    page_url  = "https://github.com/$Owner/$Repo/releases/tag/$Tag"
    generated = (Get-Date).ToUniversalTime().ToString('yyyy-MM-ddTHH:mm:ssZ')
}

if ([string]::IsNullOrWhiteSpace($OutFile)) {
    $OutFile = Join-Path $zip.DirectoryName 'update.json'
}

$json = $manifest | ConvertTo-Json -Depth 4
[System.IO.File]::WriteAllText($OutFile, $json, (New-Object System.Text.UTF8Encoding($false)))

Write-Host "manifest -> $OutFile"
Write-Host "  version : $version"
Write-Host "  size    : $([math]::Round($zip.Length / 1MB, 1)) MB"
Write-Host "  sha256  : $sha"
if ($notes.Length -gt 0) { Write-Host "  notes   : $($notes.Length) chars" } else { Write-Host "  notes   : (empty)" }
