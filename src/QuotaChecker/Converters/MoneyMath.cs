using System.Globalization;
using QuotaChecker.Models;

namespace QuotaChecker.Converters;

/// <summary>
/// 配额 → 人民币 换算。
/// new-api 中 500000 配额 = 官方计价 1 美元；人民币金额 = 官方美元价 × 站点倍率（元/美元）。
/// </summary>
public static class MoneyMath
{
    /// <summary>配额折算为官方美元金额。</summary>
    public static double QuotaToUsd(double quota)
    {
        var perUsd = AppSettings.QuotaPerUsd;
        return perUsd <= 0 ? 0d : quota / perUsd;
    }

    /// <summary>配额折算为人民币金额。</summary>
    public static double QuotaToCny(double quota) => QuotaToUsd(quota) * AppSettings.RateCnyPerUsd;

    public static string FormatCny(double cny) => "¥" + cny.ToString("N4", CultureInfo.InvariantCulture);

    /// <summary>人民币四位小数不影响阅读时（≥100 元）收敛为两位。</summary>
    public static string FormatCnyCompact(double cny) =>
        Math.Abs(cny) >= 100 ? "¥" + cny.ToString("N2", CultureInfo.InvariantCulture) : FormatCny(cny);

    public static string FormatQuota(double quota) => quota.ToString("N0", CultureInfo.InvariantCulture);

    public static string FormatUsd(double usd) => "$" + usd.ToString("N4", CultureInfo.InvariantCulture);
}
