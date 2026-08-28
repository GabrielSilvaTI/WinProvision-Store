using System.ComponentModel;
using System.Windows;
using Wpf.Ui;
using Wpf.Ui.Abstractions;
using Wpf.Ui.Appearance;
using Wpf.Ui.Controls;
using WinProvision.Core.Services;

namespace WinProvision.Store;

public partial class MainWindow : FluentWindow
{
    private readonly INavigationService _navigationService;
    private readonly OperationsQueueService _queueService;

    public MainWindow(
        INavigationViewPageProvider pageProvider,
        INavigationService navigationService,
        OperationsQueueService queueService)
    {
        InitializeComponent();

        _navigationService = navigationService;
        _queueService = queueService;

        QueuePanelControl.Queue = _queueService;
        _queueService.PropertyChanged += QueueService_PropertyChanged;
        UpdateQueueBadge();

        // Mantém o ícone do botão sol/lua coerente mesmo quando o tema muda "sozinho"
        // (troca do tema do Windows detectada pelo SystemThemeWatcher, configurado no
        // App.xaml.cs), e não só quando o próprio usuário clica no botão.
        UpdateThemeToggleIcon();
        ApplicationThemeManager.Changed += ApplicationThemeManager_Changed;
        Closed += (_, _) => ApplicationThemeManager.Changed -= ApplicationThemeManager_Changed;

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

    // -------------------------------------------------------------
    // TEMA CLARO/ESCURO
    //
    // Automático por padrão (SystemThemeWatcher, ligado no App.xaml.cs, acompanha o
    // tema do Windows o tempo todo). Este botão só permite ao usuário sobrepor essa
    // escolha manualmente sem sair do app; a próxima mudança de tema do Windows volta
    // a valer normalmente, já que o watcher continua ativo em segundo plano.
    // -------------------------------------------------------------

    private void ThemeToggleButton_Click(object sender, RoutedEventArgs e)
    {
        var next = ApplicationThemeManager.GetAppTheme() == ApplicationTheme.Dark
            ? ApplicationTheme.Light
            : ApplicationTheme.Dark;

        ApplicationThemeManager.Apply(next, WindowBackdropType.Mica);
    }

    private void ApplicationThemeManager_Changed(ApplicationTheme currentApplicationTheme, System.Windows.Media.Color systemAccent) =>
        Dispatcher.Invoke(UpdateThemeToggleIcon);

    private void UpdateThemeToggleIcon()
    {
        // Mostra o ícone da ação que o clique vai realizar (padrão comum desse tipo de
        // botão): sol quando está escuro (clique clareia), lua quando está claro.
        ThemeToggleIcon.Symbol = ApplicationThemeManager.GetAppTheme() == ApplicationTheme.Dark
            ? SymbolRegular.WeatherSunny24
            : SymbolRegular.WeatherMoon24;
    }
}