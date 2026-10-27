using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Net;
using System.Net.Http;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace QuotaChecker.Services;

/// <summary>最新 Release 的关键信息。</summary>
public sealed record UpdateInfo(
    string Tag,
    string Notes,
    string AssetUrl,
    long AssetBytes,
    string Sha256,
    string PageUrl);

/// <summary>
/// 便携版自动更新（<b>不使用 GitHub API</b>，因此没有 60 次/小时的配额限制）：
/// <list type="number">
/// <item>GET <c>{repo}/releases/latest/download/update.json</c> —— GitHub 会 302 到最新 Release
/// 的该附件，属于普通网页下载，不消耗 API 配额。</item>
/// <item>若旧 Release 没有 update.json（404），退化为探测 <c>/releases/latest</c> 的 302 Location
/// 拿到 tag，再按命名约定拼出下载地址。</item>
/// <item>下载 zip → 校验 SHA256（清单提供时）→ 解压到 %TEMP% → 由 PowerShell 更新器替换 app\ 并重启。</item>
/// </list>
/// </summary>
public sealed class UpdateService
{
    public const string RepoOwner = "Yanmiao233";
    public const string RepoName = "4SAPIQuotaChecker";

    /// <summary>更新暂存目录前缀（启动时会清理残留）。</summary>
    public const string StagingPrefix = "QuotaChecker-update-";

    /// <summary>附件清单文件名（每个 Release 都会附带）。</summary>
    public const string ManifestName = "update.json";

    private const int MaxRedirects = 6;

    /// <summary>测试用：设置环境变量 QC_UPDATE_FEED 可把源指向本地模拟站点。</summary>
    private static string FeedBase
    {
        get
        {
            var custom = Environment.GetEnvironmentVariable("QC_UPDATE_FEED");
            return string.IsNullOrWhiteSpace(custom) ? "https://github.com" : custom.TrimEnd('/');
        }
    }

    private static string RepoBase => $"{FeedBase}/{RepoOwner}/{RepoName}";

    private readonly HttpClient _http;

    public UpdateService(HttpClient? http = null)
    {
        // 手动跟随重定向（GitHub 会跳到 objects.githubusercontent.com）；
        // 总超时设为无限，改用"单次读取看门狗"控制卡死。
        _http = http ?? new HttpClient(new SocketsHttpHandler { AllowAutoRedirect = false })
        {
            Timeout = Timeout.InfiniteTimeSpan,
        };

        if (!_http.DefaultRequestHeaders.UserAgent.Any())
        {
            _http.DefaultRequestHeaders.UserAgent.ParseAdd("QuotaChecker/1.0");
        }
    }

    /// <summary>当前程序版本（取自 csproj 的 Version）。</summary>
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

    /// <summary>查询最新版本；已是最新时返回 null。网络失败抛 <see cref="InvalidOperationException"/>。</summary>
    public async Task<UpdateInfo?> CheckLatestAsync(CancellationToken cancellationToken = default)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        cts.CancelAfter(TimeSpan.FromSeconds(30));

        var manifestUrl = $"{RepoBase}/releases/latest/download/{ManifestName}";
        var body = await TryGetStringAsync(manifestUrl, cts.Token).ConfigureAwait(false);

        var info = body is null ? null : ParseManifest(body);

        if (info is null)
        {
            // 兼容没有 update.json 的旧 Release：只探测 tag
            var tag = await ProbeLatestTagAsync(cts.Token).ConfigureAwait(false);
            if (tag is null)
            {
                throw new InvalidOperationException(
                    "无法获取最新版本信息（既没有 update.json，也没能解析 latest 重定向）。请稍后重试或到 Releases 页面手动下载。");
            }

            info = BuildFallbackInfo(tag);
        }

