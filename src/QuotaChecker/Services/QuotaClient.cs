using System.Net;
using System.Net.Http;
using QuotaChecker.Models;

namespace QuotaChecker.Services;

/// <summary>
/// 调用中转站接口查询密钥（令牌）用量与用量明细：
/// <c>GET /api/usage/token/</c>（额度）、<c>GET /api/log/token</c>（最近日志）。
/// </summary>
public sealed class QuotaClient
{
    /// <summary>
    /// new-api 的路由注册为 <c>usageRoute.Group("/token").GET("/")</c>，即带尾斜杠的
    /// <c>/api/usage/token/</c>。请求不带尾斜杠时 Gin 会 301 重定向，而重定向会丢掉
    /// Authorization 头，服务端于是返回 401 "Token not provided"。
    /// 这里统一补上尾斜杠，从根上避免这次重定向。
    /// </summary>
    private const string BareEndpoint = "/api/usage/token";

    private const int MaxRedirects = 5;

    private readonly HttpClient _http;

    public QuotaClient(HttpClient? http = null)
    {
        // 关闭自动重定向：改由本类自己跟随，才能在跳转后保留 Authorization 头。
        _http = http ?? new HttpClient(new SocketsHttpHandler { AllowAutoRedirect = false })
        {
            Timeout = TimeSpan.FromSeconds(30),
        };

        if (!_http.DefaultRequestHeaders.UserAgent.Any())
        {
            _http.DefaultRequestHeaders.UserAgent.ParseAdd("QuotaChecker/1.0");
        }
    }

    public async Task<TokenQuota> QueryAsync(string baseUrl, string apiKey, CancellationToken cancellationToken = default)
    {
        var quota = await SendForJsonAsync(BuildUri(baseUrl), apiKey, cancellationToken).ConfigureAwait(false);
        return TokenQuotaParser.Parse(quota);
    }

    /// <summary>
    /// 查询该密钥的最近调用日志（<c>GET /api/log/token</c>，最多 MaxRecentItems 条）。
    /// 注意：new-api 里这条路由注册为 <em>不带</em> 尾斜杠，与 usage 那条正好相反。
    /// </summary>
    public async Task<List<ModelUsage>> GetModelUsageAsync(string baseUrl, string apiKey, CancellationToken cancellationToken = default)
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
        if (value.EndsWith("/api", StringComparison.OrdinalIgnoreCase))
        {
            value += "/log/token";
        }
        else
        {
            value += "/api/log/token";
        }

        if (!Uri.TryCreate(value, UriKind.Absolute, out var uri))
        {
            throw new QuotaQueryException($"接口地址「{baseUrl}」格式不正确。");
        }

        var body = await SendForJsonAsync(uri, apiKey, cancellationToken).ConfigureAwait(false);
        return TokenLogParser.ParseAndAggregate(body);
    }

    /// <summary>发 GET 并返回响应体；手动跟随安全的重定向并保留 Authorization。</summary>
    private async Task<string> SendForJsonAsync(Uri uri, string apiKey, CancellationToken cancellationToken)
    {
        var normalizedKey = NormalizeKey(apiKey);
        if (normalizedKey.Length == 0)
        {
            throw new QuotaQueryException("没有填写 API Key，无法查询。");
        }

        var authorization = "Bearer " + normalizedKey;

        for (var hop = 0; ; hop++)
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, uri);
            request.Headers.TryAddWithoutValidation("Authorization", authorization);

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
                if (IsRedirect(response.StatusCode) && response.Headers.Location is { } location)
                {
                    if (hop >= MaxRedirects)
                    {
                        throw new QuotaQueryException($"重定向次数过多（超过 {MaxRedirects} 次），请检查接口地址。");
                    }

                    var next = new Uri(uri, location);
                    if (!CanFollowRedirect(uri, next, out var reason))
                    {
                        throw new QuotaQueryException(
                            $"站点把接口重定向到了 {next.GetLeftPart(UriPartial.Authority)}（{reason}），" +
                            "出于安全考虑已停止，密钥没有发出。请把界面上的「接口地址」直接改成该地址。");
                    }

                    uri = next;
                    continue;
                }

                var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);

                if (response.StatusCode == HttpStatusCode.Unauthorized)
                {
                    throw new QuotaQueryException($"接口返回 401 Unauthorized：{ExtractMessage(body)}\n（请求地址：{uri}）");
                }

                if (!response.IsSuccessStatusCode)
                {
                    throw new QuotaQueryException($"接口返回 {(int)response.StatusCode} {response.ReasonPhrase}：{ExtractMessage(body)}");
                }

                return body;
            }
        }
    }

    /// <summary>把用户填写的地址规范成 <c>.../api/usage/token/</c>（带尾斜杠，与 new-api 路由一致）。</summary>
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

        if (value.EndsWith(BareEndpoint, StringComparison.OrdinalIgnoreCase))
        {
            value += "/";
        }
        else if (value.EndsWith("/api", StringComparison.OrdinalIgnoreCase))
        {
            value += "/usage/token/";
        }
        else
        {
            value += BareEndpoint + "/";
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
        if (key.Length == 0)
        {
            return string.Empty;
        }

        if (key.StartsWith("sk-", StringComparison.OrdinalIgnoreCase))
        {
            return key;
        }

        // 前缀容易被复制时丢掉，这里按 new-api 约定补回。
        return key.Contains(' ') ? key : "sk-" + key;
    }

    private static bool IsRedirect(HttpStatusCode status) =>
        status is HttpStatusCode.MovedPermanently
            or HttpStatusCode.Found
            or HttpStatusCode.SeeOther
            or HttpStatusCode.TemporaryRedirect
            or HttpStatusCode.PermanentRedirect;

    /// <summary>
    /// 只跟随同主机同端口、且不降级到明文 HTTP 的跳转。
    /// 其余情况一律拒绝，避免把密钥发送到第三方站点。
    /// </summary>
    private static bool CanFollowRedirect(Uri from, Uri to, out string reason)
    {
        reason = string.Empty;

        if (to.Scheme is not ("http" or "https"))
        {
            reason = "目标不是 http/https";
            return false;
        }

        if (!string.Equals(from.Host, to.Host, StringComparison.OrdinalIgnoreCase))
        {
            reason = $"主机由 {from.Host} 变成了 {to.Host}";
            return false;
        }

        if (from.Port != to.Port)
        {
            reason = $"端口由 {from.Port} 变成了 {to.Port}";
            return false;
        }

        if (from.Scheme == Uri.UriSchemeHttps && to.Scheme == Uri.UriSchemeHttp)
        {
            reason = "由 https 降级为 http";
            return false;
        }

        return true;
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
