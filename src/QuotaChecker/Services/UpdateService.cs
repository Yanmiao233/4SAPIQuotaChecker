using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Net.Http;
using System.Reflection;
using System.Text;
using System.Text.Json;

namespace QuotaChecker.Services;

/// <summary>GitHub 最新 Release 的关键信息。</summary>
public sealed record UpdateInfo(string Tag, string Notes, string AssetUrl, long AssetBytes);

/// <summary>
/// 便携版自动更新：
/// 1. 查 GitHub Releases 最新版本并与当前程序集版本比较
/// 2. 下载附件 zip 并解压到 %TEMP%
/// 3. 用临时编译的小更新器接管：等主程序退出 -> 替换 app\ -> 重启
/// </summary>
public sealed class UpdateService
{
    public const string RepoOwner = "Yanmiao233";
    public const string RepoName = "4SAPIQuotaChecker";

    /// <summary>更新暂存目录前缀（启动时会清理残留）。</summary>
    public const string StagingPrefix = "QuotaChecker-update-";

    private const string UpdaterSource = @"
using System;
using System.Diagnostics;
using System.IO;
using System.Threading;

static class Updater
{
    static int Main(string[] args)
    {
        if (args.Length < 4) return 2;
        int pid = int.Parse(args[0]);
        string srcRoot = args[1];
        string dstRoot = args[2];
        string exe = args[3];

        for (int i = 0; i < 30; i++)
        {
            Process p = null;
            try
            {
                p = Process.GetProcessById(pid);
                if (p.HasExited) { p.Dispose(); break; }
            }
            catch (ArgumentException) { break; }
            finally { if (p != null) p.Dispose(); }
            Thread.Sleep(1000);
        }
        Thread.Sleep(1500);

        try
        {
            string dstApp = Path.Combine(dstRoot, ""app"");
            string srcApp = Path.Combine(srcRoot, ""app"");
            if (Directory.Exists(dstApp)) DeleteDir(dstApp);
            CopyDir(srcApp, dstApp);
            foreach (string f in Directory.GetFiles(srcRoot))
                File.Copy(f, Path.Combine(dstRoot, Path.GetFileName(f)), true);
            Process.Start(new ProcessStartInfo
            {
                FileName = exe,
                WorkingDirectory = Path.GetDirectoryName(exe),
                UseShellExecute = true
            });
        }
        catch { }
        return 0;
    }

    static void DeleteDir(string dir)
    {
        foreach (string d in Directory.GetDirectories(dir)) DeleteDir(d);
        foreach (string f in Directory.GetFiles(dir))
        {
            try { File.SetAttributes(f, FileAttributes.Normal); } catch { }
            File.Delete(f);
        }
        Directory.Delete(dir, true);
    }

    static void CopyDir(string a, string b)
    {
        Directory.CreateDirectory(b);
        foreach (string d in Directory.GetDirectories(a)) CopyDir(d, Path.Combine(b, Path.GetFileName(d)));
        foreach (string f in Directory.GetFiles(a)) File.Copy(f, Path.Combine(b, Path.GetFileName(f)), true);
    }
}
";

    private readonly HttpClient _http;

    public UpdateService(HttpClient? http = null)
    {
        // 总超时设为无限：大文件在慢网络下整体耗时可能很长；
        // 超时控制改为"单次读取看门狗"（见 DownloadAsync），卡死才报错。
        _http = http ?? new HttpClient { Timeout = Timeout.InfiniteTimeSpan };
        if (!_http.DefaultRequestHeaders.UserAgent.Any())
        {
            _http.DefaultRequestHeaders.UserAgent.ParseAdd("QuotaChecker/1.0");
        }
    }

    /// <summary>当前程序版本（取自 csproj 的 Version，去掉 build 元数据）。</summary>
    public static string CurrentVersion
    {
        get
        {
            var raw = Assembly.GetEntryAssembly()
                ?.GetCustomAttribute<AssemblyInformationalVersionAttribute>()
                ?.InformationalVersion;
            if (string.IsNullOrWhiteSpace(raw))
            {
                raw = Assembly.GetEntryAssembly()?.GetName().Version?.ToString(3);
            }

            var value = (raw ?? "0.0.0").Split('+')[0].Trim();
            return value.StartsWith("v", StringComparison.OrdinalIgnoreCase) ? value[1..] : value;
        }
    }

