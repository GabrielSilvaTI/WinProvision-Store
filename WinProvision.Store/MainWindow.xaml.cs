using System.ComponentModel;
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
        OperationsQueueService queueService,
        AppDetailsOverlay detailsOverlay)
    {
        InitializeComponent();

        _navigationService = navigationService;
        _queueService = queueService;
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

        // Associa o provedor de páginas v4 e o controle de navegação[cite: 1]
        RootNavigation.SetPageProviderService(pageProvider);
        _navigationService.SetNavigationControl(RootNavigation);

        // Navega para a HomePage (vitrine de destaques) assim que o layout for renderizado
        Loaded += (s, e) =>
        {
            _navigationService.Navigate(typeof(HomePage));
        };

        // Quando a janela é ajustada para meia tela (< 1020px), recolhe o menu para modo compacto (48px),
        // liberando mais de 160px para o conteúdo principal não cortar.
        SizeChanged += (s, e) =>
        {
            if (e.WidthChanged)
            {
                RootNavigation.IsPaneOpen = e.NewSize.Width >= 1020;
            }
        };
    }

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
    }

    private void ShowQueuePanel() => QueuePanel.Visibility = Visibility.Visible;

}
