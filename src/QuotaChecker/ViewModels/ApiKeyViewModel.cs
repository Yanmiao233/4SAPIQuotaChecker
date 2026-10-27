using System.Collections.ObjectModel;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using QuotaChecker.Converters;
using QuotaChecker.Models;
using QuotaChecker.Services;

namespace QuotaChecker.ViewModels;

/// <summary>列表中的单个密钥及其额度状态。</summary>
public sealed partial class ApiKeyViewModel : ObservableObject
{
    private readonly QuotaClient _client;
    private readonly MainViewModel _owner;
    private readonly string _protectedApiKey;

    public ApiKeyViewModel(StoredKey stored, QuotaClient client, MainViewModel owner)
    {
        _client = client;
        _owner = owner;
        _protectedApiKey = stored.ProtectedApiKey;

        Id = stored.Id;
        Name = stored.Name;
        BaseUrl = stored.BaseUrl;
        ApiKeyMasked = MaskApiKey(KeyStore.Unprotect(stored.ProtectedApiKey));
        CredentialState = ApiKeyMasked.Length == 0 ? "密钥无法解密" : "尚未查询";
        IsEnabled = stored.Enabled;
    }

    public Guid Id { get; }

    public string Name { get; }

    public string BaseUrl { get; }

    public string ApiKeyMasked { get; }

    public string ApiKeyDisplay => ApiKeyMasked.Length == 0 ? "（未保存密钥）" : ApiKeyMasked;

    public string DetailText => $"{BaseUrl} · {ApiKeyDisplay}";

    [ObservableProperty]
    private bool _isEnabled = true;

    [ObservableProperty]
    private bool _isUnlimited;

    [ObservableProperty]
    private bool _isLoading;

    [ObservableProperty]
    private string _credentialState = "尚未查询";

    [ObservableProperty]
    private string _tokenName = string.Empty;

    [ObservableProperty]
    private double _grantedQuota;

    [ObservableProperty]
    private double _usedQuota;

    [ObservableProperty]
    private double _remainingQuota;

    [ObservableProperty]
    private double? _userRemainingQuota;

    [ObservableProperty]
    private DateTimeOffset? _expiresAt;

    [ObservableProperty]
    private string _errorMessage = string.Empty;

    public bool HasError => ErrorMessage.Length > 0;

    public double UsedCny => MoneyMath.QuotaToCny(UsedQuota);

    public double GrantedCny => MoneyMath.QuotaToCny(GrantedQuota);

    public double RemainingCny => MoneyMath.QuotaToCny(RemainingQuota);

    /// <summary>已用占比（0-100），用于进度条。</summary>
    public double ProgressValue => IsUnlimited || GrantedQuota <= 0
        ? 0d
        : Math.Clamp(UsedQuota / GrantedQuota * 100d, 0d, 100d);

    public string UsedCnyText => MoneyMath.FormatCnyCompact(UsedCny);

    public string GrantedCnyText => IsUnlimited ? "无限额度" : MoneyMath.FormatCnyCompact(GrantedCny);

    public string RemainingCnyText => IsUnlimited ? "无限额度" : MoneyMath.FormatCnyCompact(RemainingCny);

    public string UsedQuotaText => MoneyMath.FormatQuota(UsedQuota);

    public string GrantedQuotaText => IsUnlimited ? "无限" : MoneyMath.FormatQuota(GrantedQuota);

    public string RemainingQuotaText => IsUnlimited ? "无限" : MoneyMath.FormatQuota(RemainingQuota);

    public string ExpiresText => ExpiresAt is null
        ? "永不过期"
        : ExpiresAt.Value.ToLocalTime().ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

    public string ProgressText
    {
        get
        {
            if (IsUnlimited)
            {
                return "无限额度，不参与总额度统计";
            }

            if (GrantedQuota <= 0)
            {
                return "未设置总额度";
            }

            return $"已用 {ProgressValue.ToString("F1", CultureInfo.InvariantCulture)}%（{UsedCnyText} / {GrantedCnyText}）";
        }
    }

    /// <summary>各模型消耗汇总（展开卡片时懒加载）。</summary>
    public ObservableCollection<ModelUsage> ModelUsages { get; } = new();

    [ObservableProperty]
    private bool _isExpanded;

    [ObservableProperty]
    private bool _modelsLoading;

    [ObservableProperty]
    private string _modelsError = string.Empty;

    [ObservableProperty]
    private string _modelsSummary = string.Empty;

    partial void OnIsExpandedChanged(bool value)
    {
        if (value && ModelUsages.Count == 0 && !ModelsLoading && ErrorMessage.Length == 0)
        {
            _ = LoadModelUsagesAsync();
        }
    }

    [RelayCommand]
    private Task LoadModels() => LoadModelUsagesAsync();

