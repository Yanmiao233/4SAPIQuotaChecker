using System.IO;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using QuotaChecker.Services;

namespace QuotaChecker.Views;

/// <summary>
/// 检查更新 / 下载 / 安装 的一条龙对话框（状态机）。
/// PrimaryButton 全程通过 deferral 接管，由状态机决定何时真正关闭。
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

    private readonly UpdateService _service;
    private UpdateInfo? _update;
    private string? _stagingRoot;
    private Stage _stage = Stage.Checking;

    public UpdateDialog(XamlRoot root)
        : this(root, new UpdateService())
    {
    }

    public UpdateDialog(XamlRoot root, UpdateService service)
    {
        InitializeComponent();
        XamlRoot = root;
        _service = service;

        PrimaryButtonClick += OnPrimaryClick;
        Closed += (_, _) => _cts.Cancel();
        _ = RunCheckAsync();    }

    private readonly System.Threading.CancellationTokenSource _cts = new();

    private bool IsPortableLayout => UpdateService.DetectInstallRoot() is not null;

    private async Task RunCheckAsync()
    {
        SetStage(Stage.Checking);
        try
        {
            _update = await _service.CheckLatestAsync(_cts.Token).ConfigureAwait(true);

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
        StatusText.Text = $"当前版本 v{UpdateService.CurrentVersion} → 新版本 v{_update.Tag}{sizeText}";
        NotesText.Text = string.IsNullOrWhiteSpace(_update.Notes) ? "（无更新说明）" : _update.Notes;
        NotesViewer.Visibility = Visibility.Visible;
        SetStage(Stage.Available);
    }

    private async void OnPrimaryClick(ContentDialog sender, ContentDialogButtonClickEventArgs args)
    {
        // 全程接管按钮，由状态机决定关闭时机
        args.Cancel = true;
        if (_stage == Stage.Downloading || _cts.IsCancellationRequested)
        {
            return;
        }

        if (_stage == Stage.ReadyToInstall)
        {
            await InstallAsync().ConfigureAwait(true);
            return;
        }

        if (_stage == Stage.Failed && _update is null)
        {
            // 检查更新失败过 -> 重试检查
            await RunCheckAsync().ConfigureAwait(true);
            return;
        }

        await DownloadAsync().ConfigureAwait(true);
    }

    private async Task DownloadAsync()
    {
        if (_update is null)
        {
            return;
        }

        SetStage(Stage.Downloading);
        var progress = new Progress<double>(p =>
        {
            DownloadBar.IsIndeterminate = false;
            DownloadBar.Value = p;
            ProgressText.Text = $"{p:F1}%（已下载 {_update.AssetBytes * p / 100d / 1024d / 1024d:F1} / {_update.AssetBytes / 1024d / 1024d:F1} MB）";
        });

        try
        {
            _stagingRoot = await _service.DownloadAsync(_update, progress, _cts.Token).ConfigureAwait(true);
            DownloadBar.Value = 100;
            StatusText.Text = "下载完成。点击「重启并更新」，程序会退出、替换文件后自动重启（密钥配置不受影响）。";
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

            // 给更新器一点启动时间，再退出主程序；更新器会等进程退出后替换并重启
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
        ErrorBar.IsOpen = ErrorBar.IsOpen && stage == Stage.Failed;

        switch (stage)
        {
            case Stage.Checking:
                Title = "软件更新";
                StatusText.Text = "正在检查更新…";
                StatusText.Visibility = Visibility.Visible;
                ProgressPanel.Visibility = Visibility.Collapsed;
                NotesViewer.Visibility = Visibility.Collapsed;
                DownloadBar.IsIndeterminate = true;
                IsPrimaryButtonEnabled = false;
                CloseButtonText = "取消";
                break;

            case Stage.UpToDate:
                Title = "软件更新";
                StatusText.Text = $"已是最新版本（v{UpdateService.CurrentVersion}）。";
                ProgressPanel.Visibility = Visibility.Collapsed;
                IsPrimaryButtonEnabled = false;
                CloseButtonText = "关闭";
                break;

            case Stage.Available:
                ProgressPanel.Visibility = Visibility.Collapsed;
                IsPrimaryButtonEnabled = true;
                PrimaryButtonText = "下载并安装";
                CloseButtonText = "取消";
                break;

            case Stage.Downloading:
                StatusText.Text = "正在下载更新…";
                ProgressPanel.Visibility = Visibility.Visible;
                DownloadBar.IsIndeterminate = true;
                ProgressText.Text = string.Empty;
                IsPrimaryButtonEnabled = false;
                CloseButtonText = "取消";
                break;

            case Stage.ReadyToInstall:
                ProgressPanel.Visibility = Visibility.Collapsed;
                IsPrimaryButtonEnabled = true;
                PrimaryButtonText = "重启并更新";
                CloseButtonText = "稍后";
                break;

            case Stage.Failed:
                StatusText.Text = "操作未完成。";
                ProgressPanel.Visibility = Visibility.Collapsed;
                IsPrimaryButtonEnabled = true;
                PrimaryButtonText = "重试";
                CloseButtonText = "关闭";
                break;
        }
    }
}
