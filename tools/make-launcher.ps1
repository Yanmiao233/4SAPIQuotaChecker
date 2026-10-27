# 生成外层启动器 QuotaChecker.exe（放在包根目录，启动 app\QuotaChecker.exe）
#
# 为什么不是 .lnk：IShellLink 保存时会把目标转成本机绝对路径（PIDL），
# 相对路径不会被持久化——压缩包解压到别的目录/别的机器后快捷方式必然失效。
#
# 这个启动器是几 KB 的原生 exe：
#   - 双击后启动 app\QuotaChecker.exe（按自身所在目录定位，整包移动有效）
#   - /target:winexe 无控制台黑框，带程序图标
#   - 用 Windows 自带的 .NET Framework 4 C# 编译器编译，
#     目标机器无需安装任何运行时
#
# 用法：powershell -ExecutionPolicy Bypass -File tools\make-launcher.ps1 -BaseDir <包根目录> [-IcoPath <app.ico>]

param(
    [Parameter(Mandatory = $true)][string]$BaseDir,
    [string]$IcoPath = ''
)

$ErrorActionPreference = 'Stop'

$cscCandidates = @(
    "$env:WINDIR\Microsoft.NET\Framework64\v4.0.30319\csc.exe",
    "$env:WINDIR\Microsoft.NET\Framework\v4.0.30319\csc.exe"
)
$csc = $cscCandidates | Where-Object { Test-Path $_ } | Select-Object -First 1
if (-not $csc) { throw '找不到 .NET Framework 的 csc.exe（Windows 10/11 应自带）。' }

$root = if ([System.IO.Path]::IsPathRooted($BaseDir)) { $BaseDir } else { Join-Path (Get-Location) $BaseDir }
if (-not (Test-Path (Join-Path $root 'app\QuotaChecker.exe'))) {
    throw "找不到 $root\app\QuotaChecker.exe，请先组装 app 目录"
}

$source = @'
using System;
using System.Diagnostics;
using System.IO;
using System.Windows.Forms;

internal static class Launcher
{
    [STAThread]
    private static int Main()
    {
        string baseDir = AppDomain.CurrentDomain.BaseDirectory;
        string target = Path.Combine(baseDir, "app", "QuotaChecker.exe");

        if (!File.Exists(target))
        {
            MessageBox.Show(
                "未找到程序文件：\n" + target +
                "\n\n请保持启动器与 app 文件夹在同一目录（整个文件夹一起移动）。",
                "QuotaChecker", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return 1;
        }

        var start = new ProcessStartInfo
        {
            FileName = target,
            WorkingDirectory = Path.GetDirectoryName(target),
            UseShellExecute = true,
        };

        try
        {
            Process.Start(start);
            return 0;
        }
        catch (Exception ex)
        {
            MessageBox.Show("启动失败：" + ex.Message, "QuotaChecker", MessageBoxButtons.OK, MessageBoxIcon.Error);
            return 1;
        }
    }
}
'@

$srcFile = Join-Path $env:TEMP "qc-launcher-$([guid]::NewGuid().ToString('N').Substring(0,8)).cs"
Set-Content -Path $srcFile -Value $source -Encoding UTF8

$outExe = Join-Path $root 'QuotaChecker.exe'
$args = @(
    '/nologo', '/target:winexe', '/platform:anycpu', '/optimize+',
    '/r:System.dll', '/r:System.Core.dll', '/r:System.Windows.Forms.dll',
    "/out:`"$outExe`"", "`"$srcFile`""
)
if ($IcoPath -and (Test-Path $IcoPath)) { $args += "/win32icon:`"$IcoPath`"" }

& $csc @args 2>&1 | ForEach-Object { Write-Host "    $_" }
Remove-Item $srcFile -Force -ErrorAction SilentlyContinue

if (-not (Test-Path $outExe)) { throw '启动器编译失败' }
Write-Output "launcher created: $outExe ($([math]::Round((Get-Item $outExe).Length/1KB,1)) KB)"
