using Microsoft.UI.Xaml.Controls;
using QuotaChecker.Models;
using QuotaChecker.Services;

namespace QuotaChecker.Views;

/// <summary>添加密钥对话框（名称 / 接口地址 / API Key）。</summary>
public sealed partial class AddKeyDialog : ContentDialog
{
    public AddKeyDialog()
    {
        InitializeComponent();
    }

    public string KeyName { get; private set; } = string.Empty;

    public string BaseUrl { get; private set; } = AppSettings.DefaultBaseUrl;

    public string ApiKey { get; private set; } = string.Empty;

    private void OnPrimaryButtonClick(ContentDialog sender, ContentDialogButtonClickEventArgs args)
    {
        var apiKey = ApiKeyBox.Password.Trim();
        if (apiKey.Length == 0)
        {
            ShowError("请填写 API Key", "API Key 不能为空，通常以 sk- 开头。");
            args.Cancel = true;
            return;
        }

        var baseUrl = BaseUrlBox.Text.Trim();
        if (baseUrl.Length == 0)
        {
            baseUrl = AppSettings.DefaultBaseUrl;
        }

        try
        {
            QuotaClient.BuildUri(baseUrl);
        }
        catch (QuotaQueryException ex)
        {
            ShowError("接口地址无效", ex.Message);
            args.Cancel = true;
            return;
        }

        KeyName = NameBox.Text.Trim();
        BaseUrl = baseUrl;
        ApiKey = apiKey;
    }

    private void ShowError(string title, string message)
    {
        ErrorBar.Title = title;
        ErrorBar.Message = message;
        ErrorBar.IsOpen = true;
    }
}
