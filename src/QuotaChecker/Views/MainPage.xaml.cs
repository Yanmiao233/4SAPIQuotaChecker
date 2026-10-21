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
    }

    public MainViewModel Vm { get; }

    /// <summary>供窗口调用 <c>SetTitleBar</c>。</summary>
    public UIElement TitleBarElement => TitleBarArea;

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
