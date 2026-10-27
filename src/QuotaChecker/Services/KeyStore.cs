using System.Security.Cryptography;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using QuotaChecker.Models;

namespace QuotaChecker.Services;

/// <summary>数据文件的读取结果。</summary>
public enum LoadStatus
{
    /// <summary>文件不存在（首次运行）。</summary>
    Missing,

    /// <summary>读取成功。</summary>
    Ok,

    /// <summary>主文件损坏，已从 .bak 恢复。</summary>
    RecoveredFromBackup,

    /// <summary>主文件与备份都不可用，已隔离为 .corrupt-*，本次以空数据开始。</summary>
    Corrupt,
}

/// <summary>加载结果。</summary>
public sealed record LoadResult(AppDataFile Data, LoadStatus Status, string? Message);

/// <summary>保存失败（调用方必须提示用户，不能静默丢弃）。</summary>
public sealed class PersistenceException : Exception
{
    public PersistenceException(string message, Exception? inner = null) : base(message, inner)
    {
    }
}

/// <summary>
/// 把密钥列表与设置保存到 <c>%LOCALAPPDATA%\QuotaChecker\keys.json</c>。
/// API Key 使用 DPAPI（仅当前用户可解密）加密后落盘，不保存明文。
///
/// 可靠性措施：
/// <list type="bullet">
/// <item>写入前把上一个好文件留成 <c>keys.json.bak</c>，主文件坏了可从备份恢复。</item>
/// <item>写入走 临时文件 → 校验可反序列化 → 替换；被占用时重试 3 次。</item>
/// <item>写不进去就抛 <see cref="PersistenceException"/>，由界面明确告知用户。</item>
/// <item>损坏文件隔离为 <c>keys.json.corrupt-时间戳</c>，绝不直接覆盖。</item>
/// </list>
/// </summary>
public sealed class KeyStore
{
    private static readonly byte[] Entropy = Encoding.UTF8.GetBytes("QuotaChecker.v1");

    /// <summary>源生成上下文；中文不转义输出。</summary>
    private static readonly AppJsonContext Json = new(new JsonSerializerOptions
    {
        WriteIndented = true,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    });

    private readonly string _filePath;

    public KeyStore(string? filePath = null)
    {
        _filePath = filePath ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "QuotaChecker",
            "keys.json");
    }

    public string FilePath => _filePath;

    public string BackupPath => _filePath + ".bak";

    public string DirectoryPath => Path.GetDirectoryName(_filePath) ?? Environment.CurrentDirectory;

    public LoadResult Load()
    {
        if (!File.Exists(_filePath))
        {
            // 主文件被误删但备份还在
            if (File.Exists(BackupPath) && TryRead(BackupPath, out var fromBackup) && fromBackup is not null)
            {
                return new LoadResult(fromBackup, LoadStatus.RecoveredFromBackup, "主数据文件不存在，已从备份恢复。");
            }

            return new LoadResult(new AppDataFile(), LoadStatus.Missing, null);
        }

        if (TryRead(_filePath, out var data) && data is not null)
        {
            return new LoadResult(data, LoadStatus.Ok, null);
        }

        if (File.Exists(BackupPath) && TryRead(BackupPath, out var backup) && backup is not null)
        {
            Quarantine(_filePath);
            return new LoadResult(backup, LoadStatus.RecoveredFromBackup, "数据文件损坏，已从备份恢复上一次的内容。");
        }

        var quarantined = Quarantine(_filePath);
        return new LoadResult(
            new AppDataFile(),
            LoadStatus.Corrupt,
            $"数据文件无法解析，已保留为 {Path.GetFileName(quarantined)}；本次从空列表开始。");
    }

    private static bool TryRead(string path, out AppDataFile? data)
    {
        data = null;
        try
        {
            var json = File.ReadAllText(path, Encoding.UTF8);
            if (string.IsNullOrWhiteSpace(json))
            {
                return false;
            }

            data = JsonSerializer.Deserialize(json, Json.AppDataFile);
            return data is not null;
        }
        catch (Exception ex) when (ex is JsonException or NotSupportedException or IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    /// <summary>把坏文件改名保留供排查；返回新路径。</summary>
    private string Quarantine(string path)
    {
        var target = $"{path}.corrupt-{DateTime.Now:yyyyMMdd-HHmmss}";
        try
        {
            File.Move(path, target, overwrite: true);
            return target;
        }
        catch
        {
            return path;
        }
    }

    /// <summary>保存；失败抛 <see cref="PersistenceException"/>（不静默）。</summary>
    public void Save(AppDataFile data)
    {
        var json = JsonSerializer.Serialize(data, Json.AppDataFile);

        // 自检：序列化结果必须能还原，避免写入坏数据
        if (!CanDeserialize(json))
        {
            throw new PersistenceException("数据序列化自检失败，已取消保存以避免损坏文件。");
        }

        Exception? lastError = null;
        for (var attempt = 1; attempt <= 3; attempt++)
        {
            try
            {
                Directory.CreateDirectory(DirectoryPath);

                var temp = _filePath + ".tmp";
                File.WriteAllText(temp, json, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));

                // 读回校验，防止写入被截断
                if (!TryRead(temp, out var check) || check is null)
                {
                    throw new PersistenceException("写入后校验失败（文件可能被截断）。");
                }

                ReplaceWithRetry(temp, _filePath);

                // 主文件写好后同步一份备份（上次的备份已被本次覆盖）
                try { File.Copy(_filePath, BackupPath, overwrite: true); }
                catch { /* 备份失败不阻塞保存 */ }

                return;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                lastError = ex;
                Thread.Sleep(120 * attempt);
            }
        }

        throw new PersistenceException(
            $"无法写入 {_filePath}（{lastError?.Message}）。请检查磁盘空间、杀毒软件拦截，或文件是否被其它程序占用。",
            lastError);
    }

    private static void ReplaceWithRetry(string source, string target)
    {
        try
        {
            File.Move(source, target, overwrite: true);
            return;
        }
        catch (IOException)
        {
            // 目标被占用时退化为「先删后改名」
            Thread.Sleep(120);
            File.Delete(target);
            File.Move(source, target, overwrite: true);
        }
    }

    private static bool CanDeserialize(string json)
    {
        try
        {
            return JsonSerializer.Deserialize(json, Json.AppDataFile) is not null;
        }
        catch
        {
            return false;
        }
    }

    public static string Protect(string apiKey) =>
        Convert.ToBase64String(ProtectedData.Protect(Encoding.UTF8.GetBytes(apiKey), Entropy, DataProtectionScope.CurrentUser));

    public static string Unprotect(string protectedApiKey)
    {
        if (string.IsNullOrWhiteSpace(protectedApiKey))
        {
            return string.Empty;
        }

        try
        {
            var bytes = ProtectedData.Unprotect(Convert.FromBase64String(protectedApiKey), Entropy, DataProtectionScope.CurrentUser);
            return Encoding.UTF8.GetString(bytes);
        }
        catch (Exception ex) when (ex is FormatException or CryptographicException or NotSupportedException)
        {
            // 换用户/换机器后无法解密；界面会提示重新录入
            return string.Empty;
        }
    }
}
