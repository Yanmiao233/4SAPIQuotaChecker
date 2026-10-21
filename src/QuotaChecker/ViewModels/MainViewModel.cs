using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using QuotaChecker.Converters;
using QuotaChecker.Models;
using QuotaChecker.Services;

namespace QuotaChecker.ViewModels;

/// <summary>主界面的状态与命令。</summary>
public sealed partial class MainViewModel : ObservableObject
{
    private readonly KeyStore _store;
    private readonly QuotaClient _client;
    private bool _isUpdatingDerived;

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
        var data = _store.Load();
        DataFilePath = _store.FilePath;

        AppSettings.RateCnyPerUsd = data.RateCnyPerUsd > 0 ? data.RateCnyPerUsd : 1d;
        AppSettings.QuotaPerUsd = data.QuotaPerUsd > 0 ? data.QuotaPerUsd : AppSettings.DefaultQuotaPerUsd;
        RateCnyPerUsd = AppSettings.RateCnyPerUsd;

        Keys.Clear();
        foreach (var key in data.Keys)
        {
            AddViewModel(key);
        }

        StatusMessage = data.LoadWarning ?? (Keys.Count == 0
            ? "点击「添加密钥」开始统计，密钥用 DPAPI 加密后仅保存在本机。"
            : $"已载入 {Keys.Count} 个密钥，正在自动查询额度…");

        UpdateDerived();
    }

    /// <summary>启动后自动查询所有启用的密钥。</summary>
    public void StartAutoRefresh()
    {
        if (Keys.Any(k => k.IsEnabled))
        {
            _ = RefreshAllAsync();
        }
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
        Persist();
        UpdateDerived();
        StatusMessage = $"已添加「{vm.Name}」，正在查询额度…";
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

    public void Persist()
    {
        var data = new AppDataFile
        {
            RateCnyPerUsd = AppSettings.RateCnyPerUsd,
            QuotaPerUsd = AppSettings.QuotaPerUsd,
            Keys = Keys.Select(k => k.ToStoredKey()).ToList(),
        };

        try
        {
            _store.Save(data);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            StatusMessage = "本地数据保存失败：" + ex.Message;
        }
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
