using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using QuotaChecker.ViewModels;

namespace QuotaChecker.Views;

/// <summary>主界面内容（汇总卡片 + 密钥列表 + 状态栏）。</summary>
public sealed partial class MainPage : UserControl
{
    public MainPage()
    {
        InitializeComponent();
        Vm = App.ViewModel;
        Loaded += OnLoaded;
    }

    public MainViewModel Vm { get; }

    private bool _suppressAutoRefreshEvent;

    /// <summary>供窗口调用 <c>SetTitleBar</c>。</summary>
    public UIElement TitleBarElement => TitleBarArea;

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        // 把持久化的秒数映射回下拉框选项（不触发应用，避免覆盖状态栏提示）
        _suppressAutoRefreshEvent = true;

        var seconds = Vm.AutoRefreshSeconds;
        if (!TrySelectPreset(seconds) && seconds > 0)
        {
            // 非预设值（例如手工改过 keys.json）：补一项显示真实值，避免"显示关闭其实在刷新"
            AutoRefreshBox.Items.Add(new ComboBoxItem
            {
                Content = "每 " + MainViewModel.FormatInterval(seconds),
                Tag = seconds.ToString(System.Globalization.CultureInfo.InvariantCulture),
            });
            AutoRefreshBox.SelectedIndex = AutoRefreshBox.Items.Count - 1;
        }

        _suppressAutoRefreshEvent = false;
    }

    private bool TrySelectPreset(int seconds)
    {
        for (var i = 0; i < AutoRefreshBox.Items.Count; i++)
        {
            if (AutoRefreshBox.Items[i] is ComboBoxItem item &&
                int.TryParse(item.Tag as string, out var value) &&
                value == seconds)
            {
                AutoRefreshBox.SelectedIndex = i;
                return true;
            }
        }

        return false;
    }

    private void OnAutoRefreshChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_suppressAutoRefreshEvent)
        {
            return;
        }

        if (AutoRefreshBox.SelectedItem is ComboBoxItem item &&
            int.TryParse(item.Tag as string, out var seconds))
        {
            Vm.ApplyAutoRefreshSeconds(seconds);
        }
    }

    private void OnRateChanged(NumberBox sender, NumberBoxValueChangedEventArgs args) => Vm.ApplyRate();

    private async void OnAddKeyClick(object sender, RoutedEventArgs e)
    {
        var dialog = new AddKeyDialog
        {
            XamlRoot = RootGrid.XamlRoot,
        };

        var result = await dialog.ShowAsync();
        if (result != ContentDialogResult.Primary)
        {
            return;
        }

        await Vm.AddKeyAsync(dialog.KeyName, dialog.BaseUrl, dialog.ApiKey);
    }
}
