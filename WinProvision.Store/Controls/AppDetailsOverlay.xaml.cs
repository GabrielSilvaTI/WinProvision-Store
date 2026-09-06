using System;
using System.ComponentModel;
using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using Wpf.Ui.Controls;
using WinProvision.Core.Models;
using WinProvision.Core.Services;
using WinProvision.Store.Converters;
using WinProvision.Store.Services;

namespace WinProvision.Store.Controls;

/// <summary>
/// Painel de detalhes do pacote, exibido como overlay sobre o MainWindow (ver
/// MainWindow.xaml/.xaml.cs) em vez de uma janela separada. Singleton resolvido via DI
/// (ver App.xaml.cs) e assina AppDetailsOverlayService.Requested para saber quando
/// aparecer - qualquer página (hoje só a HomePage) pode pedir para mostrá-lo chamando
/// esse serviço, sem precisar conhecer este controle.
/// </summary>
public partial class AppDetailsOverlay : UserControl
{
    private readonly PackageCollectionService _collectionService;
    private readonly WingetExecutor _wingetExecutor;
    private readonly OperationsQueueService _queueService;
    private readonly InstalledAppsService _installedAppsService;
    private AppEntry? _app;

    public AppDetailsOverlay(AppDetailsOverlayService overlayService, PackageCollectionService collectionService,
        WingetExecutor wingetExecutor, OperationsQueueService queueService,
        InstalledAppsService installedAppsService)
    {
        InitializeComponent();

        _collectionService = collectionService;
        _wingetExecutor = wingetExecutor;
        _queueService = queueService;
        _installedAppsService = installedAppsService;

        Visibility = Visibility.Collapsed;
        overlayService.Requested += Show;
    }

    private void Show(AppEntry app)
    {
        if (_app is not null)
        {
            _app.PropertyChanged -= AppOnPropertyChanged;
        }

        _app = app;

        AsyncImage.SetSourceUrl(AppIcon, app.IconUrl);
        AppNameText.Text = app.Name;
        DescriptionText.Text = app.Description;
        DescriptionText.Visibility = string.IsNullOrWhiteSpace(app.Description) ? Visibility.Collapsed : Visibility.Visible;

        IdText.Text = app.Id;
        VersionText.Text = app.Version;
        ScoreText.Text = app.Score.ToString();

        SetupPublisher();
        SetupTags();
        SetupSize();
        SetupGitHubStars();
        SetupLicense();
        SetupLinkButtons();

        StatusText.Text = string.Empty;

        _app.PropertyChanged += AppOnPropertyChanged;
        UpdateInstallActionsVisibility();

        Visibility = Visibility.Visible;
        Focus();
    }

    private void Close()
    {
        if (_app is not null)
        {
            _app.PropertyChanged -= AppOnPropertyChanged;
            _app = null;
        }

        Visibility = Visibility.Collapsed;
    }