    /// <summary>查询最新 Release；有比当前更新的版本才返回，否则 null。</summary>
    public async Task<UpdateInfo?> CheckLatestAsync(CancellationToken cancellationToken = default)
    {
        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutCts.CancelAfter(TimeSpan.FromSeconds(30));

        using var request = new HttpRequestMessage(HttpMethod.Get,
            $"https://api.github.com/repos/{RepoOwner}/{RepoName}/releases/latest");

        using var response = await _http.SendAsync(request, timeoutCts.Token).ConfigureAwait(false);
        var body = await response.Content.ReadAsStringAsync(timeoutCts.Token).ConfigureAwait(false);

        if (!response.IsSuccessStatusCode)
        {
            var detail = body.Length > 200 ? body[..200] : body;
            throw new InvalidOperationException($"GitHub 返回 {(int)response.StatusCode} {response.ReasonPhrase}：{detail}");
        }

        using var doc = JsonDocument.Parse(body);
        var root = doc.RootElement;

        var tag = root.TryGetProperty("tag_name", out var tagEl) ? tagEl.GetString() ?? string.Empty : string.Empty;
        if (tag.Length == 0 || !IsNewer(tag, CurrentVersion))
        {
            return null;
        }

        var notes = root.TryGetProperty("body", out var bodyEl) && bodyEl.ValueKind == JsonValueKind.String
            ? bodyEl.GetString() ?? string.Empty
            : string.Empty;

        string? assetUrl = null;
        long assetBytes = 0;
        if (root.TryGetProperty("assets", out var assets) && assets.ValueKind == JsonValueKind.Array)
        {
            foreach (var asset in assets.EnumerateArray())
            {
                var name = asset.TryGetProperty("name", out var n) ? n.GetString() ?? string.Empty : string.Empty;
                if (name.Contains("win-x64", StringComparison.OrdinalIgnoreCase) &&
                    name.EndsWith(".zip", StringComparison.OrdinalIgnoreCase))
                {
                    assetUrl = asset.TryGetProperty("browser_download_url", out var u) ? u.GetString() : null;
                    assetBytes = asset.TryGetProperty("size", out var s) && s.ValueKind == JsonValueKind.Number
                        ? s.GetInt64()
                        : 0;
                    break;
                }
            }
        }

        if (string.IsNullOrEmpty(assetUrl))
        {
            throw new InvalidOperationException("最新 Release 里没有找到 win-x64 的 zip 附件，无法自动更新。");
        }

        return new UpdateInfo(tag.TrimStart('v', 'V'), notes, assetUrl, assetBytes);
    }

