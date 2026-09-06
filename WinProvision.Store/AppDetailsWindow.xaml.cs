using System;
using System.ComponentModel;
using System.Diagnostics;
using System.Windows;
using Wpf.Ui.Controls;
using WinProvision.Core.Models;
using WinProvision.Core.Services;
using WinProvision.Store.Converters;

namespace WinProvision.Store;

public partial class AppDetailsWindow : FluentWindow
{
    private readonly AppEntry _app;
    private readonly PackageCollectionService _collectionService;
    private readonly WingetExecutor _wingetExecutor;
    private readonly OperationsQueueService _queueService;
    private readonly InstalledAppsService _installedAppsService;
    private readonly AppLaunchService _appLaunchService;

    public AppDetailsWindow(AppEntry app, PackageCollectionService collectionService, WingetExecutor wingetExecutor,
        OperationsQueueService queueService, InstalledAppsService installedAppsService, AppLaunchService appLaunchService)
    {
        InitializeComponent();

        _app = app;
        _collectionService = collectionService;
        _wingetExecutor = wingetExecutor;
        _queueService = queueService;
        _installedAppsService = installedAppsService;
        _appLaunchService = appLaunchService;

        Title = app.Name;
        AsyncImage.SetSourceUrl(AppIcon, app.IconUrl);
        AppNameText.Text = app.Name;
        DescriptionText.Text = app.Description;
        DescriptionText.Visibility = string.IsNullOrWhiteSpace(app.Description) ? Visibility.Collapsed : Visibility.Visible;

        IdText.Text = app.Id;
        VersionText.Text = app.Version;
        ScoreText.Text = app.Score.ToString();

        SetupPublisher();
        SetupTags();
        SetupGitHubStars();
        SetupLicense();
        SetupLinkButtons();

        // Mantém o painel de ações (Instalar vs. Abrir+Desinstalar) sincronizado caso o
        // estado mude enquanto a janela está aberta (ex.: instalação concluída aqui mesmo).
        _app.PropertyChanged += AppOnPropertyChanged;
        Closed += (_, _) => _app.PropertyChanged -= AppOnPropertyChanged;

        UpdateInstallActionsVisibility();
    }

    private void AppOnPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(AppEntry.IsInstalled))
        {
            UpdateInstallActionsVisibility();
        }
    }

    /// <summary>Alterna entre "Instalar" (não instalado) e "Abrir" + "Desinstalar" (instalado).</summary>
    private void UpdateInstallActionsVisibility()
    {
        InstallButton.Visibility = _app.IsInstalled ? Visibility.Collapsed : Visibility.Visible;
        OpenButton.Visibility = _app.IsInstalled ? Visibility.Visible : Visibility.Collapsed;
        UninstallButton.Visibility = _app.IsInstalled ? Visibility.Visible : Visibility.Collapsed;
    }

    private void SetupPublisher()
    {
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
        TagsList.ItemsSource = _app.Tags;
        TagsList.Visibility = _app.Tags.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    private void SetupGitHubStars()
    {
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
        bool hasHomepage = !string.IsNullOrWhiteSpace(_app.Homepage);
        HomepageButton.Visibility = hasHomepage ? Visibility.Visible : Visibility.Collapsed;
        HomepageButton.Tag = _app.Homepage;

        bool hasReleaseNotes = !string.IsNullOrWhiteSpace(_app.ReleaseNotesUrl);
        ReleaseNotesButton.Visibility = hasReleaseNotes ? Visibility.Visible : Visibility.Collapsed;
        ReleaseNotesButton.Tag = _app.ReleaseNotesUrl;
    }

    private void AddToCatalogButton_Click(object sender, RoutedEventArgs e)
    {
        int added = _collectionService.AddRangeToActive([_app]);

        var tabTitle = _collectionService.ActiveTab?.Title ?? "Perfil Padrão";
        StatusText.Text = added == 1
            ? $"Adicionado à guia '{tabTitle}'."
            : $"Já estava na guia '{tabTitle}'.";
    }

    private async void InstallButton_Click(object sender, RoutedEventArgs e)
    {
        InstallButton.IsEnabled = false;
        StatusText.Text = $"{_app.Name} adicionado à fila de instalação. Acompanhe pelo ícone de fila na barra de título.";

        try
        {
            var result = await OperationRunner.RunInstallAsync(
                _queueService, _wingetExecutor, _app.Id, _app.Name, _app.IconUrl, _installedAppsService);

            if (result.Success)
            {
                // Dispara AppOnPropertyChanged -> UpdateInstallActionsVisibility, que troca
                // "Instalar" por "Abrir"/"Desinstalar" automaticamente.
                _app.IsInstalled = true;
                StatusText.Text = $"{_app.Name} instalado.";
            }
            else
            {
                StatusText.Text = $"Falha ao instalar {_app.Name}.";
            }
        }
        catch (Exception ex)
        {
            StatusText.Text = $"Erro ao instalar {_app.Name}: {ex.Message}";
        }
        finally
        {
            InstallButton.IsEnabled = true;
        }
    }

    private async void OpenButton_Click(object sender, RoutedEventArgs e)
    {
        OpenButton.IsEnabled = false;
        StatusText.Text = $"Abrindo {_app.Name}...";

        try
        {
            string? executablePath = await _appLaunchService.TryResolveExecutableAsync(_app);

            if (executablePath is null || !_appLaunchService.TryLaunch(executablePath))
            {
                StatusText.Text = $"Não foi possível localizar o executável de {_app.Name}.";
                return;
            }

            StatusText.Text = $"{_app.Name} aberto.";
        }
        finally
        {
            OpenButton.IsEnabled = true;
        }
    }

    private async void UninstallButton_Click(object sender, RoutedEventArgs e)
    {
        // Confirmação antes de qualquer ação destrutiva - mesmo padrão (Wpf.Ui MessageBox)
        // já usado no rodapé do MainWindow para confirmar o fechamento do app.
        var confirmDialog = new Wpf.Ui.Controls.MessageBox
        {
            Title = "Desinstalar aplicativo",
            Content = $"Tem certeza que deseja desinstalar \"{_app.Name}\"?",
            PrimaryButtonText = "Desinstalar",
            CloseButtonText = "Cancelar"
        };

        var confirmResult = await confirmDialog.ShowDialogAsync();
        if (confirmResult != Wpf.Ui.Controls.MessageBoxResult.Primary)
        {
            return;
        }

        UninstallButton.IsEnabled = false;
        StatusText.Text = $"{_app.Name} adicionado à fila de remoção. Acompanhe pelo ícone de fila na barra de título.";

        try
        {
            var result = await OperationRunner.RunUninstallAsync(
                _queueService, _wingetExecutor, _app.Id, _app.Name, _app.IconUrl, _installedAppsService);

            if (result.Success)
            {
                // Dispara AppOnPropertyChanged -> UpdateInstallActionsVisibility, que volta
                // a mostrar "Instalar" no lugar de "Abrir"/"Desinstalar".
                _app.IsInstalled = false;
                StatusText.Text = $"{_app.Name} removido.";
            }
            else
            {
                StatusText.Text = $"Falha ao desinstalar {_app.Name}.";
            }
        }
        catch (Exception ex)
        {
            StatusText.Text = $"Erro ao desinstalar {_app.Name}: {ex.Message}";
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
