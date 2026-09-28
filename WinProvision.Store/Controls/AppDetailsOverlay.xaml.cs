using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Imaging;
using Microsoft.Win32;
using WinProvision.Core.Models;
using WinProvision.Core.Services;
using WinProvision.Store.Converters;
using WinProvision.Store.Services;
using Wpf.Ui.Controls;

namespace WinProvision.Store.Controls;

/// <summary>
/// Painel de detalhes do pacote, exibido como overlay sobre o MainWindow.
/// Apresenta metadados e ações para pacotes disponíveis no catálogo.
/// </summary>
public partial class AppDetailsOverlay : UserControl
{
    private readonly PackageCollectionService _collectionService;
    private readonly WingetExecutor _wingetExecutor;
    private readonly OperationsQueueService _queueService;
    private readonly InstalledAppsService _installedAppsService;
    private readonly InstalledPackagesViewModel _installedPackagesViewModel;

    private AppEntry? _app;
    private string? _installLocation;
    private string? _availableUpdateVersion;
    private CancellationTokenSource? _detailsCts;
    private CancellationTokenSource? _screenshotLoadCts;
    private string[] _screenshots = [];
    private int _screenshotIndex;

    private sealed class ExtendedPackageInfo
    {
        public string? Version { get; set; }
        public string? Publisher { get; set; }
        public string? Author { get; set; }
        public string? Description { get; set; }
        public string? Homepage { get; set; }
        public string? License { get; set; }
        public string? LicenseUrl { get; set; }
        public string? ReleaseDate { get; set; }
        public string? InstallerType { get; set; }
        public string? InstallerUrl { get; set; }
        public string? Sha256 { get; set; }
        public string? Dependencies { get; set; }
        public string? ReleaseNotes { get; set; }
        public string? ReleaseNotesUrl { get; set; }
        public List<string> Tags { get; } = [];
    }

    public AppDetailsOverlay(AppDetailsOverlayService overlayService, PackageCollectionService collectionService,
        WingetExecutor wingetExecutor, OperationsQueueService queueService,
        InstalledAppsService installedAppsService, InstalledPackagesViewModel installedPackagesViewModel)
    {
        InitializeComponent();

        _collectionService = collectionService;
        _wingetExecutor = wingetExecutor;
        _queueService = queueService;
        _installedAppsService = installedAppsService;
        _installedPackagesViewModel = installedPackagesViewModel;

        Visibility = Visibility.Collapsed;
        SizeChanged += AppDetailsOverlay_SizeChanged;
        overlayService.Requested += app => Show(app, null, null);
        overlayService.UpdateRequested += (app, current, available) => Show(app, current, available);
    }