    private void AppDetailsOverlay_IsVisibleChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        // Esc fecha o painel — só funciona enquanto o controle está visível e com
        // foco (ver Show acima, que chama Focus()); sem isso, PreviewKeyDown nunca
        // chegaria aqui porque um UserControl escondido não recebe foco/entrada.
        if ((bool)e.NewValue)
        {
            PreviewKeyDown += AppDetailsOverlay_PreviewKeyDown;
        }
        else
        {
            PreviewKeyDown -= AppDetailsOverlay_PreviewKeyDown;
        }
    }

    private void AppDetailsOverlay_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
        {
            Close();
            e.Handled = true;
        }
    }

    private void AppOnPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(AppEntry.IsInstalled))
        {
            UpdateInstallActionsVisibility();
        }
    }

    /// <summary>Alterna entre "Instalar" (não instalado) e "Desinstalar" (instalado).</summary>
    private void UpdateInstallActionsVisibility()
    {
        if (_app is null) return;

        InstallButton.Visibility = _app.IsInstalled ? Visibility.Collapsed : Visibility.Visible;
        UninstallButton.Visibility = _app.IsInstalled ? Visibility.Visible : Visibility.Collapsed;
    }

    private void SetupPublisher()
    {
        if (_app is null) return;

        bool hasUrl = !string.IsNullOrWhiteSpace(_app.PublisherUrl);
        PublisherLink.Visibility = hasUrl ? Visibility.Visible : Visibility.Collapsed;
        PublisherPlainText.Visibility = hasUrl ? Visibility.Collapsed : Visibility.Visible;

        if (hasUrl)
        {
            PublisherLinkText.Text = _app.Publisher;
            PublisherLink.NavigateUri = _app.PublisherUrl!;
        }
        else
        {
            PublisherPlainText.Text = _app.Publisher;
        }
    }

    private void SetupTags()
    {
        if (_app is null) return;

        TagsList.ItemsSource = _app.Tags;
        TagsList.Visibility = _app.Tags.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    /// <summary>
    /// Mostra o tamanho estimado do instalador quando disponível (ver
    /// AppEntry.InstallerSizeBytes, calculado pelo Indexer na sincronização diária a
    /// partir da InstallerUrl do manifesto). Ausente = linha inteira escondida, em vez
    /// de mostrar "0 B" ou um valor enganoso.
    /// </summary>
    private void SetupSize()
    {
        if (_app is null) return;

        bool hasSize = _app.InstallerSizeBytes is > 0;
        SizeLabel.Visibility = hasSize ? Visibility.Visible : Visibility.Collapsed;
        SizeText.Visibility = hasSize ? Visibility.Visible : Visibility.Collapsed;

        if (hasSize)
        {
            double megabytes = _app.InstallerSizeBytes!.Value / 1024d / 1024d;
            SizeText.Text = megabytes >= 1024
                ? $"~ {megabytes / 1024:0.0} GB"
                : $"~ {megabytes:0} MB";
        }
    }

    private void SetupGitHubStars()
    {
        if (_app is null) return;

        bool hasStars = _app.HasGitHubMetrics;
        GitHubStarsLabel.Visibility = hasStars ? Visibility.Visible : Visibility.Collapsed;
        GitHubStarsRow.Visibility = hasStars ? Visibility.Visible : Visibility.Collapsed;

        if (hasStars)
        {
            GitHubStarsText.Text = _app.GitHubStars?.ToString() ?? "0";
        }
    }

    private void SetupLicense()
    {
        if (_app is null) return;

        bool hasLicense = !string.IsNullOrWhiteSpace(_app.License);
        bool hasLicenseUrl = hasLicense && !string.IsNullOrWhiteSpace(_app.LicenseUrl);

        LicenseLabel.Visibility = hasLicense ? Visibility.Visible : Visibility.Collapsed;
        LicenseLink.Visibility = hasLicenseUrl ? Visibility.Visible : Visibility.Collapsed;
        LicensePlainText.Visibility = hasLicense && !hasLicenseUrl ? Visibility.Visible : Visibility.Collapsed;

        if (hasLicenseUrl)
        {
            LicenseLinkText.Text = _app.License;
            LicenseLink.NavigateUri = _app.LicenseUrl!;
        }
        else if (hasLicense)
        {
            LicensePlainText.Text = _app.License;
        }
    }

    private void SetupLinkButtons()
    {
        if (_app is null) return;

        bool hasHomepage = !string.IsNullOrWhiteSpace(_app.Homepage);
        HomepageButton.Visibility = hasHomepage ? Visibility.Visible : Visibility.Collapsed;
        HomepageButton.Tag = _app.Homepage;

        bool hasReleaseNotes = !string.IsNullOrWhiteSpace(_app.ReleaseNotesUrl);
        ReleaseNotesButton.Visibility = hasReleaseNotes ? Visibility.Visible : Visibility.Collapsed;
        ReleaseNotesButton.Tag = _app.ReleaseNotesUrl;
    }

    private void Scrim_MouseLeftButtonUp(object sender, MouseButtonEventArgs e) => Close();

    // Impede que o clique dentro do cartão borbulhe até o Scrim (que fecharia o
    // painel) - cobre cliques em espaços vazios do cartão, já que cliques em botões
    // já chegam "Handled" por conta própria (ButtonBase marca isso ao processar).
    private void Card_MouseLeftButtonUp(object sender, MouseButtonEventArgs e) => e.Handled = true;

    private void CloseButton_Click(object sender, RoutedEventArgs e) => Close();

    private void AddToCatalogButton_Click(object sender, RoutedEventArgs e)
    {
        if (_app is null) return;

        int added = _collectionService.AddRangeToActive([_app]);

        var tabTitle = _collectionService.ActiveTab?.Title ?? "Perfil Padrão";
        StatusText.Text = added == 1
            ? $"Adicionado à guia '{tabTitle}'."
            : $"Já estava na guia '{tabTitle}'.";
    }

    private async void InstallButton_Click(object sender, RoutedEventArgs e)
    {
        if (_app is null) return;

        // Captura numa variável local: o painel pode ser fechado (Close() zera _app)
        // enquanto este await está pendente — ex.: o usuário fecha o painel enquanto
        // aguarda a resposta ao prompt de UAC de uma elevação sob demanda. Usar _app
        // direto depois do await arriscava NullReferenceException nesse cenário.
        var app = _app;

        InstallButton.IsEnabled = false;
        StatusText.Text = $"{app.Name} adicionado à fila de instalação. Acompanhe pelo ícone de fila na barra de título.";

        try
        {
            var result = await OperationRunner.RunInstallAsync(
                _queueService, _wingetExecutor, app.Id, app.Name, app.IconUrl, _installedAppsService);

            if (result.Success)
            {
                // Dispara AppOnPropertyChanged -> UpdateInstallActionsVisibility, que troca
                // "Instalar" por "Abrir"/"Desinstalar" automaticamente — só faz sentido se o
                // painel ainda estiver mostrando esse mesmo app.
                app.IsInstalled = true;
                if (_app == app)
                {
                    StatusText.Text = $"{app.Name} instalado.";
                }
            }
            else if (_app == app)
            {
                StatusText.Text = WingetErrorTranslator.ToMessage(result.FailureReason, "instalar", app.Name);
            }
        }
        catch (Exception ex)
        {
            if (_app == app)
            {
                StatusText.Text = $"Erro ao instalar {app.Name}: {ex.Message}";
            }
        }
        finally
        {
            InstallButton.IsEnabled = true;
        }
    }

    private async void UninstallButton_Click(object sender, RoutedEventArgs e)
    {
        if (_app is null) return;

        // Confirmação antes de qualquer ação destrutiva - mesmo padrão (Wpf.Ui MessageBox)
        // já usado no rodapé do MainWindow para confirmar o fechamento do app.
        //
        // O botão primário do MessageBox é um ui:Button interno do próprio template da
        // janela, sem Background/MouseOverBackground/PressedBackground expostos nas
        // propriedades públicas do MessageBox. A forma que já funciona no resto do app
        // (ver o Style com BasedOn="{StaticResource {x:Type ui:Button}}" nos cards da
        // HomePage) é sobrescrever o Style implícito de ui:Button só dentro da janela do
        // diálogo, herdando do Style padrão e adicionando um Trigger para
        // Appearance="Primary" que aplica o mesmo azul fixo (InstallActionBrush) usado
        // nos outros botões de ação - não mexe no Style global do app, só nesse diálogo.
        var confirmDialog = new Wpf.Ui.Controls.MessageBox
        {
            Title = "Desinstalar aplicativo",
            Content = $"Tem certeza que deseja desinstalar \"{_app.Name}\"?",
            PrimaryButtonText = "Desinstalar",
            CloseButtonText = "Cancelar"
        };

        var basePrimaryButtonStyle = (Style)Application.Current.Resources[typeof(Wpf.Ui.Controls.Button)];
        var primaryButtonStyle = new Style(typeof(Wpf.Ui.Controls.Button), basePrimaryButtonStyle);
        primaryButtonStyle.Triggers.Add(new Trigger
        {
            Property = Wpf.Ui.Controls.Button.AppearanceProperty,
            Value = Wpf.Ui.Controls.ControlAppearance.Primary,
            Setters =
            {
                new Setter(Wpf.Ui.Controls.Button.BackgroundProperty, Application.Current.Resources["InstallActionBrush"]),
                new Setter(Wpf.Ui.Controls.Button.MouseOverBackgroundProperty, Application.Current.Resources["InstallActionHoverBrush"]),
                new Setter(Wpf.Ui.Controls.Button.PressedBackgroundProperty, Application.Current.Resources["InstallActionPressedBrush"]),
                new Setter(Wpf.Ui.Controls.Button.ForegroundProperty, Brushes.White),
                new Setter(Wpf.Ui.Controls.Button.PressedForegroundProperty, Brushes.White)
            }
        });
        confirmDialog.Resources[typeof(Wpf.Ui.Controls.Button)] = primaryButtonStyle;

        var confirmResult = await confirmDialog.ShowDialogAsync();
        if (confirmResult != Wpf.Ui.Controls.MessageBoxResult.Primary)
        {
            return;
        }

        if (_app is null) return;

        // Mesmo motivo do InstallButton_Click: captura local pra sobreviver ao painel
        // sendo fechado (Close() zera _app) enquanto aguarda o await abaixo — cenário bem
        // mais provável agora que uma desinstalação pode ficar esperando o usuário
        // responder a um prompt de UAC (elevação sob demanda).
        var app = _app;

        UninstallButton.IsEnabled = false;
        StatusText.Text = $"{app.Name} adicionado à fila de remoção. Acompanhe pelo ícone de fila na barra de título.";

        try
        {
            var result = await OperationRunner.RunUninstallAsync(
                _queueService, _wingetExecutor, app.Id, app.Name, app.IconUrl, _installedAppsService);

            if (result.Success)
            {
                // Dispara AppOnPropertyChanged -> UpdateInstallActionsVisibility, que volta
                // a mostrar "Instalar" no lugar de "Abrir"/"Desinstalar" — só faz sentido se
                // o painel ainda estiver mostrando esse mesmo app.
                app.IsInstalled = false;
                if (_app == app)
                {
                    StatusText.Text = $"{app.Name} removido.";
                }
            }
            else if (_app == app)
            {
                StatusText.Text = WingetErrorTranslator.ToMessage(result.FailureReason, "desinstalar", app.Name);
            }
        }
        catch (Exception ex)
        {
            if (_app == app)
            {
                StatusText.Text = $"Erro ao desinstalar {app.Name}: {ex.Message}";
            }
        }
        finally
        {
            UninstallButton.IsEnabled = true;
        }
    }

    private void OpenLink_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { Tag: string { Length: > 0 } url })
        {
            return;
        }

        try
        {
            Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            StatusText.Text = $"Não foi possível abrir o link: {ex.Message}";
        }
    }
}
