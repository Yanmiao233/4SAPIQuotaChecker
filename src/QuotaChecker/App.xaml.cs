using Microsoft.UI.Xaml;
using QuotaChecker.Services;
using QuotaChecker.ViewModels;

namespace QuotaChecker;

/// <summary>应用入口。</summary>
public partial class App : Application
{
    private Window? _window;

    public App() => InitializeComponent();

    /// <summary>全局可用的主视图模型。</summary>
    public static MainViewModel ViewModel { get; private set; } = null!;

    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        ViewModel = new MainViewModel(new KeyStore(), new QuotaClient());
        ViewModel.Initialize();

        _window = new MainWindow();
        _window.Activate();

        ViewModel.StartAutoRefresh();
    }
}