    /// <summary>
    /// 下载 zip 并解压到 %TEMP% 暂存目录，返回「新包根目录」（内含 app\ 与外层启动器）。
    /// </summary>
    public async Task<string> DownloadAsync(UpdateInfo info, IProgress<double>? progress, CancellationToken cancellationToken = default)
    {
        var staging = Path.Combine(Path.GetTempPath(), StagingPrefix + DateTime.Now.ToString("yyyyMMdd-HHmmss"));
        Directory.CreateDirectory(staging);
        var zipPath = Path.Combine(staging, "update.zip");

        using (var response = await _http.GetAsync(info.AssetUrl, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false))
        {
            response.EnsureSuccessStatusCode();
            var total = response.Content.Headers.ContentLength ?? info.AssetBytes;

            await using var remote = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
            await using var local = File.Create(zipPath);

            var buffer = new byte[1024 * 128];
            long written = 0;
            while (true)
            {
                // 单次读取看门狗：网络断流时 2 分钟内必然报错，而不是永远挂着
                using var readCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                readCts.CancelAfter(TimeSpan.FromSeconds(120));
                int read;
                try
                {
                    read = await remote.ReadAsync(buffer, readCts.Token).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
                {
                    throw new InvalidOperationException("下载中断：超过 120 秒没有收到数据，请检查网络后重试。");
                }

                if (read == 0)
                {
                    break;
                }

                await local.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
                written += read;
                if (total > 0)
                {
                    progress?.Report(written * 100d / total);
                }
            }
        }

        var extractDir = Path.Combine(staging, "pkg");
        ZipFile.ExtractToDirectory(zipPath, extractDir, overwriteFiles: true);
        File.Delete(zipPath);

        // zip 里有顶层目录 QuotaChecker-portable-win-x64\；定位包含 app\QuotaChecker.exe 的那层
        if (File.Exists(Path.Combine(extractDir, "app", "QuotaChecker.exe")))
        {
            return extractDir;
        }

        foreach (var dir in Directory.GetDirectories(extractDir))
        {
            if (File.Exists(Path.Combine(dir, "app", "QuotaChecker.exe")))
            {
                return dir;
            }
        }

        throw new InvalidOperationException("更新包结构不正确（缺少 app\\QuotaChecker.exe），已中止。");
    }

    /// <summary>便携版安装根目录（app\ 的上一级）；非便携布局时返回 null。</summary>
    public static string? DetectInstallRoot()
    {
        var baseDir = AppContext.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        if (!Path.GetFileName(baseDir).Equals("app", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        var root = Directory.GetParent(baseDir)?.FullName;
        return root is not null && Directory.Exists(root) ? root : null;
    }

    /// <summary>
    /// 临时编译更新器并启动（args: 主程序 PID / 新包根目录 / 安装根目录 / 重启的 exe），
    /// 返回后调用方应立即退出主程序；更新器会等退出后替换文件并重启。
    /// </summary>
    public static void LaunchUpdater(string stagingRoot, string installRoot)
    {
        var csc = new[]
        {
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "Microsoft.NET", "Framework64", "v4.0.30319", "csc.exe"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "Microsoft.NET", "Framework", "v4.0.30319", "csc.exe"),
        }.FirstOrDefault(File.Exists)
            ?? throw new InvalidOperationException("找不到系统自带的 csc.exe，无法生成更新器。");

        var updaterExe = Path.Combine(stagingRoot, "updater.exe");
        var sourceFile = Path.Combine(stagingRoot, "updater.cs");
        var logFile = Path.Combine(stagingRoot, "csc-log.txt");
        File.WriteAllText(sourceFile, UpdaterSource, Encoding.UTF8);

        var psi = new ProcessStartInfo
        {
            FileName = csc,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardError = true,
            RedirectStandardOutput = true,
            StandardErrorEncoding = Encoding.UTF8,
            StandardOutputEncoding = Encoding.UTF8,
        };
        psi.ArgumentList.Add("/nologo");
        psi.ArgumentList.Add("/target:winexe");
        psi.ArgumentList.Add("/optimize+");
        // 显式引用：不依赖 csc.rsp 默认集（部分环境下不生效，报 CS0246 找不到 Process）
        psi.ArgumentList.Add("/r:System.dll");
        psi.ArgumentList.Add("/r:System.Core.dll");
        psi.ArgumentList.Add($"/out:{updaterExe}");
        psi.ArgumentList.Add(sourceFile);

        string output;
        using (var compile = Process.Start(psi) ?? throw new InvalidOperationException("无法启动 csc.exe。"))
        {
            output = compile.StandardOutput.ReadToEnd() + compile.StandardError.ReadToEnd();
            compile.WaitForExit();
            File.WriteAllText(logFile, output, Encoding.UTF8);

            if (compile.ExitCode != 0 || !File.Exists(updaterExe))
            {
                var tail = output.Length > 400 ? output[^400..] : output;
                throw new InvalidOperationException(
                    $"更新器编译失败（csc 退出码 {compile.ExitCode}）：{tail}（完整输出在 {logFile}）");
            }
        }

        var exePath = Path.Combine(installRoot, "app", "QuotaChecker.exe");
        var currentPid = Environment.ProcessId;

        var launch = new ProcessStartInfo
        {
            FileName = updaterExe,
            UseShellExecute = false,
            CreateNoWindow = true,
            WorkingDirectory = stagingRoot,
        };
        launch.ArgumentList.Add(currentPid.ToString(System.Globalization.CultureInfo.InvariantCulture));
        launch.ArgumentList.Add(stagingRoot);
        launch.ArgumentList.Add(installRoot);
        launch.ArgumentList.Add(exePath);

        if (Process.Start(launch) is null)
        {
            throw new InvalidOperationException("无法启动更新器。");
        }
    }

    /// <summary>清理上次更新留下的暂存目录。</summary>
    public static void CleanupStaleStaging()
    {
        try
        {
            var temp = Path.GetTempPath();
            foreach (var dir in Directory.GetDirectories(temp, StagingPrefix + "*"))
            {
                try { Directory.Delete(dir, true); } catch { /* 被占用就留给下次 */ }
            }
        }
        catch
        {
            // 清理失败不影响主流程
        }
    }

    /// <summary>语义化版本比较（tag 允许 v 前缀，只比较数字部分）。</summary>
    public static bool IsNewer(string tag, string current) =>
        TryParseVersion(tag, out var tagV) && TryParseVersion(current, out var curV) && tagV > curV;

    private static bool TryParseVersion(string text, out Version version)
    {
        version = new Version(0, 0, 0);
        if (string.IsNullOrWhiteSpace(text))
        {
            return false;
        }

        var value = text.Trim().TrimStart('v', 'V').Split('+', '-', ' ')[0];
        // 逐段剥离非数字尾巴（例如 1.4.0-beta → 1.4.0）
        var parts = value.Split('.');
        var clean = new List<string>();
        foreach (var part in parts)
        {
            var digits = new string(part.TakeWhile(char.IsDigit).ToArray());
            if (digits.Length == 0)
            {
                break;
            }

            clean.Add(digits);
        }

        return clean.Count > 0 && Version.TryParse(string.Join('.', clean), out version!);
    }

    private static string Truncate(string text) => text.Length <= 200 ? text : text[..200] + "…";
}
