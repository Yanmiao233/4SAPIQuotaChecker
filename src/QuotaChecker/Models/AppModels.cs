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
    public const string DefaultBaseUrl = "https://4sapi.org";

    /// <summary>new-api 的配额基数：500000 配额 = 官方计价 1 美元。</summary>
    public const double DefaultQuotaPerUsd = 500_000d;

    /// <summary>1 美元官方价格对应的人民币金额（即站点倍率，4sapi 基础汇率为 1:1）。</summary>
    public static double RateCnyPerUsd { get; set; } = 1d;

    /// <summary>多少配额折算为 1 美元（官方价格）。</summary>
    public static double QuotaPerUsd { get; set; } = DefaultQuotaPerUsd;

    /// <summary>自动刷新间隔（秒）；0 表示关闭。</summary>
    public static int AutoRefreshSeconds { get; set; }
}

/// <summary>本地数据文件结构。</summary>
public sealed class AppDataFile
{
    public int Version { get; set; } = 1;

    public double RateCnyPerUsd { get; set; } = 1d;

    public double QuotaPerUsd { get; set; } = AppSettings.DefaultQuotaPerUsd;

    /// <summary>自动刷新间隔（秒）；0 表示关闭。</summary>
    public int AutoRefreshSeconds { get; set; }

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

/// <summary>某个模型的消耗汇总（由日志聚合而来）。</summary>
public sealed record ModelUsage(
    string ModelName,
    double Quota,
    long Calls,
    long PromptTokens,
    long CompletionTokens)
{
    public string QuotaCnyText => Converters.MoneyMath.FormatCnyCompact(Converters.MoneyMath.QuotaToCny(Quota));

    public string QuotaText => Converters.MoneyMath.FormatQuota(Quota);

    public string CallsText => Calls.ToString("N0", System.Globalization.CultureInfo.InvariantCulture);

    public string TokensText => PromptTokens + CompletionTokens <= 0
        ? string.Empty
        : $"{Converters.MoneyMath.FormatQuota(PromptTokens + CompletionTokens)} tokens";
}

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

/// <summary>解析 <c>GET /api/log/token</c> 的日志数组，并按模型聚合消耗。</summary>
public static class TokenLogParser
{
    /// <summary>服务端 new-api 的 MaxRecentItems 上限。</summary>
    public const int MaxRecentItems = 1000;

    public static List<ModelUsage> ParseAndAggregate(string json)
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

            if (!root.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Array)
            {
                var message = root.TryGetProperty("message", out var m) && m.ValueKind == JsonValueKind.String
                    ? m.GetString() ?? "接口未返回日志数据。"
                    : "接口未返回日志数据。";
                throw new QuotaQueryException(message);
            }

            var byModel = new Dictionary<string, (double Quota, long Calls, long Prompt, long Completion)>(StringComparer.OrdinalIgnoreCase);

            foreach (var entry in data.EnumerateArray())
            {
                // type=2 是消费日志（new-api LogTypeConsume）
                if (entry.TryGetProperty("type", out var type) && type.ValueKind == JsonValueKind.Number && type.GetInt32() != 2)
                {
                    continue;
                }

                var model = entry.TryGetProperty("model_name", out var mn) && mn.ValueKind == JsonValueKind.String
                    ? mn.GetString() ?? string.Empty
                    : string.Empty;
                if (model.Length == 0)
                {
                    model = "(未知模型)";
                }

                var quota = GetDouble(entry, "quota") ?? 0d;
                var prompt = (long)(GetDouble(entry, "prompt_tokens") ?? 0d);
                var completion = (long)(GetDouble(entry, "completion_tokens") ?? 0d);

                var (q, c, p, cm) = byModel.TryGetValue(model, out var cur)
                    ? cur
                    : (0d, 0L, 0L, 0L);
                byModel[model] = (q + quota, c + 1, p + prompt, cm + completion);
            }

            return byModel
                .OrderByDescending(kv => kv.Value.Quota)
                .Select(kv => new ModelUsage(kv.Key, kv.Value.Quota, kv.Value.Calls, kv.Value.Prompt, kv.Value.Completion))
                .ToList();
        }
    }

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
}
