using System.Windows;
using System.Windows.Controls;
using Microsoft.Extensions.DependencyInjection;

namespace WinProvision.Store;

public partial class InstalledPackagesPage : Page
{
    private readonly InstalledPackagesViewModel _viewModel;
    public InstalledPackagesPage()
    {
        InitializeComponent();
        _viewModel = new InstalledPackagesViewModel(
            App.Services.GetRequiredService<Services.InstalledPackagesService>(),
            App.Services.GetRequiredService<WinProvision.Core.Services.WingetExecutor>(),
            App.Services.GetRequiredService<WinProvision.Core.Services.OperationsQueueService>(),
            App.Services.GetRequiredService<WinProvision.Core.Services.Office.OfficeDeploymentToolService>(),
            App.Services.GetRequiredService<WinProvision.Core.Services.Office.OfficeInstalledProductsDetector>(),
            App.Services.GetRequiredService<Services.InstalledPackageClassifier>());
        DataContext = _viewModel;
        Loaded += async (_, _) => await _viewModel.LoadAsync();
    }

    private async void Remove_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new Wpf.Ui.Controls.MessageBox
        {
            Title = "Remover pacotes",
            Content = "Os itens selecionados serão removidos sequencialmente. Se houver Office selecionado, todas as versões do Office desta máquina serão removidas (não apenas uma versão Click-to-Run). Continuar?",
            PrimaryButtonText = "Remover",
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

    private void ToggleSelectAll_Click(object sender, RoutedEventArgs e)
    {
        var visible = _viewModel.VisiblePackages.Cast<InstalledPackageRow>()
            .Where(x => x.CanRemove)
            .ToArray();
        bool select = visible.Any(x => !x.IsSelected);
        foreach (var package in visible)
            package.IsSelected = select;
    }

    private void SelectPackage_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: InstalledPackageRow row } && row.CanSelect)
            row.IsSelected = !row.IsSelected;
    }

    private void SelectionCheckBox_Click(object sender, RoutedEventArgs e) =>
        e.Handled = true;
}
