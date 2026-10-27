using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.UI.Dispatching;
using QuotaChecker.Converters;
using QuotaChecker.Models;
using QuotaChecker.Services;

namespace QuotaChecker.ViewModels;

/// <summary>主界面的状态与命令。</summary>
public sealed partial class MainViewModel : ObservableObject
{
    private readonly KeyStore _store;
    private readonly QuotaClient _client;
    private readonly UpdateService _updateService = new();
    private readonly UpdateState _updateState = new();
    private bool _isUpdatingDerived;
    private DispatcherQueueTimer? _autoRefreshTimer;

    /// <summary>启动读取失败时为 true：在用户重新录入前不写盘，避免覆盖原文件。</summary>
    private bool _loadFailed;

    private string? _loadWarning;

    /// <summary>是否处于「数据文件读取失败」状态。</summary>
    public bool HasLoadFailure => _loadFailed;

    /// <summary>读取失败时给用户的提示。</summary>
    public string LoadFailureText => _loadWarning ?? string.Empty;

    public MainViewModel(KeyStore store, QuotaClient client)
    {
        _store = store;
        _client = client;
    }

    public ObservableCollection<ApiKeyViewModel> Keys { get; } = new();

    [ObservableProperty]
    private bool _isBusy;

    /// <summary>站点倍率：官方计价 1 美元对应多少人民币。</summary>
    [ObservableProperty]
    private double _rateCnyPerUsd = 1d;

    /// <summary>自动刷新间隔（秒）；0 表示关闭。</summary>
    [ObservableProperty]
    private int _autoRefreshSeconds;

    /// <summary>自动检查更新间隔（分钟）；0 表示关闭自动检查。</summary>
    [ObservableProperty]
    private int _updateCheckIntervalMinutes = AppSettings.DefaultUpdateCheckMinutes;

    /// <summary>设置菜单里显示的更新状态文案。</summary>
    [ObservableProperty]
    private string _updateStatusText = string.Empty;

    [ObservableProperty]
    private string _statusMessage = string.Empty;

    [ObservableProperty]
    private string _dataFilePath = string.Empty;

    public string TotalCnyText => MoneyMath.FormatCnyCompact(TotalCny);

    public string TotalCnyCaption => "≈ " + MoneyMath.FormatUsd(MoneyMath.QuotaToUsd(TotalUsedQuota)) + " 官方计价";

    public string TotalQuotaText => MoneyMath.FormatQuota(TotalUsedQuota) + " 配额已消耗";

    public string TotalRemainingText => MoneyMath.FormatCnyCompact(TotalRemainingCny);

    public string TotalRemainingCaption => "不含无限额度密钥";

    public string TotalGrantedText => MoneyMath.FormatCnyCompact(TotalGrantedCny);

    public string TotalGrantedCaption => "所有非无限额度密钥之和";

    public string KeyCountText => $"共 {Keys.Count} 个密钥 · 启用 {Keys.Count(k => k.IsEnabled)} 个";

    public double TotalUsedQuota => Keys.Where(k => k.IsEnabled).Sum(k => k.UsedQuota);

    public double TotalCny => Keys.Where(k => k.IsEnabled).Sum(k => k.UsedCny);

    public double TotalRemainingCny => Keys.Where(k => k.IsEnabled && !k.IsUnlimited).Sum(k => k.RemainingCny);

    public double TotalGrantedCny => Keys.Where(k => k.IsEnabled && !k.IsUnlimited).Sum(k => k.GrantedCny);

    public void Initialize()
    {
        var loaded = _store.Load();
        var data = loaded.Data;
        DataFilePath = _store.FilePath;

        // 读取失败时禁止自动写回，否则会把原有文件覆盖成空列表（密钥就"消失"了）
        _loadFailed = loaded.Status is LoadStatus.Corrupt;
        _loadWarning = loaded.Message;

        AppSettings.RateCnyPerUsd = data.RateCnyPerUsd > 0 ? data.RateCnyPerUsd : 1d;
        AppSettings.QuotaPerUsd = data.QuotaPerUsd > 0 ? data.QuotaPerUsd : AppSettings.DefaultQuotaPerUsd;
        AppSettings.AutoRefreshSeconds = Math.Max(0, data.AutoRefreshSeconds);
        RateCnyPerUsd = AppSettings.RateCnyPerUsd;
        AutoRefreshSeconds = AppSettings.AutoRefreshSeconds;

        _updateState.Load(
            data.UpdateCheckIntervalMinutes,
            data.LastUpdateCheckUtc,
            data.CachedUpdate,
            data.SkippedVersion);
        UpdateCheckIntervalMinutes = _updateState.CheckIntervalMinutes;
        RefreshUpdateStatusText();

        Keys.Clear();
        foreach (var key in data.Keys)
        {
            AddViewModel(key);
        }

        StatusMessage = loaded.Message ?? (Keys.Count == 0
            ? "点击「添加密钥」开始统计，密钥用 DPAPI 加密后仅保存在本机。"
            : $"已载入 {Keys.Count} 个密钥，正在自动查询额度…");

        UpdateDerived();
    }

