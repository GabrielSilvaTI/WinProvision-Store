using System.ComponentModel;
using System.Windows.Automation;
using System.Windows;
using System.Windows.Threading;
using WinProvision.Core.Services;
using WinProvision.Store.Controls;
using Wpf.Ui.Animations;
using Wpf.Ui;
using Wpf.Ui.Abstractions;
using Wpf.Ui.Controls;
using Wpf.Ui.TaskBar;

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
                TaskBarProgress.SetState(this, TaskBarProgressState.None);
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
        UpdateNavigationMotionPreference();
        SystemParameters.StaticPropertyChanged += SystemParameters_StaticPropertyChanged;

        // Abre a vitrine inicial assim que a árvore visual estiver pronta.
        Loaded += MainWindow_Loaded;
    }

    private void MainWindow_Loaded(object sender, RoutedEventArgs e) =>
        _navigationService.Navigate(typeof(HomePage));

    private void SystemParameters_StaticPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (!string.Equals(e.PropertyName, nameof(SystemParameters.ClientAreaAnimation), StringComparison.Ordinal))
            return;

        Dispatcher.BeginInvoke(UpdateNavigationMotionPreference, DispatcherPriority.DataBind);
    }

    private void UpdateNavigationMotionPreference()
    {
        bool allowMotion = SystemParameters.ClientAreaAnimation;
        RootNavigation.Transition = allowMotion ? Transition.FadeInWithSlide : Transition.None;
        RootNavigation.TransitionDuration = allowMotion ? 160 : 0;
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
        if (e.PropertyName is not (nameof(OperationsQueueService.TotalCount)
            or nameof(OperationsQueueService.CompletedCount)
            or nameof(OperationsQueueService.OverallProgress)
            or nameof(OperationsQueueService.IsProgressIndeterminate)
            or nameof(OperationsQueueService.HasFailedOperations)))
            return;

        Dispatcher.BeginInvoke(() =>
        {
            UpdateQueueBadge();
            UpdateTaskbarProgress();
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

    private void UpdateTaskbarProgress()
    {
        if (!_queueService.HasOperations)
        {
            TaskBarProgress.SetState(this, TaskBarProgressState.None);
            return;
        }

        if (_queueService.CompletedCount == _queueService.TotalCount)
        {
            TaskBarProgressState completedState = _queueService.HasFailedOperations
                ? TaskBarProgressState.Error
                : _queueService.Operations.Any(operation => operation.State == WinProvision.Core.Models.OperationState.Canceled)
                    ? TaskBarProgressState.Paused
                    : TaskBarProgressState.Normal;
            TaskBarProgress.SetValue(this, completedState, 100, 100);
            return;
        }

        if (_queueService.IsProgressIndeterminate)
        {
            TaskBarProgress.SetState(this, TaskBarProgressState.Indeterminate);
            return;
        }

        int progress = (int)Math.Clamp(Math.Round(_queueService.OverallProgress), 0, 100);
        TaskBarProgress.SetValue(this, TaskBarProgressState.Normal, progress, 100);
    }

    private void ShowQueuePanel() => QueuePanel.Visibility = Visibility.Visible;

    protected override void OnClosed(EventArgs e)
    {
        _queueService.PropertyChanged -= QueueService_PropertyChanged;
        SystemParameters.StaticPropertyChanged -= SystemParameters_StaticPropertyChanged;
        _queueAutoCloseTimer.Stop();
        TaskBarProgress.SetState(this, TaskBarProgressState.None);
        base.OnClosed(e);
    }

}
