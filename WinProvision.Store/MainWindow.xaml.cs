using System.ComponentModel;
using System.Windows;
using Wpf.Ui;
using Wpf.Ui.Abstractions;
using Wpf.Ui.Controls;
using WinProvision.Core.Services;
using WinProvision.Store.Controls;

namespace WinProvision.Store;

public partial class MainWindow : FluentWindow
{
    private readonly INavigationService _navigationService;
    private readonly OperationsQueueService _queueService;

    public MainWindow(
        INavigationViewPageProvider pageProvider,
        INavigationService navigationService,
        OperationsQueueService queueService,
        AppDetailsOverlay detailsOverlay)
    {
        InitializeComponent();

        _navigationService = navigationService;
        _queueService = queueService;

        QueuePanelControl.Queue = _queueService;
        _queueService.PropertyChanged += QueueService_PropertyChanged;
        UpdateQueueBadge();

        // Overlay de Detalhes do pacote (ver AppDetailsOverlay/AppDetailsOverlayService)
        // - resolvido via DI porque depende de vários serviços (PackageCollectionService,
        // WingetExecutor etc.), então é mais simples deixar o host de conteúdo no XAML
        // vazio e atribuir aqui do que reconstruir a árvore de injeção dentro do XAML.
        DetailsOverlayHost.Content = detailsOverlay;

        // Associa o provedor de páginas v4 e o controle de navegação[cite: 1]
        RootNavigation.SetPageProviderService(pageProvider);
        _navigationService.SetNavigationControl(RootNavigation);

        // Navega para a HomePage (vitrine de destaques) assim que o layout for renderizado
        Loaded += (s, e) =>
        {
            _navigationService.Navigate(typeof(HomePage));
        };
    }

    private void QueueToggleButton_Click(object sender, RoutedEventArgs e)
    {
        QueuePopup.IsOpen = !QueuePopup.IsOpen;
    }

    private void QueueService_PropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(OperationsQueueService.TotalCount) or nameof(OperationsQueueService.CompletedCount))
        {
            Dispatcher.Invoke(UpdateQueueBadge);
        }
    }

    private void UpdateQueueBadge()
    {
        int pending = _queueService.TotalCount - _queueService.CompletedCount;
        QueueBadge.Visibility = pending > 0 ? Visibility.Visible : Visibility.Collapsed;
        QueueBadgeText.Text = pending.ToString();
    }
}