    /// <summary>启动时先查一次，随后按设置的间隔开启自动刷新。</summary>
    public void StartBackgroundRefresh()
    {
        if (Keys.Any(k => k.IsEnabled))
        {
            _ = RefreshAllAsync();
        }

        StartAutoRefreshTimer();
    }

    /// <summary>添加一个密钥并立即查询额度。</summary>
    public async Task AddKeyAsync(string name, string baseUrl, string apiKey)
    {
        var stored = new StoredKey
        {
            Name = string.IsNullOrWhiteSpace(name) ? "未命名密钥" : name.Trim(),
            BaseUrl = string.IsNullOrWhiteSpace(baseUrl) ? AppSettings.DefaultBaseUrl : baseUrl.Trim(),
            ProtectedApiKey = KeyStore.Protect(apiKey.Trim()),
        };

        var vm = AddViewModel(stored);
        // 用户主动录入 → 视为接受覆盖，解除「读取失败不写盘」保护
        _loadFailed = false;
        Persist();
        UpdateDerived();

        StatusMessage = LastSaveError.Length > 0
            ? $"密钥已添加，但保存失败：{LastSaveError}"
            : RelocationNotice.Length > 0
                ? $"已添加「{vm.Name}」。{RelocationNotice}"
                : $"已添加「{vm.Name}」，正在查询额度…";

        await vm.RefreshCoreAsync().ConfigureAwait(true);
        UpdateDerived();
    }

    public void RemoveKey(ApiKeyViewModel key)
    {
        key.PropertyChanged -= OnKeyPropertyChanged;
        Keys.Remove(key);
        Persist();
        UpdateDerived();
        StatusMessage = $"已删除「{key.Name}」。";
    }

    [RelayCommand]
    private async Task RefreshAllAsync()
    {
        if (IsBusy)
        {
            return;
        }

        var targets = Keys.Where(k => k.IsEnabled).ToList();
        if (targets.Count == 0)
        {
            StatusMessage = Keys.Count == 0 ? "还没有密钥，请先添加。" : "没有启用的密钥。";
            return;
        }

        IsBusy = true;
        StatusMessage = $"正在查询 {targets.Count} 个密钥…";
        try
        {
            await Task.WhenAll(targets.Select(k => k.RefreshCoreAsync())).ConfigureAwait(true);
            UpdateDerived();

            var failed = targets.Count(k => k.HasError);
            StatusMessage = failed == 0
                ? $"刷新完成（{DateTime.Now:HH:mm:ss}），{targets.Count} 个密钥全部成功。"
                : $"刷新完成（{DateTime.Now:HH:mm:ss}），{failed}/{targets.Count} 个密钥查询失败。";
        }
        finally
        {
            IsBusy = false;
        }
    }

    public void ApplyRate()
    {
        if (RateCnyPerUsd <= 0)
        {
            RateCnyPerUsd = 1d;
        }

        var changed = Math.Abs(AppSettings.RateCnyPerUsd - RateCnyPerUsd) > double.Epsilon;
        AppSettings.RateCnyPerUsd = RateCnyPerUsd;
        Persist();
        UpdateDerived();

        if (changed)
        {
            StatusMessage = $"站点倍率已更新：官方 1 美元 = ¥{RateCnyPerUsd.ToString("N2", CultureInfo.InvariantCulture)}";
        }
    }

    /// <summary>套用自动刷新间隔（秒，0 = 关闭）并重启定时器。</summary>
    public void ApplyAutoRefreshSeconds(int seconds)
    {
        AutoRefreshSeconds = Math.Max(0, seconds);
        AppSettings.AutoRefreshSeconds = AutoRefreshSeconds;
        Persist();
        StartAutoRefreshTimer();

        StatusMessage = AutoRefreshSeconds <= 0
            ? "已关闭自动刷新。"
            : $"已开启自动刷新：每 {FormatInterval(AutoRefreshSeconds)}刷新一次。";
    }

