using QuotaChecker.Models;

namespace QuotaChecker.Services;

/// <summary>
/// 更新检查的本地状态（检查间隔、上次检查时间、版本缓存、跳过的版本）。
/// 纯 C#，不依赖 UI，便于自动化测试；持久化由调用方负责。
/// </summary>
public sealed class UpdateState
{
    public int CheckIntervalMinutes { get; private set; } = AppSettings.DefaultUpdateCheckMinutes;

    public DateTimeOffset? LastCheckUtc { get; private set; }

    public CachedUpdate? Cache { get; private set; }

    public string SkippedVersion { get; private set; } = string.Empty;

    /// <summary>当前安装版本。</summary>
    public string CurrentVersion { get; private set; } = UpdateService.CurrentVersion;

    public void Load(int intervalMinutes, DateTimeOffset? lastCheckUtc, CachedUpdate? cache, string? skippedVersion)
    {
        CheckIntervalMinutes = intervalMinutes < 0 ? AppSettings.DefaultUpdateCheckMinutes : intervalMinutes;
        LastCheckUtc = lastCheckUtc;
        Cache = cache;
        SkippedVersion = skippedVersion ?? string.Empty;
        CurrentVersion = UpdateService.CurrentVersion;
    }

    public void ApplyInterval(int minutes) => CheckIntervalMinutes = Math.Max(0, minutes);

    /// <summary>缓存是否已过期（间隔为 0 时永不自动检查，视为"不过期"）。</summary>
    public bool IsCacheStale(DateTimeOffset? now = null)
    {
        if (CheckIntervalMinutes <= 0)
        {
            return false;
        }

        if (Cache is not { HasValue: true } cache)
        {
            return true;
        }

        return (now ?? DateTimeOffset.UtcNow) - cache.CheckedAt >= TimeSpan.FromMinutes(CheckIntervalMinutes);
    }

    /// <summary>是否有用户未跳过、且确实比当前版本新的版本。</summary>
    public bool HasPendingUpdate =>
        Cache is { HasValue: true } c &&
        !string.Equals(c.Version, SkippedVersion, StringComparison.OrdinalIgnoreCase) &&
        UpdateService.IsNewer(c.Version, CurrentVersion);

    /// <summary>待安装版本号；无则 null。</summary>
    public string? PendingVersion => HasPendingUpdate ? Cache!.Version : null;

    /// <summary>记录一次检查结果（null 表示已是最新）。</summary>
    public void ApplyResult(UpdateInfo? info, DateTimeOffset? now = null)
    {
        var checkedAt = now ?? DateTimeOffset.UtcNow;
        LastCheckUtc = checkedAt;

        Cache = info is null
            ? new CachedUpdate { Version = UpdateService.CurrentVersion, CheckedAt = checkedAt }
            : new CachedUpdate
            {
                Version = info.Tag,
                Notes = info.Notes,
                AssetUrl = info.AssetUrl,
                AssetBytes = info.AssetBytes,
                Sha256 = info.Sha256,
                PageUrl = info.PageUrl,
                CheckedAt = checkedAt,
            };
    }

    public void Skip(string version) => SkippedVersion = version ?? string.Empty;

    /// <summary>把缓存内容还原成 <see cref="UpdateInfo"/>（用于展示，不再联网）。</summary>
    public UpdateInfo? ToCachedInfo()
    {
        if (Cache is not { HasValue: true } c)
        {
            return null;
        }

        return new UpdateInfo(c.Version, c.Notes, c.AssetUrl, c.AssetBytes, c.Sha256, c.PageUrl);
    }

    /// <summary>设置菜单里的状态文案。</summary>
    public string BuildStatusText()
    {
        var when = LastCheckUtc is { } t ? $"（上次检查 {t.ToLocalTime():MM-dd HH:mm}）" : string.Empty;

        if (HasPendingUpdate)
        {
            return $"发现新版本 v{Cache!.Version}{when}";
        }

        return Cache is { HasValue: true } ? $"已是最新版本{when}" : "尚未检查更新";
    }
}