    /// <summary>拉取最近日志并按模型聚合消耗。</summary>
    public async Task LoadModelUsagesAsync()
    {
        if (ModelsLoading)
        {
            return;
        }

        var apiKey = KeyStore.Unprotect(_protectedApiKey);
        if (apiKey.Length == 0)
        {
            ModelsError = "密钥无法解密，无法查询模型消耗。";
            OnPropertyChanged(nameof(HasModelsError));
            return;
        }

        ModelsLoading = true;
        ModelsError = string.Empty;
        OnPropertyChanged(nameof(HasModelsError));
        try
        {
            var usages = await _client.GetModelUsageAsync(BaseUrl, apiKey).ConfigureAwait(true);

            ModelUsages.Clear();
            foreach (var usage in usages)
            {
                ModelUsages.Add(usage);
            }

            ModelsSummary = usages.Count == 0
                ? "最近没有调用记录"
                : $"最近 {Math.Min(TokenLogParser.MaxRecentItems, usages.Sum(u => u.Calls))} 次调用 · {usages.Count} 个模型 · 共 " +
                  MoneyMath.FormatCnyCompact(usages.Sum(u => MoneyMath.QuotaToCny(u.Quota)));
        }
        catch (QuotaQueryException ex)
        {
            ModelsError = ex.Message;
        }
        catch (Exception ex)
        {
            ModelsError = "查询失败：" + ex.Message;
        }
        finally
        {
            ModelsLoading = false;
            OnPropertyChanged(nameof(HasModelsError));
        }
    }

    public bool HasModelsError => ModelsError.Length > 0;

    /// <summary>额度刷新成功后，若模型明细已展开就同步刷新。</summary>
    private async Task RefreshModelsIfOpenAsync()
    {
        if (IsExpanded && ModelUsages.Count > 0)
        {
            await LoadModelUsagesAsync().ConfigureAwait(true);
        }
    }

    [RelayCommand]
    private Task Refresh() => RefreshCoreAsync();

    [RelayCommand]
    private void Delete() => _owner.RemoveKey(this);

    public async Task RefreshCoreAsync()
    {
        if (IsLoading)
        {
            return;
        }

        var apiKey = KeyStore.Unprotect(_protectedApiKey);
        if (apiKey.Length == 0)
        {
            ErrorMessage = "密钥无法解密（可能换了 Windows 账号或电脑），请删除后重新添加。";
            CredentialState = "密钥不可用";
            OnPropertyChanged(nameof(HasError));
            return;
        }

        IsLoading = true;
        try
        {
            var quota = await _client.QueryAsync(BaseUrl, apiKey).ConfigureAwait(true);
            Apply(quota);
            await RefreshModelsIfOpenAsync().ConfigureAwait(true);
        }
        catch (QuotaQueryException ex)
        {
            ErrorMessage = ex.Message;
        }
        catch (Exception ex)
        {
            ErrorMessage = "查询失败：" + ex.Message;
        }
        finally
        {
            IsLoading = false;
            OnPropertyChanged(nameof(HasError));
            _owner.UpdateDerived();
        }
    }

    public void RecomputeMoney()
    {
        OnPropertyChanged(nameof(UsedCny));
        OnPropertyChanged(nameof(GrantedCny));
        OnPropertyChanged(nameof(RemainingCny));
        OnPropertyChanged(nameof(ProgressValue));
        OnPropertyChanged(nameof(UsedCnyText));
        OnPropertyChanged(nameof(GrantedCnyText));
        OnPropertyChanged(nameof(RemainingCnyText));
        OnPropertyChanged(nameof(UsedQuotaText));
        OnPropertyChanged(nameof(GrantedQuotaText));
        OnPropertyChanged(nameof(RemainingQuotaText));
        OnPropertyChanged(nameof(ExpiresText));
        OnPropertyChanged(nameof(ProgressText));
        OnPropertyChanged(nameof(HasError));
    }

    public StoredKey ToStoredKey() => new()
    {
        Id = Id,
        Name = Name,
        BaseUrl = BaseUrl,
        Enabled = IsEnabled,
        ProtectedApiKey = _protectedApiKey,
    };

    private void Apply(TokenQuota quota)
    {
        IsUnlimited = quota.UnlimitedQuota;
        TokenName = quota.TokenName;
        GrantedQuota = quota.TotalGranted;
        UsedQuota = quota.TotalUsed;
        RemainingQuota = quota.TotalAvailable;
        UserRemainingQuota = quota.UserQuotaAvailable;
        ExpiresAt = quota.ExpiresAt;
        ErrorMessage = string.Empty;
        CredentialState = string.IsNullOrWhiteSpace(quota.TokenName) ? "已查询" : quota.TokenName;
    }

    private static string MaskApiKey(string apiKey)
    {
        if (apiKey.Length == 0)
        {
            return string.Empty;
        }

        return apiKey.Length <= 10
            ? apiKey[..3] + new string('*', 4)
            : apiKey[..6] + new string('*', 6) + apiKey[^4..];
    }
}