    // ---------------- 更新相关 ----------------

/// <summary>缓存的更新信息（可能为 null）。</summary>
public CachedUpdate? CachedUpdate => _updateState.Cache;

/// <summary>是否存在用户未跳过的可用新版本。</summary>
public bool HasPendingUpdate => _updateState.HasPendingUpdate;

/// <summary>刷新设置菜单里的更新状态文案。</summary>
public void RefreshUpdateStatusText()
{
    UpdateStatusText = _updateState.BuildStatusText();
}

/// <summary>套用自动检查更新间隔（分钟，0 = 关闭）并持久化。</summary>
public void ApplyUpdateCheckInterval(int minutes)
{
    _updateState.ApplyInterval(minutes);
    UpdateCheckIntervalMinutes = _updateState.CheckIntervalMinutes;
    Persist();
    RefreshUpdateStatusText();

    StatusMessage = UpdateCheckIntervalMinutes <= 0
        ? "已关闭自动检查更新。"
        : $"自动检查更新：每 {FormatMinutes(UpdateCheckIntervalMinutes)}一次。";

    if (UpdateCheckIntervalMinutes > 0)
    {
        _ = MaybeAutoCheckUpdateAsync();
    }
}

/// <summary>
/// 获取最新版本信息。<paramref name="force"/> 为 false 且缓存未过期时直接用缓存，
/// 避免重复联网。返回 null 表示已是最新（或缓存显示已是最新）。
/// </summary>
public async Task<UpdateInfo?> CheckForUpdatesAsync(bool force, CancellationToken cancellationToken = default)
{
    if (!force && !_updateState.IsCacheStale())
    {
        var cachedInfo = _updateState.ToCachedInfo();
        if (cachedInfo is not null)
        {
            return UpdateService.IsNewer(cachedInfo.Tag, UpdateService.CurrentVersion) ? cachedInfo : null;
        }
    }

    var info = await _updateService.CheckLatestAsync(cancellationToken).ConfigureAwait(true);

    _updateState.ApplyResult(info);
    Persist();
    RefreshUpdateStatusText();
    return info;
}

/// <summary>用户选择跳过某版本（不再提示，直到出现更高版本）。</summary>
public void SkipVersion(string version)
{
    _updateState.Skip(version);
    Persist();
    RefreshUpdateStatusText();

    StatusMessage = _updateState.SkippedVersion.Length == 0
        ? "已取消跳过。"
        : $"已跳过 v{_updateState.SkippedVersion}，出现更高版本时会再次提示。";
}

/// <summary>启动后按设置的间隔静默检查一次（不打扰用户）。</summary>
public async Task MaybeAutoCheckUpdateAsync()
{
    if (_updateState.CheckIntervalMinutes <= 0 || !_updateState.IsCacheStale())
    {
        return;
    }

    try
    {
        await CheckForUpdatesAsync(force: true).ConfigureAwait(true);
        if (HasPendingUpdate)
        {
            StatusMessage = $"发现新版本 v{_updateState.PendingVersion}，可在「设置 → 检查更新」中升级。";
        }
    }
    catch (Exception ex)
    {
        UpdateStatusText = "更新检查失败（可手动重试）";
        StatusMessage = "自动检查更新失败：" + ex.Message;
    }
}

/// <summary>把分钟数说成人话。</summary>
public static string FormatMinutes(int minutes) => minutes switch
{
    <= 0 => "关闭",
    < 60 => $"{minutes} 分钟",
    _ when minutes % 60 == 0 => $"{minutes / 60} 小时",
    _ => $"{minutes / 60} 小时 {minutes % 60} 分钟",
};
/// <summary>按当前间隔启动（或停止）自动刷新定时器。必须在 UI 线程调用。</summary>
    public void StartAutoRefreshTimer()
    {
        var queue = DispatcherQueue.GetForCurrentThread();
        if (queue is null)
        {
            return;
        }

        _autoRefreshTimer ??= queue.CreateTimer();
        _autoRefreshTimer.Stop();
        _autoRefreshTimer.Tick -= OnAutoRefreshTick;

        if (AutoRefreshSeconds <= 0)
        {
            return;
        }

        _autoRefreshTimer.Interval = TimeSpan.FromSeconds(AutoRefreshSeconds);
        _autoRefreshTimer.IsRepeating = true;
        _autoRefreshTimer.Tick += OnAutoRefreshTick;
        _autoRefreshTimer.Start();
    }

