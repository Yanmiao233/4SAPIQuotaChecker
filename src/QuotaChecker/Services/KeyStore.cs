using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using QuotaChecker.Models;

namespace QuotaChecker.Services;

/// <summary>
/// 把密钥列表与换算参数保存到 <c>%LOCALAPPDATA%\QuotaChecker\keys.json</c>。
/// API Key 使用 DPAPI（仅当前用户可解密）加密后落盘，不保存明文。
/// </summary>
public sealed class KeyStore
{
    private static readonly byte[] Entropy = Encoding.UTF8.GetBytes("QuotaChecker.v1");

    private readonly string _filePath;

    public KeyStore(string? filePath = null)
    {
        _filePath = filePath ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "QuotaChecker",
            "keys.json");
    }

    public string FilePath => _filePath;

    public AppDataFile Load()
    {
        if (!File.Exists(_filePath))
        {
            return new AppDataFile();
        }

        try
        {
            var json = File.ReadAllText(_filePath, Encoding.UTF8);
            var data = JsonSerializer.Deserialize<AppDataFile>(json, CreateOptions()) ?? new AppDataFile();
            return data;
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
        {
            return new AppDataFile { LoadWarning = $"本地数据读取失败，已从空列表开始：{ex.Message}" };
        }
    }

    public void Save(AppDataFile data)
    {
        var directory = Path.GetDirectoryName(_filePath);
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        var json = JsonSerializer.Serialize(data, CreateOptions());

        // 先写临时文件再替换，避免写入过程中崩溃导致配置损坏。
        var temp = _filePath + ".tmp";
        File.WriteAllText(temp, json, Encoding.UTF8);
        File.Move(temp, _filePath, overwrite: true);
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
        catch (Exception ex) when (ex is FormatException or CryptographicException)
        {
            // 例如换用户/换机器后无法解密，返回空串由界面提示重新录入。
            return string.Empty;
        }
    }

    private static JsonSerializerOptions CreateOptions() => new()
    {
        WriteIndented = true,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };
}
