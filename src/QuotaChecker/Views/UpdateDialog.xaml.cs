using System.Diagnostics;
using System.IO;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using QuotaChecker.Services;
using QuotaChecker.ViewModels;

namespace QuotaChecker.Views;

/// <summary>
/// 检查 / 下载 / 安装 更新的一条龙对话框（状态机）。
/// PrimaryButton 全程通过 deferral 接管，由状态机决定何时真正关闭。
/// 版本信息优先使用本地缓存，避免每次打开都联网。
/// </summary>
public sealed partial class UpdateDialog : ContentDialog
{
    private enum Stage
    {
        Checking,
        UpToDate,
        Available,
        Downloading,
        ReadyToInstall,
        Failed,
    }

    private readonly MainViewModel _vm;
    private readonly UpdateService _service = new();
    private readonly CancellationTokenSource _cts = new();

    private UpdateInfo? _update;
    private string? _stagingRoot;
    private Stage _stage = Stage.Checking;

    public UpdateDialog(XamlRoot root, MainViewModel vm)
    {
        InitializeComponent();
        XamlRoot = root;
        _vm = vm;

        PrimaryButtonClick += OnPrimaryClick;
        Closed += (_, _) => _cts.Cancel();

        _ = LoadAsync();
    }

    private bool IsPortableLayout => UpdateService.DetectInstallRoot() is not null;

    private string CurrentVersionText => $"当前版本 v{UpdateService.CurrentVersion}";

    private async Task LoadAsync()
    {
        SetStage(Stage.Checking);

        try
        {
            // 先用缓存（若未过期），避免每次都联网；用户可点「重新检查」强制刷新
            _update = await _vm.CheckForUpdatesAsync(force: false, _cts.Token).ConfigureAwait(true);

            if (_update is null)
            {
                SetStage(Stage.UpToDate);
            }
            else if (!IsPortableLayout)
            {
                ErrorBar.Title = "当前不是便携版布局";
                ErrorBar.Message = $"已发现新版本 v{_update.Tag}，但自动更新只支持便携版（app\\ 上级目录结构）。请到 GitHub Releases 手动下载。";
                ErrorBar.IsOpen = true;
                SetStage(Stage.Failed);
            }
            else
            {
                ShowUpdate();
            }
        }
        catch (OperationCanceledException)
        {
            // 对话框被关闭
        }
        catch (Exception ex)
        {
            ErrorBar.Message = "检查更新失败：" + ex.Message;
            ErrorBar.IsOpen = true;
            SetStage(Stage.Failed);
        }
    }

    private void ShowUpdate()
    {
        if (_update is null)
        {
            return;
        }

        Title = $"发现新版本 v{_update.Tag}";
        var sizeText = _update.AssetBytes > 0
            ? $" · 约 {_update.AssetBytes / 1024d / 1024d:F1} MB"
            : string.Empty;
        StatusText.Text = $"{CurrentVersionText} → 新版本 v{_update.Tag}{sizeText}";
        NotesText.Text = string.IsNullOrWhiteSpace(_update.Notes) ? "（无更新说明）" : _update.Notes;
        NotesViewer.Visibility = Visibility.Visible;
        PageLink.Visibility = _update.PageUrl.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
        SkipLink.Visibility = Visibility.Visible;
        SetStage(Stage.Available);
    }

    private async void OnPrimaryClick(ContentDialog sender, ContentDialogButtonClickEventArgs args)
    {
        args.Cancel = true; // 由状态机决定关闭时机
        if (_stage is Stage.Downloading || _stage is Stage.Checking || _cts.IsCancellationRequested)
        {
            return;
        }

        switch (_stage)
        {
            case Stage.ReadyToInstall:
                await InstallAsync().ConfigureAwait(true);
                return;

            case Stage.Failed when _update is null:
                await LoadAsync().ConfigureAwait(true);
                return;

            case Stage.Available:
            case Stage.Failed:
                await DownloadAsync().ConfigureAwait(true);
                return;
        }
    }