        return IsNewer(info.Tag, CurrentVersion) ? info : null;
    }

    private static UpdateInfo? ParseManifest(string body)
    {
        try
        {
            using var doc = JsonDocument.Parse(body);
            var root = doc.RootElement;

            string GetString(string name) =>
                root.TryGetProperty(name, out var el) && el.ValueKind == JsonValueKind.String
                    ? el.GetString() ?? string.Empty
                    : string.Empty;

            var version = GetString("version").TrimStart('v', 'V');
            var assetUrl = GetString("asset_url");
            if (version.Length == 0 || !Uri.TryCreate(assetUrl, UriKind.Absolute, out var uri))
            {
                return null;
            }

            // 只允许 GitHub 域名，避免清单被篡改后指向任意主机
            var host = uri.Host;
            if (!host.Equals("github.com", StringComparison.OrdinalIgnoreCase) &&
                !host.EndsWith(".githubusercontent.com", StringComparison.OrdinalIgnoreCase) &&
                !host.Equals("127.0.0.1", StringComparison.OrdinalIgnoreCase) &&
                !host.Equals("localhost", StringComparison.OrdinalIgnoreCase))
            {
                return null;
            }

            var bytes = root.TryGetProperty("size", out var sizeEl) && sizeEl.ValueKind == JsonValueKind.Number
                ? sizeEl.GetInt64()
                : 0;

            var page = GetString("page_url");
            if (page.Length == 0)
            {
                page = $"{RepoBase}/releases/tag/v{version}";
            }

            return new UpdateInfo(version, GetString("notes"), assetUrl, bytes, GetString("sha256"), page);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static UpdateInfo BuildFallbackInfo(string tag)
    {
        var version = tag.TrimStart('v', 'V');
        var assetName = $"QuotaChecker-portable-win-x64-v{version}.zip";
        return new UpdateInfo(
            version,
            string.Empty,
            $"{RepoBase}/releases/download/v{version}/{assetName}",
            0,
            string.Empty,
            $"{RepoBase}/releases/tag/v{version}");
    }

    /// <summary>GET /releases/latest 不跟随重定向，从 Location 里取 tag。</summary>
    private async Task<string?> ProbeLatestTagAsync(CancellationToken cancellationToken)
    {
        var url = $"{RepoBase}/releases/latest";
        for (var hop = 0; hop < MaxRedirects; hop++)
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, url);
            using var response = await SendAsync(request, cancellationToken).ConfigureAwait(false);

            if (IsRedirect(response.StatusCode) && response.Headers.Location is { } location)
            {
                var next = new Uri(new Uri(url), location);

                // 形如 https://github.com/{owner}/{repo}/releases/tag/v1.4.0
                var segments = next.AbsolutePath.TrimEnd('/').Split('/');
                var tagIndex = Array.LastIndexOf(segments, "tag");
                if (tagIndex >= 0 && tagIndex + 1 < segments.Length)
                {
                    return Uri.UnescapeDataString(segments[tagIndex + 1]);
                }

                url = next.AbsoluteUri;
                continue;
            }

            return null;
        }

        return null;
    }

    private async Task<string?> TryGetStringAsync(string url, CancellationToken cancellationToken)
    {
        var current = url;
        for (var hop = 0; hop < MaxRedirects; hop++)
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, current);
            using var response = await SendAsync(request, cancellationToken).ConfigureAwait(false);

            if (IsRedirect(response.StatusCode) && response.Headers.Location is { } location)
            {
                current = new Uri(new Uri(current), location).AbsoluteUri;
                continue;
            }

            if (response.StatusCode == HttpStatusCode.NotFound)
            {
                return null;
            }

            if (!response.IsSuccessStatusCode)
            {
                var detail = await ReadBodySafeAsync(response, cancellationToken).ConfigureAwait(false);
                throw new InvalidOperationException($"获取更新信息失败：HTTP {(int)response.StatusCode} {detail}");
            }

            return await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        }

        throw new InvalidOperationException("获取更新信息失败：重定向次数过多。");
    }

    /// <summary>下载并解压到 %TEMP%，返回新包根目录（内含 app\ 与外层启动器）。</summary>
    public async Task<string> DownloadAsync(UpdateInfo info, IProgress<double>? progress, CancellationToken cancellationToken = default)
    {
        var staging = Path.Combine(Path.GetTempPath(), StagingPrefix + DateTime.Now.ToString("yyyyMMdd-HHmmss"));
        Directory.CreateDirectory(staging);
        var zipPath = Path.Combine(staging, "update.zip");

        var current = info.AssetUrl;
        HttpResponseMessage? final = null;
        try
        {
            for (var hop = 0; hop < MaxRedirects; hop++)
            {
                var response = await SendAsync(new HttpRequestMessage(HttpMethod.Get, current), cancellationToken)
                    .ConfigureAwait(false);

                if (IsRedirect(response.StatusCode) && response.Headers.Location is { } location)
                {
                    response.Dispose();
                    current = new Uri(new Uri(current), location).AbsoluteUri;
                    continue;
                }

                if (!response.IsSuccessStatusCode)
                {
                    var detail = await ReadBodySafeAsync(response, cancellationToken).ConfigureAwait(false);
                    response.Dispose();
                    throw new InvalidOperationException($"下载更新失败：HTTP {(int)response.StatusCode} {detail}");
                }

                final = response;
                break;
            }

            if (final is null)
            {
                throw new InvalidOperationException("下载更新失败：重定向次数过多。");
            }

            var total = final.Content.Headers.ContentLength ?? info.AssetBytes;
            await using var remote = await final.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
            await using var local = File.Create(zipPath);

            var buffer = new byte[1024 * 128];
            long written = 0;
            while (true)
            {
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
        finally
        {
            final?.Dispose();
        }

        if (!string.IsNullOrWhiteSpace(info.Sha256))
        {
            var actual = await ComputeSha256Async(zipPath, cancellationToken).ConfigureAwait(false);
            if (!actual.Equals(info.Sha256, StringComparison.OrdinalIgnoreCase))
            {
                File.Delete(zipPath);
                throw new InvalidOperationException(
                    $"更新包校验失败（SHA256 不匹配），已删除下载文件。\n期望 {info.Sha256}\n实际 {actual}");
            }
        }

        var extractDir = Path.Combine(staging, "pkg");
        ZipFile.ExtractToDirectory(zipPath, extractDir, overwriteFiles: true);
        File.Delete(zipPath);

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

    private async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        try
        {
            return await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (TaskCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new InvalidOperationException("连接超时：网络不可达或被防火墙拦截。");
        }
        catch (HttpRequestException ex)
        {
            throw new InvalidOperationException("网络请求失败：" + ex.Message);
        }
    }

    private static async Task<string> ReadBodySafeAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        try
        {
            var text = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            return text.Length > 160 ? text[..160] + "…" : text;
        }
        catch
        {
            return string.Empty;
        }
    }

    private static async Task<string> ComputeSha256Async(string path, CancellationToken cancellationToken)
    {
        await using var stream = File.OpenRead(path);
        var hash = await SHA256.HashDataAsync(stream, cancellationToken).ConfigureAwait(false);
        return Convert.ToHexString(hash);
    }

    private static bool IsRedirect(HttpStatusCode status) =>
        status is HttpStatusCode.MovedPermanently
            or HttpStatusCode.Found
            or HttpStatusCode.SeeOther
            or HttpStatusCode.TemporaryRedirect
            or HttpStatusCode.PermanentRedirect;

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
    /// 启动无窗口的 PowerShell 更新器（等主程序退出 → 替换 app\ 与根目录文件 → 重启）。
    /// 用 -EncodedCommand 传参，没有引号/编码问题。
    /// </summary>
    public static void LaunchUpdater(string stagingRoot, string installRoot)
    {
        var currentPid = Environment.ProcessId;
        var exePath = Path.Combine(installRoot, "app", "QuotaChecker.exe");

        // 模板里已带单引号，替换时只需把路径内的 ' 转义成 ''（不能再加一层引号）
        const string scriptTemplate = @"
$target = __PID__
for ($i = 0; $i -lt 30; $i++) {
    if (-not (Get-Process -Id $target -ErrorAction SilentlyContinue)) { break }
    Start-Sleep -Seconds 1
}
Start-Sleep -Milliseconds 1500
$src = '__SRC__'
$dst = '__DST__'
$exe = '__EXE__'
$log = Join-Path $dst 'update-result.txt'
$srcApp = Join-Path $src 'app'
$dstApp = Join-Path $dst 'app'
try {
    # 1) 尽量整目录替换（文件可能仍被占用，重试几次）
    $removed = $false
    for ($t = 0; $t -lt 12; $t++) {
        try {
            if (Test-Path $dstApp) { Remove-Item $dstApp -Recurse -Force -ErrorAction Stop }
            $removed = $true
            break
        } catch { Start-Sleep -Milliseconds 700 }
    }

    # 2) 拷贝内容（注意用 app\* 而不是 app，否则目标存在时会嵌套成 app\app）
    if (-not (Test-Path $dstApp)) { New-Item -ItemType Directory -Force -Path $dstApp | Out-Null }
    Copy-Item -Path (Join-Path $srcApp '*') -Destination $dstApp -Recurse -Force -ErrorAction Stop

    # 3) 防御：清掉可能历史遗留的嵌套目录
    $nested = Join-Path $dstApp 'app'
    if (Test-Path $nested) { Remove-Item $nested -Recurse -Force -ErrorAction SilentlyContinue }

    # 4) 根目录文件（启动器 / 说明）
    Get-ChildItem $src -File | Copy-Item -Destination $dst -Force -ErrorAction Stop

    # 5) 删除新包里已不存在的旧根文件（按清单比对；保留用户数据与日志）
    $manifestFile = Join-Path $src 'package-manifest.txt'
    if (Test-Path $manifestFile) {
        $keep = @{}
        foreach ($line in (Get-Content $manifestFile -Encoding UTF8)) {
            $n = $line.Trim()
            if ($n.Length -gt 0) { $keep[$n] = $true }
        }
        # 用户数据 / 运行产物不参与清理
        foreach ($reserved in @('keys.json', 'keys.json.bak', 'update-result.txt', 'update.json')) { $keep[$reserved] = $true }

        Get-ChildItem $dst -File | ForEach-Object {
            if (-not $keep.ContainsKey($_.Name)) {
                try { Remove-Item $_.FullName -Force -ErrorAction Stop } catch { }
            }
        }
    }

    # 6) 校验主程序确实被换成新版本
    $newVer = (Get-Item $exe).VersionInfo.FileVersion
    $oldVer = (Get-Item (Join-Path $srcApp 'QuotaChecker.exe')).VersionInfo.FileVersion
    if ($newVer -ne $oldVer) { throw (""exe not replaced: dst=$newVer src=$oldVer"") }

    Set-Content -Path $log -Value (""OK $newVer $(Get-Date -Format s)"") -Encoding UTF8
} catch {
    Set-Content -Path $log -Value (""FAIL $($_.Exception.Message)"") -Encoding UTF8
}
Start-Process -FilePath $exe -WorkingDirectory (Join-Path $dst 'app')
";
        var script = scriptTemplate
            .Replace("__PID__", currentPid.ToString(System.Globalization.CultureInfo.InvariantCulture))
            .Replace("__SRC__", stagingRoot.Replace("'", "''"))
            .Replace("__DST__", installRoot.Replace("'", "''"))
            .Replace("__EXE__", exePath.Replace("'", "''"));

        var encoded = Convert.ToBase64String(Encoding.Unicode.GetBytes(script));

        var psi = new ProcessStartInfo
        {
            FileName = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.System),
                "WindowsPowerShell", "v1.0", "powershell.exe"),
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        psi.ArgumentList.Add("-NoProfile");
        psi.ArgumentList.Add("-NonInteractive");
        psi.ArgumentList.Add("-ExecutionPolicy");
        psi.ArgumentList.Add("Bypass");
        psi.ArgumentList.Add("-EncodedCommand");
        psi.ArgumentList.Add(encoded);

        if (Process.Start(psi) is null)
        {
            throw new InvalidOperationException("无法启动更新器。");
        }
    }

    /// <summary>
    /// 清理安装根目录下已废弃的外层文件（旧启动器名等）。
    /// 在程序启动时执行，这样即使用户是用旧版更新器升级上来的，也能立刻清理干净。
    /// </summary>
    public static void CleanupObsoleteRootFiles(string? installRoot = null)
    {
        var root = installRoot ?? DetectInstallRoot();
        if (root is null)
        {
            return;
        }

        foreach (var name in new[] { "启动 QuotaChecker.exe", "启动 QuotaChecker.cmd", "QuotaChecker.lnk" })
        {
            var path = Path.Combine(root, name);
            if (File.Exists(path))
            {
                try { File.Delete(path); } catch { /* 被占用就下次再试 */ }
            }
        }
    }

    /// <summary>清理上次更新留下的暂存目录。</summary>
    public static void CleanupStaleStaging()
    {
        try
        {
            foreach (var dir in Directory.GetDirectories(Path.GetTempPath(), StagingPrefix + "*"))
            {
                try { Directory.Delete(dir, true); } catch { /* 被占用就留给下次 */ }
            }
        }
        catch
        {
            // 清理失败不影响主流程
        }
    }

    /// <summary>语义化版本比较（允许 v 前缀，只比较数字部分）。</summary>
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
        var clean = new List<string>();
        foreach (var part in value.Split('.'))
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
}
