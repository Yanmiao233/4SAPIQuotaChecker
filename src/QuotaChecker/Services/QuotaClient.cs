using System.Net.Http;
using System.Net.Http.Headers;
using QuotaChecker.Models;

namespace QuotaChecker.Services;

/// <summary>
/// 调用中转站的 <c>GET /api/usage/token</c> 查询密钥（令牌）用量。
/// </summary>
public sealed class QuotaClient
{
    private readonly HttpClient _http;

    public QuotaClient(HttpClient? http = null)
    {
        _http = http ?? new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
        _http.DefaultRequestHeaders.UserAgent.ParseAdd("QuotaChecker/1.0");
    }

    public async Task<TokenQuota> QueryAsync(string baseUrl, string apiKey, CancellationToken cancellationToken = default)
    {
        var uri = BuildUri(baseUrl);

        using var request = new HttpRequestMessage(HttpMethod.Get, uri);
        request.Headers.TryAddWithoutValidation("Authorization", "Bearer " + NormalizeKey(apiKey));

        HttpResponseMessage response;
        try
        {
            response = await _http.SendAsync(request, HttpCompletionOption.ResponseContentRead, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (TaskCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new QuotaQueryException($"请求超时（{uri.Host} 无响应）。");
        }
        catch (HttpRequestException ex)
        {
            throw new QuotaQueryException($"无法连接 {uri.Host}：{ex.Message}");
        }

        using (response)
        {
            var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);

            if (!response.IsSuccessStatusCode)
            {
                throw new QuotaQueryException($"接口返回 {(int)response.StatusCode} {response.ReasonPhrase}：{ExtractMessage(body)}");
            }

            return TokenQuotaParser.Parse(body);
        }
    }

    /// <summary>把用户填写的地址规范成 <c>.../api/usage/token</c>。</summary>
    public static Uri BuildUri(string baseUrl)
    {
        var value = (baseUrl ?? string.Empty).Trim();
        if (value.Length == 0)
        {
            value = AppSettings.DefaultBaseUrl;
        }

        if (!value.Contains("://", StringComparison.Ordinal))
        {
            value = "https://" + value;
        }

        value = value.TrimEnd('/');

        if (!value.EndsWith("/api/usage/token", StringComparison.OrdinalIgnoreCase))
        {
            value = value.EndsWith("/api", StringComparison.OrdinalIgnoreCase)
                ? value + "/usage/token"
                : value + "/api/usage/token";
        }

        if (!Uri.TryCreate(value, UriKind.Absolute, out var uri))
        {
            throw new QuotaQueryException($"接口地址「{baseUrl}」格式不正确。");
        }

        return uri;
    }

    /// <summary>请求头统一为 <c>Bearer sk-xxx</c>。</summary>
    public static string NormalizeKey(string apiKey)
    {
        var key = (apiKey ?? string.Empty).Trim().Trim('"');
        if (key.StartsWith("sk-", StringComparison.OrdinalIgnoreCase))
        {
            return key;
        }

        // 前缀容易被复制时丢掉，这里按 new-api 约定补回。
        return key.Length > 0 && !key.Contains(' ') ? "sk-" + key : key;
    }

    private static string ExtractMessage(string body)
    {
        if (string.IsNullOrWhiteSpace(body))
        {
            return "（空响应）";
        }

        var text = body.Trim();
        return text.Length <= 300 ? text : text[..300] + "…";
    }
}