    private async void OnRecheckClick(object sender, RoutedEventArgs e)
    {
        if (_stage is Stage.Downloading)
        {
            return;
        }

        ErrorBar.IsOpen = false;
        _update = null;
        _stagingRoot = null;
        NotesViewer.Visibility = Visibility.Collapsed;
        PageLink.Visibility = Visibility.Collapsed;
        SkipLink.Visibility = Visibility.Collapsed;

        SetStage(Stage.Checking);
        try
        {
            _update = await _vm.CheckForUpdatesAsync(force: true, _cts.Token).ConfigureAwait(true);
            if (_update is null)
            {
                SetStage(Stage.UpToDate);
            }
            else if (!IsPortableLayout)
            {
                ErrorBar.Message = $"已发现新版本 v{_update.Tag}，但当前不是便携版布局，请手动下载。";
                ErrorBar.IsOpen = true;
                SetStage(Stage.Failed);
            }
            else
            {
                ShowUpdate();
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            ErrorBar.Message = "检查更新失败：" + ex.Message;
            ErrorBar.IsOpen = true;
            SetStage(Stage.Failed);
        }
    }

    private void OnSkipClick(object sender, RoutedEventArgs e)
    {
        if (_update is null)
        {
            return;
        }

        _vm.SkipVersion(_update.Tag);
        Hide();
    }

    private void OnOpenPageClick(object sender, RoutedEventArgs e)
    {
        var url = _update?.PageUrl;
        if (string.IsNullOrEmpty(url))
        {
            return;
        }

        try
        {
            Process.Start(new ProcessStartInfo { FileName = url, UseShellExecute = true });
        }
        catch
        {
            // 打不开浏览器就算了，页面地址已经显示在对话框里
        }
    }

    private async Task DownloadAsync()
    {
        if (_update is null)
        {
            return;
        }

        SetStage(Stage.Downloading);
        var info = _update;
        var progress = new Progress<double>(p =>
        {
            DownloadBar.IsIndeterminate = false;
            DownloadBar.Value = p;
            ProgressText.Text = info.AssetBytes > 0
                ? $"{p:F1}%（{info.AssetBytes * p / 100d / 1024d / 1024d:F1} / {info.AssetBytes / 1024d / 1024d:F1} MB）"
                : $"{p:F1}%";
        });

        try
        {
            _stagingRoot = await _service.DownloadAsync(info, progress, _cts.Token).ConfigureAwait(true);
            DownloadBar.Value = 100;
            StatusText.Text = "下载完成。点击「重启并更新」，程序会退出、替换文件后自动重启（密钥与设置不受影响）。";
            SetStage(Stage.ReadyToInstall);
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            ErrorBar.Message = "下载失败：" + ex.Message;
            ErrorBar.IsOpen = true;
            SetStage(Stage.Failed);
        }
    }

    private async Task InstallAsync()
    {
        if (_stagingRoot is null)
        {
            return;
        }

        try
        {
            var installRoot = UpdateService.DetectInstallRoot()
                ?? throw new InvalidOperationException("未找到便携版安装目录。");

            UpdateService.LaunchUpdater(_stagingRoot, installRoot);
            Hide();

            // 给更新器一点启动时间，再退出主程序
            await Task.Delay(800).ConfigureAwait(true);
            App.ShutdownForUpdate();
        }
        catch (Exception ex)
        {
            ErrorBar.Message = "启动更新器失败：" + ex.Message;
            ErrorBar.IsOpen = true;
            SetStage(Stage.Failed);
        }
    }

    private void SetStage(Stage stage)
    {
        _stage = stage;

        switch (stage)
        {
            case Stage.Checking:
                Title = "软件更新";
                StatusText.Text = "正在获取版本信息…";
                ProgressPanel.Visibility = Visibility.Collapsed;
                NotesViewer.Visibility = Visibility.Collapsed;
                DownloadBar.IsIndeterminate = true;
                IsPrimaryButtonEnabled = false;
                PrimaryButtonText = "下载并安装";
                CloseButtonText = "取消";
                RecheckLink.IsEnabled = false;
                break;

            case Stage.UpToDate:
                Title = "软件更新";
                StatusText.Text = $"{CurrentVersionText}，已是最新版本。";
                ProgressPanel.Visibility = Visibility.Collapsed;
                IsPrimaryButtonEnabled = false;
                CloseButtonText = "关闭";
                RecheckLink.IsEnabled = true;
                break;

            case Stage.Available:
                ProgressPanel.Visibility = Visibility.Collapsed;
                IsPrimaryButtonEnabled = true;
                PrimaryButtonText = "下载并安装";
                CloseButtonText = "取消";
                RecheckLink.IsEnabled = true;
                break;

            case Stage.Downloading:
                StatusText.Text = "正在下载更新…";
                ProgressPanel.Visibility = Visibility.Visible;
                DownloadBar.IsIndeterminate = true;
                ProgressText.Text = string.Empty;
                IsPrimaryButtonEnabled = false;
                CloseButtonText = "取消";
                RecheckLink.IsEnabled = false;
                SkipLink.Visibility = Visibility.Collapsed;
                break;

            case Stage.ReadyToInstall:
                ProgressPanel.Visibility = Visibility.Collapsed;
                IsPrimaryButtonEnabled = true;
                PrimaryButtonText = "重启并更新";
                CloseButtonText = "稍后";
                RecheckLink.IsEnabled = false;
                SkipLink.Visibility = Visibility.Collapsed;
                break;

            case Stage.Failed:
                StatusText.Text = "操作未完成。";
                ProgressPanel.Visibility = Visibility.Collapsed;
                IsPrimaryButtonEnabled = true;
                PrimaryButtonText = "重试";
                CloseButtonText = "关闭";
                RecheckLink.IsEnabled = true;
                break;
        }
    }
}
