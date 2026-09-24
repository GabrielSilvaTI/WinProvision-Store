using System.Windows;
using System.Windows.Controls;
using Microsoft.Extensions.DependencyInjection;
using Wpf.Ui.Controls;

namespace WinProvision.Store;

public partial class InstalledPackagesPage : Page
{
    private readonly InstalledPackagesViewModel _viewModel;

    public InstalledPackagesPage()
    {
        InitializeComponent();

        _viewModel = App.Services.GetRequiredService<InstalledPackagesViewModel>();
        DataContext = _viewModel;
        Loaded += async (_, _) => await _viewModel.LoadAsync();
    }

    private void SearchBox_TextChanged(object sender, System.Windows.Controls.TextChangedEventArgs e)
    {
        if (sender is Wpf.Ui.Controls.TextBox box)
            _viewModel.SearchText = box.Text ?? string.Empty;
    }

    private async void Remove_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new Wpf.Ui.Controls.MessageBox
        {
            Title = "Desinstalar aplicativos",
            Content = "Desinstalar os aplicativos selecionados?",
            PrimaryButtonText = "Desinstalar",
            CloseButtonText = "Cancelar"
        };

        if (await dialog.ShowDialogAsync() == Wpf.Ui.Controls.MessageBoxResult.Primary)
            await _viewModel.RemoveSelectedAsync();
    }

    private async void Refresh_Click(object sender, RoutedEventArgs e) =>
        await _viewModel.LoadAsync();

    private void GridViewToggleButton_Click(object sender, RoutedEventArgs e)
    {
        GridViewScrollViewer.Visibility = Visibility.Visible;
        ListViewScrollViewer.Visibility = Visibility.Collapsed;
        GridViewToggleButton.IsChecked = true;
        ListViewToggleButton.IsChecked = false;
    }

    private void ListViewToggleButton_Click(object sender, RoutedEventArgs e)
    {
        GridViewScrollViewer.Visibility = Visibility.Collapsed;
        ListViewScrollViewer.Visibility = Visibility.Visible;
        GridViewToggleButton.IsChecked = false;
        ListViewToggleButton.IsChecked = true;
    }

    private void ToggleSelectAll_Click(object sender, RoutedEventArgs e) =>
        _viewModel.ToggleSelectAll();

    private void SelectPackage_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: InstalledPackageRow row } && row.CanSelect)
            row.IsSelected = !row.IsSelected;
    }
}
