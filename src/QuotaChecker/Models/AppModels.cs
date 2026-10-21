using System.Text.Json;
using System.Text.Json.Serialization;
namespace QuotaChecker.Models;

/// <summary>持久化到本地的单个密钥配置。</summary>
public sealed class StoredKey
{
    public Guid Id { get; set; } = Guid.NewGuid();

    public string Name { get; set; } = string.Empty;

    public string BaseUrl { get; set; } = AppSettings.DefaultBaseUrl;

    /// <summary>DPAPI(当前用户) 加密后的 API Key，Base64 存储。</summary>
    public string ProtectedApiKey { get; set; } = string.Empty;

    public string Note { get; set; } = string.Empty;

    public bool Enabled { get; set; } = true;
}

/// <summary>换算参数：配额 → 美元 → 人民币。</summary>
public sealed class AppSettings
{
    public const string DefaultBaseUrl = "https://4sapi.com";

    /// <summary>new-api 的配额基数：500000 配额 = 官方计价 1 美元。</summary>
    public const double DefaultQuotaPerUsd = 500_000d;

    /// <summary>1 美元官方价格对应的人民币金额（即站点倍率，4sapi 基础汇率为 1:1）。</summary>
    public static double RateCnyPerUsd { get; set; } = 1d;

    /// <summary>多少配额折算为 1 美元（官方价格）。</summary>
    public static double QuotaPerUsd { get; set; } = DefaultQuotaPerUsd;
}

/// <summary>本地数据文件结构。</summary>
public sealed class AppDataFile
{
    public int Version { get; set; } = 1;

    public double RateCnyPerUsd { get; set; } = 1d;

    public double QuotaPerUsd { get; set; } = AppSettings.DefaultQuotaPerUsd;

    public List<StoredKey> Keys { get; set; } = new();

    [JsonIgnore]
    public string? LoadWarning { get; set; }
}

/// <summary><c>GET /api/usage/token</c> 的解析结果。</summary>
public sealed record TokenQuota(
    string TokenName,
    double TotalGranted,
    double TotalUsed,
    double TotalAvailable,
    bool UnlimitedQuota,
    DateTimeOffset? ExpiresAt,
    double? UserQuotaAvailable,
    double? UserQuotaUsed);

/// <summary>查询密钥额度失败时抛出。</summary>
public sealed class QuotaQueryException : Exception
{
    public QuotaQueryException(string message) : base(message)
    {
    }
}

/// <summary>把 new-api 的响应 JSON 解析为 <see cref="TokenQuota"/>。</summary>
public static class TokenQuotaParser
{
    public static TokenQuota Parse(string json)
    {
        JsonDocument doc;
        try
        {
            doc = JsonDocument.Parse(json);
        }
        catch (JsonException)
        {
            throw new QuotaQueryException("响应不是合法的 JSON，请确认接口地址是否正确。");
        }

        using (doc)
        {
            var root = doc.RootElement;

            if (root.TryGetProperty("data", out var data) && data.ValueKind == JsonValueKind.Object)
            {
                return FromData(data);
            }

            var message = root.TryGetProperty("message", out var m) && m.ValueKind == JsonValueKind.String
                ? m.GetString() ?? "接口未返回 data 字段。"
                : "接口未返回 data 字段。";
            throw new QuotaQueryException(message);
        }
    }

    private static TokenQuota FromData(JsonElement data)
    {
        var unlimited = GetBool(data, "unlimited_quota");

        double? userAvailable = null;
        double? userUsed = null;
        if (data.TryGetProperty("user_quota", out var userQuota) && userQuota.ValueKind == JsonValueKind.Object)
        {
            userAvailable = GetDouble(userQuota, "total_available");
            userUsed = GetDouble(userQuota, "total_used");
        }

        return new TokenQuota(
            TokenName: GetString(data, "name"),
            TotalGranted: GetDouble(data, "total_granted") ?? 0d,
            TotalUsed: GetDouble(data, "total_used") ?? 0d,
            TotalAvailable: GetDouble(data, "total_available") ?? 0d,
            UnlimitedQuota: unlimited,
            ExpiresAt: ToDateTimeOffset(GetDouble(data, "expires_at")),
            UserQuotaAvailable: userAvailable,
            UserQuotaUsed: userUsed);
    }

    private static string GetString(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString() ?? string.Empty
            : string.Empty;

    private static double? GetDouble(JsonElement element, string name)
    {
        if (!element.TryGetProperty(name, out var value))
        {
            return null;
        }

        return value.ValueKind switch
        {
            JsonValueKind.Number => value.GetDouble(),
            JsonValueKind.String when double.TryParse(value.GetString(), out var parsed) => parsed,
            _ => null,
        };
    }

    private static bool GetBool(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) &&
        (value.ValueKind == JsonValueKind.True ||
         (value.ValueKind == JsonValueKind.String && bool.TryParse(value.GetString(), out var parsed) && parsed));

    private static DateTimeOffset? ToDateTimeOffset(double? unixSeconds)
    {
        if (unixSeconds is not > 0)
        {
            return null;
        }

        try
        {
            return DateTimeOffset.FromUnixTimeSeconds((long)unixSeconds.Value);
        }
        catch (ArgumentOutOfRangeException)
        {
            return null;
        }
    }
}
