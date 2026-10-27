using System.Text;
using Microsoft.UI.Xaml;
using QuotaChecker.Services;
using QuotaChecker.ViewModels;

namespace QuotaChecker;

/// <summary>应用入口。</summary>
public partial class App : Application
{
    private static Window? _window;

    public App()
    {
        InitializeComponent();

        // 崩溃日志：未处理异常写入 %LOCALAPPDATA%\QuotaChecker\crash.log，
        // 便于向项目反馈问题时带上现场。
        UnhandledException += (_, e) => WriteCrashLog("XamlUnhandledException", e.Exception);

        AppDomain.CurrentDomain.UnhandledException += (_, e) =>
            WriteCrashLog("AppDomainUnhandledException", e.ExceptionObject as Exception);

        TaskScheduler.UnobservedTaskException += (_, e) =>
        {
            WriteCrashLog("UnobservedTaskException", e.Exception);
            e.SetObserved();
        };
    }

    /// <summary>全局可用的主视图模型。</summary>
    public static MainViewModel ViewModel { get; private set; } = null!;

    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        // 清理上次更新留下的暂存目录
        UpdateService.CleanupStaleStaging();

        ViewModel = new MainViewModel(new KeyStore(), new QuotaClient());
        ViewModel.Initialize();

        _window = new MainWindow();
        _window.Activate();

        ViewModel.StartBackgroundRefresh();
    }

    /// <summary>更新器已接管，退出主程序（更新器会等本进程退出后替换文件并重启）。</summary>
    public static void ShutdownForUpdate() => _window?.Close();

    private static void WriteCrashLog(string source, Exception? exception)
    {
        try
        {
            var dir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "QuotaChecker");
            Directory.CreateDirectory(dir);

            var sb = new StringBuilder();
            sb.AppendLine($"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] {source}");
            sb.AppendLine(exception?.ToString() ?? "(no exception object)");
            sb.AppendLine(new string('-', 60));

            File.AppendAllText(Path.Combine(dir, "crash.log"), sb.ToString(), Encoding.UTF8);
        }
        catch
        {
            // 记录失败时不做任何事，避免二次崩溃
        }
    }
}