    private void AppDetailsOverlay_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        Card.Width = Math.Clamp(e.NewSize.Width - 40, 760, 1040);
        Card.MaxHeight = Math.Max(480, e.NewSize.Height - 40);
    }

    private void Show(AppEntry app, string? currentVersion, string? availableVersion)
    {
        _detailsCts?.Cancel();
        _detailsCts = new CancellationTokenSource();
        _screenshotLoadCts?.Cancel();
        _screenshotLoadCts = new CancellationTokenSource();

        if (_app is not null)
        {
            _app.PropertyChanged -= AppOnPropertyChanged;
        }

        _app = app;
        _availableUpdateVersion = availableVersion;

        // ── 1. Inicialização síncrona com os dados já disponíveis no AppEntry ──
        AsyncImage.SetSourceUrl(AppIcon, app.IconUrl);
        AppNameText.Text = app.Name;

        string sourceLabel = IsMicrosoftStoreSource(app.Source)
            ? "Microsoft Store"
            : IsSupportedCollectionSource(app.Source)
                ? $"WinGet: {app.Source}"
                : "PC Local";
        PackageSourceText.Text = sourceLabel;
        PackageManagerText.Text = sourceLabel;

        string displayedVersion = availableVersion ?? app.Version;
        PackageVersionText.Text = string.IsNullOrWhiteSpace(displayedVersion) ? "Não informada" : displayedVersion;

        DescriptionText.Text = string.IsNullOrWhiteSpace(app.Description)
            ? "Nenhuma descrição disponível."
            : app.Description;

        _screenshots = app.StoreScreenshotUrls?
            .Where(url => Uri.TryCreate(url, UriKind.Absolute, out var uri) && uri.Scheme == Uri.UriSchemeHttps)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Take(12)
            .ToArray() ?? [];
        _screenshotIndex = 0;
        ScreenshotsPanel.Visibility = Visibility.Visible;
        ScreenshotsEmptyText.Text = _screenshots.Length == 0
            ? "Este pacote ainda não tem capturas de tela no catálogo."
            : "Não foi possível carregar esta captura. Verifique sua conexão e tente novamente.";
        ScreenshotsEmptyText.Visibility = _screenshots.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
        ScreenshotImage.Visibility = _screenshots.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
        ScreenshotLoadingBar.Visibility = Visibility.Collapsed;
        PreviousScreenshotButton.Visibility = _screenshots.Length > 1 ? Visibility.Visible : Visibility.Collapsed;
        NextScreenshotButton.Visibility = _screenshots.Length > 1 ? Visibility.Visible : Visibility.Collapsed;
        ScreenshotLightbox.Visibility = Visibility.Collapsed;
        if (_screenshots.Length > 0)
            _ = LoadCurrentScreenshotAsync(_app, _screenshotLoadCts.Token);

        // Tags
        TagsList.ItemsSource = app.Tags;
        TagsList.Visibility = app.Tags.Count > 0 ? Visibility.Visible : Visibility.Collapsed;

        // ID do Pacote
        PackageIdText.Text = app.Id;

        // Manifesto URL
        SetupManifestUrl(app.Id, app.Source);

        // Página Inicial
        SetupHomepage(app.Homepage);

        // Desenvolvedor e Autor
        DeveloperText.Text = !string.IsNullOrWhiteSpace(app.Publisher) ? app.Publisher : "—";
        AuthorText.Text = !string.IsNullOrWhiteSpace(app.Publisher) ? app.Publisher : "—";

        // Licença
        SetupLicense(app.License, app.LicenseUrl);

        // Última atualização
        bool isOfficeStoreOffer = app.Tags.Contains("Office", StringComparer.OrdinalIgnoreCase);
        LastUpdatedText.Text = isOfficeStoreOffer ? "Microsoft Store" : "Carregando...";

        // Tipo de instalador e detalhes
        InstallerTypeText.Text = isOfficeStoreOffer ? "Produto Microsoft 365" : "Carregando...";
        InstallerUrlLink.Visibility = Visibility.Collapsed;
        InstallerUrlLink.Tag = null;
        Sha256Text.Text = isOfficeStoreOffer ? "Não aplicável" : "Carregando...";

        // Tamanho do instalador
        SetupSize(app.InstallerSizeBytes);

        // Dependências e Notas
        DependenciesText.Text = "Nenhuma dependência especificada";
        ReleaseNotesText.Text = "Não disponível";
        SetupReleaseNotesUrl(app.ReleaseNotesUrl);

        // Update comparison panel
        UpdateVersionPanel.Visibility = currentVersion is not null && availableVersion is not null
            ? Visibility.Visible : Visibility.Collapsed;
        CurrentUpdateVersionText.Text = currentVersion ?? string.Empty;
        AvailableUpdateVersionText.Text = availableVersion ?? string.Empty;

        // Status & Opções
        StatusText.Text = string.Empty;
        _installLocation = null;
        SelectedLocationText.Text = "Local padrão do sistema";
        OptionsBodyPanel.Visibility = Visibility.Collapsed;
        OptionsChevronIcon.Symbol = Wpf.Ui.Controls.SymbolRegular.ChevronDown16;

        _app.PropertyChanged += AppOnPropertyChanged;
        UpdateInstallActionsVisibility();

        // ── 2. Animação de entrada ──
        Scrim.BeginAnimation(UIElement.OpacityProperty, null);
        Card.BeginAnimation(UIElement.OpacityProperty, null);
        CardTransform.BeginAnimation(TranslateTransform.YProperty, null);
        Scrim.Opacity = 0;
        Card.Opacity = 0;
        CardTransform.Y = 12;
        Visibility = Visibility.Visible;
        AnimateIn();
        Focus();

        // ── 3. Busca assíncrona de informações completas via winget show ──
        if (isOfficeStoreOffer)
        {
            LoadingProgressBar.Visibility = Visibility.Collapsed;
            DependenciesText.Text = "Gerenciado pela Microsoft Store";
            ReleaseNotesText.Text = "Consulte os detalhes do produto na Microsoft Store.";
        }
        else
        {
            _ = LoadExtendedDetailsAsync(app, _detailsCts.Token);
        }
    }

    private void SetupManifestUrl(string id, string source)
    {
        if (string.Equals(source, "msstore", StringComparison.OrdinalIgnoreCase))
        {
            string url = $"https://apps.microsoft.com/detail/{id}";
            ManifestLinkText.Text = url;
            ManifestLink.Tag = url;
            ManifestLink.Visibility = Visibility.Visible;
            return;
        }

        // WinGet manifest no GitHub
        string firstChar = id.Length > 0 ? char.ToLowerInvariant(id[0]).ToString() : "a";
        string parts = id.Replace('.', '/');
        string manifestUrl = $"https://github.com/microsoft/winget-pkgs/tree/master/manifests/{firstChar}/{parts}";
        ManifestLinkText.Text = manifestUrl;
        ManifestLink.Tag = manifestUrl;
        ManifestLink.Visibility = Visibility.Visible;
    }

    private void SetupHomepage(string? url)
    {
        bool hasUrl = !string.IsNullOrWhiteSpace(url);
        HomepageLink.Visibility = hasUrl ? Visibility.Visible : Visibility.Collapsed;
        HomepageNotAvailableText.Visibility = hasUrl ? Visibility.Collapsed : Visibility.Visible;

        if (hasUrl)
        {
            HomepageLinkText.Text = url;
            HomepageLink.Tag = url;
        }
    }

    private void SetupLicense(string? name, string? url)
    {
        bool hasName = !string.IsNullOrWhiteSpace(name);
        bool hasUrl = !string.IsNullOrWhiteSpace(url);

        LicenseText.Text = hasName ? name : (hasUrl ? string.Empty : "Não informada");
        LicenseLink.Visibility = hasUrl ? Visibility.Visible : Visibility.Collapsed;

        if (hasUrl)
        {
            LicenseLinkText.Text = url;
            LicenseLink.Tag = url;
        }
    }

    private void SetupSize(long? sizeBytes)
    {
        if (sizeBytes is > 0)
        {
            double mb = sizeBytes.Value / 1024d / 1024d;
            InstallerSizeText.Text = mb >= 1024
                ? $"(~ {mb / 1024:0.0} GB)"
                : $"(~ {mb:0} MB)";
        }
        else
        {
            InstallerSizeText.Text = "(Tamanho desconhecido)";
        }
    }

    private void SetupReleaseNotesUrl(string? url)
    {
        bool hasUrl = !string.IsNullOrWhiteSpace(url);
        ReleaseNotesUrlLink.Visibility = hasUrl ? Visibility.Visible : Visibility.Collapsed;
        ReleaseNotesUrlNotAvailableText.Visibility = hasUrl ? Visibility.Collapsed : Visibility.Visible;

        if (hasUrl)
        {
            ReleaseNotesUrlLinkText.Text = url;
            ReleaseNotesUrlLink.Tag = url;
        }
    }

    private async Task LoadExtendedDetailsAsync(AppEntry app, CancellationToken cancellationToken)
    {
        LoadingProgressBar.Visibility = Visibility.Visible;

        try
        {
            string args = $"show --id \"{app.Id}\" --exact --source {app.Source} --accept-source-agreements --disable-interactivity";
            var process = new Process
            {
                StartInfo = new ProcessStartInfo
                {
                    FileName = "winget.exe",
                    Arguments = args,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    StandardOutputEncoding = System.Text.Encoding.UTF8
                }
            };

            process.Start();
            string output = await process.StandardOutput.ReadToEndAsync(cancellationToken);
            await process.WaitForExitAsync(cancellationToken);

            if (cancellationToken.IsCancellationRequested || _app != app)
                return;

            var info = ParseWingetShowOutput(output);

            Dispatcher.Invoke(() =>
            {
                if (_app != app) return;

                if (!string.IsNullOrWhiteSpace(info.Version))
                    PackageVersionText.Text = info.Version;

                if (!string.IsNullOrWhiteSpace(info.Publisher))
                    DeveloperText.Text = info.Publisher;

                if (!string.IsNullOrWhiteSpace(info.Author))
                    AuthorText.Text = info.Author;

                if (!string.IsNullOrWhiteSpace(info.Homepage))
                    SetupHomepage(info.Homepage);

                if (!string.IsNullOrWhiteSpace(info.License) || !string.IsNullOrWhiteSpace(info.LicenseUrl))
                    SetupLicense(info.License ?? app.License, info.LicenseUrl ?? app.LicenseUrl);

                LastUpdatedText.Text = !string.IsNullOrWhiteSpace(info.ReleaseDate)
                    ? info.ReleaseDate
                    : "Não informada";

                InstallerTypeText.Text = !string.IsNullOrWhiteSpace(info.InstallerType)
                    ? info.InstallerType
                    : "exe";

                if (!string.IsNullOrWhiteSpace(info.InstallerUrl))
                {
                    InstallerUrlLink.Visibility = Visibility.Visible;
                    InstallerUrlLinkText.Text = info.InstallerUrl;
                    InstallerUrlLink.Tag = info.InstallerUrl;
                    DownloadInstallerLink.Tag = info.InstallerUrl;
                    DownloadInstallerLink.Visibility = Visibility.Visible;
                }
                else
                {
                    InstallerUrlLink.Visibility = Visibility.Collapsed;
                    DownloadInstallerLink.Visibility = Visibility.Collapsed;
                }

                Sha256Text.Text = !string.IsNullOrWhiteSpace(info.Sha256)
                    ? info.Sha256.ToUpperInvariant()
                    : "Não especificado";

                if (!string.IsNullOrWhiteSpace(info.Dependencies))
                    DependenciesText.Text = info.Dependencies;

                if (!string.IsNullOrWhiteSpace(info.ReleaseNotes))
                    ReleaseNotesText.Text = info.ReleaseNotes;

                if (!string.IsNullOrWhiteSpace(info.ReleaseNotesUrl))
                    SetupReleaseNotesUrl(info.ReleaseNotesUrl);

                if (info.Tags.Count > 0)
                {
                    TagsList.ItemsSource = info.Tags;
                    TagsList.Visibility = Visibility.Visible;
                }

                if (!string.IsNullOrWhiteSpace(info.Description) && string.IsNullOrWhiteSpace(app.Description))
                {
                    DescriptionText.Text = info.Description;
                }
            });
        }
        catch (OperationCanceledException)
        {
            // Cancelado normalmente ao mudar de pacote
        }
        catch
        {
            // Fallback gracioso mantendo os metadados existentes
            Dispatcher.Invoke(() =>
            {
                if (_app == app)
                {
                    LastUpdatedText.Text = "Não disponível";
                    InstallerTypeText.Text = "Padrão";
                    Sha256Text.Text = "Não disponível";
                }
            });
        }
        finally
        {
            Dispatcher.Invoke(() =>
            {
                LoadingProgressBar.Visibility = Visibility.Collapsed;
            });
        }
    }

    private static ExtendedPackageInfo ParseWingetShowOutput(string output)
    {
        var info = new ExtendedPackageInfo();
        var lines = output.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries);
        string currentSection = "";

        foreach (var rawLine in lines)
        {
            string line = rawLine.Trim();
            if (string.IsNullOrWhiteSpace(line)) continue;

            // Seções pai (ex.: Marcas:, Instaladora:, Documentação:)
            if (line.EndsWith(':') && !line.Contains("://", StringComparison.OrdinalIgnoreCase))
            {
                currentSection = line.TrimEnd(':').Trim();
                continue;
            }

            int colonIdx = line.IndexOf(':');
            if (colonIdx > 0 && !line.StartsWith("http", StringComparison.OrdinalIgnoreCase))
            {
                string key = line[..colonIdx].Trim();
                string val = line[(colonIdx + 1)..].Trim();

                if (key.Equals("Versão", StringComparison.OrdinalIgnoreCase) || key.Equals("Version", StringComparison.OrdinalIgnoreCase))
                    info.Version = val;
                else if (key.Equals("Fornecedor", StringComparison.OrdinalIgnoreCase) || key.Equals("Publisher", StringComparison.OrdinalIgnoreCase))
                    info.Publisher = val;
                else if (key.Equals("Autor", StringComparison.OrdinalIgnoreCase) || key.Equals("Author", StringComparison.OrdinalIgnoreCase))
                    info.Author = val;
                else if (key.Equals("Descrição", StringComparison.OrdinalIgnoreCase) || key.Equals("Description", StringComparison.OrdinalIgnoreCase))
                    info.Description = val;
                else if (key.Equals("Página inicial", StringComparison.OrdinalIgnoreCase) || key.Equals("Homepage", StringComparison.OrdinalIgnoreCase))
                    info.Homepage = val;
                else if (key.Equals("Licença", StringComparison.OrdinalIgnoreCase) || key.Equals("License", StringComparison.OrdinalIgnoreCase))
                    info.License = val;
                else if (key.Equals("URL da licença", StringComparison.OrdinalIgnoreCase) || key.Equals("License Url", StringComparison.OrdinalIgnoreCase))
                    info.LicenseUrl = val;
                else if (key.Equals("Tipo de instalador", StringComparison.OrdinalIgnoreCase) || key.Equals("Installer Type", StringComparison.OrdinalIgnoreCase))
                    info.InstallerType = val;
                else if (key.Contains("URL do instalador", StringComparison.OrdinalIgnoreCase) || key.Contains("Installer Url", StringComparison.OrdinalIgnoreCase))
                    info.InstallerUrl = val;
                else if (key.Contains("SHA256", StringComparison.OrdinalIgnoreCase))
                    info.Sha256 = val;
                else if (key.Contains("Lançamento", StringComparison.OrdinalIgnoreCase) || key.Contains("Release Date", StringComparison.OrdinalIgnoreCase))
                    info.ReleaseDate = val;
                else if (key.Contains("Dependência", StringComparison.OrdinalIgnoreCase) || key.Contains("Dependencies", StringComparison.OrdinalIgnoreCase))
                    info.Dependencies = val;
                else if (key.Contains("Notas de lançamento", StringComparison.OrdinalIgnoreCase) || key.Contains("Release Notes", StringComparison.OrdinalIgnoreCase))
                    info.ReleaseNotes = val;
                else if (key.Contains("URL das notas", StringComparison.OrdinalIgnoreCase) || key.Contains("Release Notes Url", StringComparison.OrdinalIgnoreCase))
                    info.ReleaseNotesUrl = val;
            }
            else if (currentSection.Equals("Marcas", StringComparison.OrdinalIgnoreCase) || currentSection.Equals("Tags", StringComparison.OrdinalIgnoreCase))
            {
                if (!string.IsNullOrWhiteSpace(line))
                    info.Tags.Add(line);
            }
        }

        return info;
    }

    private void Close()
    {
        _detailsCts?.Cancel();
        _screenshotLoadCts?.Cancel();
        ScreenshotLightbox.Visibility = Visibility.Collapsed;
        if (_app is not null)
        {
            _app.PropertyChanged -= AppOnPropertyChanged;
            _app = null;
        }

        AnimateOut();
    }

    private void AnimateIn()
    {
        var easing = new QuadraticEase { EasingMode = EasingMode.EaseOut };
        Scrim.BeginAnimation(UIElement.OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(160))
        {
            EasingFunction = easing
        });
        Card.BeginAnimation(UIElement.OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(190))
        {
            EasingFunction = easing
        });
        CardTransform.BeginAnimation(TranslateTransform.YProperty, new DoubleAnimation(12, 0, TimeSpan.FromMilliseconds(220))
        {
            EasingFunction = easing
        });
    }

    private void AnimateOut()
    {
        var easing = new QuadraticEase { EasingMode = EasingMode.EaseIn };
        var fadeOut = new DoubleAnimation(1, 0, TimeSpan.FromMilliseconds(130))
        {
            EasingFunction = easing
        };
        fadeOut.Completed += (_, _) =>
        {
            Visibility = Visibility.Collapsed;
            CardTransform.Y = 12;
        };

        Scrim.BeginAnimation(UIElement.OpacityProperty, new DoubleAnimation(1, 0, TimeSpan.FromMilliseconds(130))
        {
            EasingFunction = easing
        });
        Card.BeginAnimation(UIElement.OpacityProperty, fadeOut);
        CardTransform.BeginAnimation(TranslateTransform.YProperty, new DoubleAnimation(0, 12, TimeSpan.FromMilliseconds(150))
        {
            EasingFunction = easing
        });
    }

    private void AppDetailsOverlay_IsVisibleChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
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
            if (ScreenshotLightbox.Visibility == Visibility.Visible)
                ScreenshotLightbox.Visibility = Visibility.Collapsed;
            else
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

    private void UpdateInstallActionsVisibility()
    {
        if (_app is null) return;

        bool isUpdateContext = !string.IsNullOrWhiteSpace(_availableUpdateVersion);
        UpdateButton.Visibility = isUpdateContext ? Visibility.Visible : Visibility.Collapsed;
        InstallSplitGroup.Visibility = isUpdateContext || _app.IsInstalled ? Visibility.Collapsed : Visibility.Visible;
        UninstallSplitGroup.Visibility = isUpdateContext || !_app.IsInstalled ? Visibility.Collapsed : Visibility.Visible;

    }

    private void Scrim_MouseLeftButtonUp(object sender, MouseButtonEventArgs e) => Close();

    private void Card_MouseLeftButtonUp(object sender, MouseButtonEventArgs e) => e.Handled = true;

    private void CloseButton_Click(object sender, RoutedEventArgs e) => Close();

    private async Task LoadCurrentScreenshotAsync(AppEntry? app, CancellationToken cancellationToken)
    {
        if (app is null || _screenshots.Length == 0) return;

        int requestedIndex = _screenshotIndex;
        string url = _screenshots[requestedIndex];
        ScreenshotImage.Source = null;
        ScreenshotLightboxImage.Source = null;
        ScreenshotsEmptyText.Visibility = Visibility.Collapsed;
        ScreenshotLoadingBar.Visibility = Visibility.Visible;
        ScreenshotCounterText.Text = $"{requestedIndex + 1} / {_screenshots.Length}";

        BitmapSource? bitmap = await AsyncImage.LoadBitmapAsync(url);
        if (cancellationToken.IsCancellationRequested || !ReferenceEquals(_app, app) || requestedIndex != _screenshotIndex)
            return;

        ScreenshotLoadingBar.Visibility = Visibility.Collapsed;
        if (bitmap is null)
        {
            ScreenshotsEmptyText.Text = "Não foi possível carregar esta captura. Verifique o endereço da imagem no catálogo.";
            ScreenshotsEmptyText.Visibility = Visibility.Visible;
            return;
        }

        ScreenshotImage.Source = bitmap;
        if (ScreenshotLightbox.Visibility == Visibility.Visible)
            ScreenshotLightboxImage.Source = bitmap;
    }

    private void PreviousScreenshotButton_Click(object sender, RoutedEventArgs e) => MoveScreenshot(-1);
    private void NextScreenshotButton_Click(object sender, RoutedEventArgs e) => MoveScreenshot(1);

    private void MoveScreenshot(int offset)
    {
        if (_screenshots.Length < 2 || _app is null) return;
        _screenshotIndex = (_screenshotIndex + offset + _screenshots.Length) % _screenshots.Length;
        _screenshotLoadCts?.Cancel();
        _screenshotLoadCts = new CancellationTokenSource();
        _ = LoadCurrentScreenshotAsync(_app, _screenshotLoadCts.Token);
    }

    private void ScreenshotImage_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (ScreenshotImage.Source is null) return;
        ScreenshotLightboxImage.Source = ScreenshotImage.Source;
        ScreenshotLightbox.Visibility = Visibility.Visible;
        e.Handled = true;
    }

    private void CloseScreenshotLightboxButton_Click(object sender, RoutedEventArgs e)
    {
        ScreenshotLightbox.Visibility = Visibility.Collapsed;
        e.Handled = true;
    }

    private void ScreenshotLightbox_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (ReferenceEquals(e.OriginalSource, ScreenshotLightbox))
            ScreenshotLightbox.Visibility = Visibility.Collapsed;
        e.Handled = true;
    }
    private void OptionsHeader_Click(object sender, MouseButtonEventArgs e)
    {
        bool isExpanded = OptionsBodyPanel.Visibility == Visibility.Visible;
        OptionsBodyPanel.Visibility = isExpanded ? Visibility.Collapsed : Visibility.Visible;
        OptionsChevronIcon.Symbol = isExpanded
            ? Wpf.Ui.Controls.SymbolRegular.ChevronDown16
            : Wpf.Ui.Controls.SymbolRegular.ChevronUp24;
    }

    private void SaveOptionsButton_Click(object sender, RoutedEventArgs e)
    {
        OptionsBodyPanel.Visibility = Visibility.Collapsed;
        OptionsChevronIcon.Symbol = Wpf.Ui.Controls.SymbolRegular.ChevronDown16;
        StatusText.Text = "Opções salvas para esta sessão.";
    }

    private void InstallChevronButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement fe)
        {
            var menu = new ContextMenu();

            var miUser = new System.Windows.Controls.MenuItem { Header = "Instalar para o usuário atual" };
            miUser.Click += (_, _) =>
            {
                InstallAllUsersCheckBox.IsChecked = false;
                InstallButton_Click(sender, e);
            };
            menu.Items.Add(miUser);

            var miAll = new System.Windows.Controls.MenuItem { Header = "Instalar para todos os usuários (Admin)" };
            miAll.Click += (_, _) =>
            {
                InstallAllUsersCheckBox.IsChecked = true;
                InstallButton_Click(sender, e);
            };
            menu.Items.Add(miAll);

            if (!string.IsNullOrWhiteSpace(InstallerUrlLink.Tag as string))
            {
                var miDownload = new System.Windows.Controls.MenuItem { Header = "Baixar instalador apenas" };
                miDownload.Click += (_, _) => OpenUrl((string)InstallerUrlLink.Tag);
                menu.Items.Add(miDownload);
            }

            menu.Items.Add(new Separator());
            bool alreadyAdded = _app is not null && _collectionService.ActiveTab.Items.Any(item =>
                string.Equals(item.Id, _app.Id, StringComparison.OrdinalIgnoreCase));
            var miAddToPackages = new System.Windows.Controls.MenuItem
            {
                Header = !CanAddCurrentAppToPackages ? "Disponível apenas para WinGet e Microsoft Store" : alreadyAdded ? "Já está nos pacotes" : "Adicionar app aos pacotes",
                IsEnabled = CanAddCurrentAppToPackages && !alreadyAdded
            };
            miAddToPackages.Click += (_, _) => AddCurrentAppToPackages();
            menu.Items.Add(miAddToPackages);
            menu.PlacementTarget = fe;
            menu.IsOpen = true;
        }
    }

    private void UninstallChevronButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement target) return;

        bool alreadyAdded = _app is not null && _collectionService.ActiveTab.Items.Any(item =>
            string.Equals(item.Id, _app.Id, StringComparison.OrdinalIgnoreCase));
        var menu = new ContextMenu();
        var addToPackages = new System.Windows.Controls.MenuItem
        {
            Header = !CanAddCurrentAppToPackages ? "Disponível apenas para WinGet e Microsoft Store" : alreadyAdded ? "Já está nos pacotes" : "Adicionar app aos pacotes",
            IsEnabled = CanAddCurrentAppToPackages && !alreadyAdded
        };
        addToPackages.Click += (_, _) => AddCurrentAppToPackages();
        menu.Items.Add(addToPackages);
        menu.PlacementTarget = target;
        menu.IsOpen = true;
    }
    private bool CanAddCurrentAppToPackages => _app is not null && IsSupportedCollectionSource(_app.Source);

    private static bool IsMicrosoftStoreSource(string? source) =>
        string.Equals(source?.Trim(), "msstore", StringComparison.OrdinalIgnoreCase)
        || string.Equals(source?.Trim(), "Microsoft Store", StringComparison.OrdinalIgnoreCase);

    private static bool IsSupportedCollectionSource(string? source) =>
        string.Equals(source?.Trim(), "winget", StringComparison.OrdinalIgnoreCase)
        || source?.Trim().StartsWith("WinGet:", StringComparison.OrdinalIgnoreCase) == true
        || string.Equals(source?.Trim(), "msstore", StringComparison.OrdinalIgnoreCase)
        || string.Equals(source?.Trim(), "Microsoft Store", StringComparison.OrdinalIgnoreCase);

    private void AddCurrentAppToPackages()
    {
        if (_app is null) return;
        if (!CanAddCurrentAppToPackages)
        {
            StatusText.Text = "Somente pacotes WinGet e Microsoft Store podem ser adicionados à coleção.";
            return;
        }

        int added = _collectionService.AddRangeToActive([_app]);
        string tabTitle = _collectionService.ActiveTab?.Title ?? "Perfil Padrão";
        StatusText.Text = added > 0
            ? $"{_app.Name} adicionado à coleção '{tabTitle}'."
            : $"{_app.Name} já está na coleção '{tabTitle}'.";
    }
    private async void InstallButton_Click(object sender, RoutedEventArgs e)
    {
        if (_app is null) return;

        var app = _app;
        InstallButton.IsEnabled = false;
        InstallChevronButton.IsEnabled = false;
        StatusText.Text = $"{app.Name} entrou na fila de instalação.";

        try
        {
            var result = await OperationRunner.RunInstallAsync(
                _queueService, _wingetExecutor, app.Id, app.Name, app.IconUrl, _installedAppsService,
                _installLocation, app.Source);

            if (result.Success)
            {
                app.IsInstalled = true;
                if (_app == app)
                    StatusText.Text = $"{app.Name} instalado.";
            }
            else if (_app == app)
            {
                StatusText.Text = WingetErrorTranslator.ToMessage(result.FailureReason, "instalar", app.Name);
            }
        }
        catch
        {
            if (_app == app)
                StatusText.Text = $"Não foi possível instalar {app.Name}. Tente novamente.";
        }
        finally
        {
            InstallButton.IsEnabled = true;
            InstallChevronButton.IsEnabled = true;
        }
    }

    private async void UpdateButton_Click(object sender, RoutedEventArgs e)
    {
        if (_app is null) return;

        var app = _app;
        UpdateButton.IsEnabled = false;
        StatusText.Text = $"{app.Name} entrou na fila de atualização.";

        try
        {
            var result = await OperationRunner.RunUpdateAsync(
                _queueService, _wingetExecutor, app.Id, app.Name, app.IconUrl, app.Source);

            if (_app == app)
            {
                StatusText.Text = result.Success
                    ? $"{app.Name} atualizado."
                    : WingetErrorTranslator.ToMessage(result.FailureReason, "atualizar", app.Name);
            }
        }
        catch
        {
            if (_app == app)
                StatusText.Text = $"Não foi possível atualizar {app.Name}. Tente novamente.";
        }
        finally
        {
            UpdateButton.IsEnabled = true;
        }
    }

    private void SelectLocationButton_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFolderDialog
        {
            Title = "Escolha o local de instalação",
            Multiselect = false
        };

        if (dialog.ShowDialog() != true || string.IsNullOrWhiteSpace(dialog.FolderName))
            return;

        _installLocation = dialog.FolderName;
        SelectedLocationText.Text = _installLocation;
    }

    private async void UninstallButton_Click(object sender, RoutedEventArgs e)
    {
        if (_app is null) return;

        var confirmDialog = new Wpf.Ui.Controls.MessageBox
        {
            Title = "Desinstalar aplicativo",
            Content = $"Desinstalar {_app.Name}?",
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
            return;

        if (_app is null) return;

        var app = _app;
        UninstallButton.IsEnabled = false;
        StatusText.Text = $"{app.Name} entrou na fila de desinstalação.";

        try
        {
            bool removed = await _installedPackagesViewModel.RemoveByIdentityAsync(app.Id, app.Name, app.IconUrl);
            if (removed)
            {
                app.IsInstalled = false;
                if (_app == app)
                    StatusText.Text = $"{app.Name} removido.";
            }
            else if (_app == app)
            {
                StatusText.Text = $"Não foi possível desinstalar {app.Name}.";
            }
        }
        catch
        {
            if (_app == app)
                StatusText.Text = $"Não foi possível desinstalar {app.Name}. Tente novamente.";
        }
        finally
        {
            UninstallButton.IsEnabled = true;
        }
    }

    private void OpenLink_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { Tag: string { Length: > 0 } url })
        {
            OpenUrl(url);
        }
    }

    private static void OpenUrl(string url)
    {
        try
        {
            Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
        }
        catch { }
    }
}
