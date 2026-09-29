using System.ComponentModel;
using System.Windows.Automation;
using System.Windows;
using System.Windows.Threading;
using WinProvision.Core.Services;
using WinProvision.Store.Controls;
using Wpf.Ui;
using Wpf.Ui.Abstractions;
using Wpf.Ui.Controls;

namespace WinProvision.Store;

public partial class MainWindow : FluentWindow
{
    private readonly INavigationService _navigationService;
    private readonly OperationsQueueService _queueService;
    private readonly DispatcherTimer _queueAutoCloseTimer = new() { Interval = TimeSpan.FromSeconds(4) };

    public MainWindow(
        INavigationViewPageProvider pageProvider,
        INavigationService navigationService,
        ISnackbarService snackbarService,
        IContentDialogService contentDialogService,
        OperationsQueueService queueService,
        AppDetailsOverlay detailsOverlay)
    {
        InitializeComponent();

        _navigationService = navigationService;
        _queueService = queueService;
        snackbarService.SetSnackbarPresenter(MainSnackbarPresenter);
        contentDialogService.SetDialogHost(RootContentDialog);
        QueuePanel.Queue = _queueService;
        _queueAutoCloseTimer.Tick += (_, _) =>
        {
            _queueAutoCloseTimer.Stop();
            if (_queueService.HasOperations
                && _queueService.TotalCount == _queueService.CompletedCount)
            {
                QueuePanel.Visibility = Visibility.Collapsed;
            }
        };

        _queueService.PropertyChanged += QueueService_PropertyChanged;
        UpdateQueueBadge();
        if (_queueService.HasOperations)
        {
            ShowQueuePanel();
            ScheduleQueueAutoCloseIfFinished();
        }

        // Overlay de Detalhes do pacote (ver AppDetailsOverlay/AppDetailsOverlayService)
        // - resolvido via DI porque depende de vários serviços (PackageCollectionService,
        // WingetExecutor etc.), então é mais simples deixar o host de conteúdo no XAML
        // vazio e atribuir aqui do que reconstruir a árvore de injeção dentro do XAML.
        DetailsOverlayHost.Content = detailsOverlay;

        // Registra o provedor de páginas e o NavigationView na navegação do WPF-UI.
        RootNavigation.SetPageProviderService(pageProvider);
        _navigationService.SetNavigationControl(RootNavigation);

        // Abre a vitrine inicial assim que a árvore visual estiver pronta.
        Loaded += MainWindow_Loaded;
    }

    private void MainWindow_Loaded(object sender, RoutedEventArgs e) =>
        _navigationService.Navigate(typeof(HomePage));

    private void QueueToggleButton_Click(object sender, RoutedEventArgs e)
    {
        if (QueuePanel.Visibility == Visibility.Visible)
            QueuePanel.Visibility = Visibility.Collapsed;
        else
            ShowQueuePanel();
    }

    private void QueueService_PropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is not (nameof(OperationsQueueService.TotalCount) or nameof(OperationsQueueService.CompletedCount)))
            return;

        Dispatcher.BeginInvoke(() =>
        {
            UpdateQueueBadge();
            if (!_queueService.HasOperations)
            {
                _queueAutoCloseTimer.Stop();
                QueuePanel.Visibility = Visibility.Collapsed;
            }
            else if (_queueService.TotalCount > _queueService.CompletedCount)
            {
                _queueAutoCloseTimer.Stop();
                if (e.PropertyName == nameof(OperationsQueueService.TotalCount))
                    ShowQueuePanel();
            }
            else
            {
                ScheduleQueueAutoCloseIfFinished();
            }
        });
    }

    private void ScheduleQueueAutoCloseIfFinished()
    {
        if (_queueService.HasOperations
            && _queueService.TotalCount == _queueService.CompletedCount)
        {
            _queueAutoCloseTimer.Stop();
            _queueAutoCloseTimer.Start();
        }
    }

    private void UpdateQueueBadge()
    {
        int pending = _queueService.TotalCount - _queueService.CompletedCount;
        QueueBadge.Visibility = pending > 0 ? Visibility.Visible : Visibility.Collapsed;
        QueueBadgeText.Text = pending.ToString();
        string queueDescription = pending == 0
            ? "Fila de operações"
            : $"Fila de operações, {pending} pendente(s)";
        QueueToggleButton.ToolTip = queueDescription;
        AutomationProperties.SetName(QueueToggleButton, queueDescription);
    }

    private void ShowQueuePanel() => QueuePanel.Visibility = Visibility.Visible;

}
