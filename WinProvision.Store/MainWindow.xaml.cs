using System.Windows;
using Wpf.Ui;
using Wpf.Ui.Abstractions;
using Wpf.Ui.Controls;

namespace WinProvision.Store;

public partial class MainWindow : FluentWindow
{
    private readonly INavigationService _navigationService;

    public MainWindow(
        INavigationViewPageProvider pageProvider,
        INavigationService navigationService)
    {
        InitializeComponent();

        _navigationService = navigationService;

        // Associa o provedor de páginas v4 e o controle de navegação[cite: 1]
        RootNavigation.SetPageProviderService(pageProvider);
        _navigationService.SetNavigationControl(RootNavigation);

        // Navega para a StorePage assim que o layout for renderizado
        Loaded += (s, e) =>
        {
            _navigationService.Navigate(typeof(StorePage));
        };
    }
}