    private async void OnAutoRefreshTick(DispatcherQueueTimer sender, object args)
    {
        // 定时器回调里抛出的异常会直接终止进程，必须全部兜住。
        try
        {
            if (IsBusy || !Keys.Any(k => k.IsEnabled))
            {
                return;
            }

            await RefreshAllAsync().ConfigureAwait(true);
            StatusMessage = $"自动刷新完成（{DateTime.Now:HH:mm:ss}），下次 {FormatInterval(AutoRefreshSeconds)}后。";
        }
        catch (Exception ex)
        {
            StatusMessage = "自动刷新出错：" + ex.Message;
        }
    }

    /// <summary>把秒数说成人话，例如 60 -> "1 分钟"。</summary>
    public static string FormatInterval(int seconds) => seconds switch
    {
        <= 0 => "关闭",
        < 60 => $"{seconds} 秒",
        _ when seconds % 60 == 0 => $"{seconds / 60} 分钟",
        _ => $"{seconds / 60} 分 {seconds % 60} 秒",
    };

    public void Persist()
    {
        var data = new AppDataFile
        {
            RateCnyPerUsd = AppSettings.RateCnyPerUsd,
            QuotaPerUsd = AppSettings.QuotaPerUsd,
            AutoRefreshSeconds = AppSettings.AutoRefreshSeconds,
            UpdateCheckIntervalMinutes = _updateState.CheckIntervalMinutes,
            LastUpdateCheckUtc = _updateState.LastCheckUtc,
            CachedUpdate = _updateState.Cache,
            SkippedVersion = _updateState.SkippedVersion,
            Keys = Keys.Select(k => k.ToStoredKey()).ToList(),
        };

        if (_loadFailed)
        {
            // 直接写盘会覆盖用户原有数据，这里拒绝；用户重新添加密钥后会自动解除
            StatusMessage = "数据文件读取失败，已暂停自动保存以保护原有数据。";
            return;
        }

        try
        {
            _store.Save(data);
            LastSaveError = string.Empty;
        }
        catch (Exception ex)
        {
            // 主位置不可写时自动换到可写位置再试一次（企业策略/受控文件夹访问常见）
            if (_store.TryRelocate(out var newPath))
            {
                try
                {
                    _store.Save(data);
                    LastSaveError = string.Empty;
                    DataFilePath = newPath;
                    RelocationNotice = $"原位置无法写入，数据已改存到：{newPath}";
                    StatusMessage = RelocationNotice;
                    return;
                }
                catch (Exception retryEx)
                {
                    ex = retryEx;
                }
            }

            LastSaveError = ex.Message;
            StatusMessage = "本地数据保存失败：" + ex.Message + Environment.NewLine +
                            "可在「设置」里查看数据目录，或手动指定可写位置。";
        }
    }

    /// <summary>最近一次保存失败的原因（空表示正常）。</summary>
    public string LastSaveError { get; private set; } = string.Empty;

    /// <summary>因原位置不可写而改存到新位置时的提示（空表示未发生）。</summary>
    public string RelocationNotice { get; private set; } = string.Empty;

    /// <summary>解除「读取失败时禁止写盘」状态并立即保存。</summary>
    public void AllowSaveAfterLoadFailure()
    {
        _loadFailed = false;
        Persist();
    }

    public void UpdateDerived()
    {
        // RecomputeMoney 会触发 PropertyChanged，若不设防会与 OnKeyPropertyChanged 相互递归。
        if (_isUpdatingDerived)
        {
            return;
        }

        _isUpdatingDerived = true;
        try
        {
            foreach (var key in Keys)
            {
                key.RecomputeMoney();
            }

            OnPropertyChanged(nameof(TotalCnyText));
            OnPropertyChanged(nameof(TotalCnyCaption));
            OnPropertyChanged(nameof(TotalQuotaText));
            OnPropertyChanged(nameof(TotalRemainingText));
            OnPropertyChanged(nameof(TotalRemainingCaption));
            OnPropertyChanged(nameof(TotalGrantedText));
            OnPropertyChanged(nameof(TotalGrantedCaption));
            OnPropertyChanged(nameof(KeyCountText));
        }
        finally
        {
            _isUpdatingDerived = false;
        }
    }

    private ApiKeyViewModel AddViewModel(StoredKey stored)
    {
        var vm = new ApiKeyViewModel(stored, _client, this);
        vm.PropertyChanged += OnKeyPropertyChanged;
        Keys.Add(vm);
        return vm;
    }

    private void OnKeyPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(ApiKeyViewModel.IsEnabled))
        {
            Persist();
        }

        UpdateDerived();
    }
}
