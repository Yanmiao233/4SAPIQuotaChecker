using Microsoft.UI.Composition.SystemBackdrops;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;
using Windows.Graphics;

namespace QuotaChecker;

/// <summary>主窗口：承载 <see cref="Views.MainPage"/>。</summary>
public sealed partial class MainWindow : Window
{
    private const int DefaultWidth = 1180;
    private const int DefaultHeight = 800;

    public MainWindow()
    {
        InitializeComponent();

        Title = "配额统计 QuotaChecker";
        ExtendsContentIntoTitleBar = true;
        SetTitleBar(Page.TitleBarElement);

        if (MicaController.IsSupported())
        {
            SystemBackdrop = new MicaBackdrop { Kind = MicaKind.BaseAlt };
        }

        ResizeAndCenter();
    }

    private void ResizeAndCenter()
    {
        var appWindow = AppWindow;
        var display = DisplayArea.GetFromWindowId(appWindow.Id, DisplayAreaFallback.Primary);
        var workArea = display.WorkArea;

        var width = Math.Min(DefaultWidth, workArea.Width);
        var height = Math.Min(DefaultHeight, workArea.Height);

        appWindow.Resize(new SizeInt32(width, height));
        appWindow.Move(new PointInt32(
            workArea.X + ((workArea.Width - width) / 2),
            workArea.Y + ((workArea.Height - height) / 2)));

        if (appWindow.Presenter is OverlappedPresenter presenter)
        {
            presenter.PreferredMinimumWidth = 900;
            presenter.PreferredMinimumHeight = 560;
        }
    }
}

