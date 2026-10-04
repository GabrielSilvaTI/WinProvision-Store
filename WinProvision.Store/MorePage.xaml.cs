using System.Windows;
using System.Windows.Controls;
using Microsoft.Extensions.DependencyInjection;
using Wpf.Ui;

namespace WinProvision.Store;

public partial class MorePage : Page
{
    private readonly INavigationService _navigationService;

    public MorePage()
    {
        InitializeComponent();
        _navigationService = App.Services.GetRequiredService<INavigationService>();
    }

    private void AboutCard_Click(object sender, RoutedEventArgs e) =>
        _navigationService.Navigate(typeof(AboutPage));

    private void StatusCard_Click(object sender, RoutedEventArgs e) =>
        _navigationService.Navigate(typeof(StoreStatusPage));

    private void HistoryCard_Click(object sender, RoutedEventArgs e) =>
        _navigationService.Navigate(typeof(HistoryPage));

    private void LogCard_Click(object sender, RoutedEventArgs e) =>
        _navigationService.Navigate(typeof(LogViewerPage));
}
