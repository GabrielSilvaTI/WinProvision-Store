using System;
using System.Reflection;
using System.Threading.Tasks;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using Microsoft.Extensions.DependencyInjection;
using WinProvision.Core.Models;
using WinProvision.Core.Services;
using WinProvision.Store.Services;
using Wpf.Ui.Appearance;
using Wpf.Ui.Controls;

namespace WinProvision.Store;

public partial class SettingsPage : Page
{
    private readonly CacheService _cacheService;
    private readonly AppUpdateService _updateService;
    private readonly InstallationPreferencesService _installationPreferences;
    private bool _initializingInstallMethod = true;
    private AppUpdateCheckResult? _pendingUpdate;

    public SettingsPage()
    {
        InitializeComponent();

        _cacheService = App.Services.GetRequiredService<CacheService>();
        _updateService = App.Services.GetRequiredService<AppUpdateService>();
        _installationPreferences = App.Services.GetRequiredService<InstallationPreferencesService>();

        InstallMethodComboBox.SelectedItem = InstallMethodComboBox.Items
            .OfType<ComboBoxItem>()
            .FirstOrDefault(item => Enum.TryParse<PackageInstallMethod>(item.Tag?.ToString(), out var method)
                                    && method == _installationPreferences.PreferredMethod)
            ?? InstallMethodComboBox.Items[0];
        UpdateInstallMethodDescription(_installationPreferences.PreferredMethod);
        _initializingInstallMethod = false;

        RefreshThemeButtonsUi();
        VersionText.Text = $"Versão {GetApplicationVersion()}";

        ApplicationThemeManager.Changed += ApplicationThemeManager_Changed;
        Unloaded += (_, _) =>
        {
            ApplicationThemeManager.Changed -= ApplicationThemeManager_Changed;
        };
    }

    private static string GetApplicationVersion()
    {
        var version = Assembly.GetEntryAssembly()?.GetName().Version;
        return version is null ? "desconhecida" : version.ToString(3);
    }

    private void InstallMethodComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_initializingInstallMethod || InstallMethodComboBox.SelectedItem is not ComboBoxItem { Tag: string tag }
            || !Enum.TryParse(tag, out PackageInstallMethod method))
            return;

        try
        {
            _installationPreferences.SetPreferredMethod(method);
            UpdateInstallMethodDescription(method);
            StatusText.Text = "Método de instalação salvo.";
        }
        catch
        {
            StatusText.Text = "Não foi possível salvar a preferência. Tente novamente.";
        }
    }

    private void UpdateInstallMethodDescription(PackageInstallMethod method)
    {
        InstallMethodDescriptionText.Text = method switch
        {
            PackageInstallMethod.ComApi => "Usa somente o WinGet COM.",
            PackageInstallMethod.WinProvisionApi => "Usa somente a API da WinProvision Store.",
            PackageInstallMethod.WingetCli => "Usa somente o WinGet CLI.",
            _ => "Tenta outros métodos se a instalação falhar."
        };
    }

    // -------------------------------------------------------------
    // ATUALIZAÇÕES DO APP (independente do provisionamento de pacotes)
    // -------------------------------------------------------------

    private async void CheckUpdateButton_Click(object sender, RoutedEventArgs e)
    {
        CheckUpdateButton.IsEnabled = false;
        UpdateAvailablePanel.Visibility = Visibility.Collapsed;
        _pendingUpdate = null;
        UpdateSubtitleText.Text = "Procurando atualizações...";

        try
        {
            var result = await _updateService.CheckForUpdateAsync();

            if (!result.Success)
            {
                UpdateSubtitleText.Text = result.Error;
                return;
            }

            if (!result.UpdateAvailable)
            {
                UpdateSubtitleText.Text = result.LatestVersion is not null
                    ? $"Você já está na versão mais recente ({result.CurrentVersion?.ToString(3)})."
                    : "Seu build já é o mais recente publicado no canal nightly.";
                return;
            }

            _pendingUpdate = result;
            UpdateSubtitleText.Text = $"Versão instalada: {result.CurrentVersion?.ToString(3)}";
            UpdateAvailableText.Text = result.LatestVersion is not null
                ? $"Nova versão disponível: {result.LatestVersion.ToString(3)}"
                : BuildNightlyAvailableText(result);
            UpdateAvailablePanel.Visibility = Visibility.Visible;
        }
        catch
        {
            UpdateSubtitleText.Text = "Não foi possível verificar atualizações. Tente novamente.";
        }
        finally
        {
            CheckUpdateButton.IsEnabled = true;
        }
    }

    private static string BuildNightlyAvailableText(AppUpdateCheckResult result)
    {
        string label = string.IsNullOrEmpty(result.ReleaseLabel) ? "Novo build nightly disponível" : $"Novo build nightly disponível: {result.ReleaseLabel}";
        return result.PublishedAt is { } publishedAt
            ? $"{label} (publicado em {publishedAt.ToLocalTime():dd/MM HH:mm})."
            : $"{label}.";
    }

    private async void InstallUpdateButton_Click(object sender, RoutedEventArgs e)
    {
        if (_pendingUpdate is null) return;

        CheckUpdateButton.IsEnabled = false;
        InstallUpdateButton.IsEnabled = false;
        UpdateProgressText.Visibility = Visibility.Visible;
        UpdateProgressBar.Visibility = Visibility.Visible;
        UpdateProgressText.Text = "Baixando o instalador...";

        var progress = new Progress<double>(fraction =>
        {
            UpdateProgressBar.Value = fraction;
            UpdateProgressText.Text = $"Baixando o instalador... {fraction:P0}";
        });

        try
        {
            string setupPath = await _updateService.DownloadInstallerAsync(_pendingUpdate, progress);

            UpdateProgressText.Text = "Instalando e reiniciando o WinProvision Store...";
            AppUpdateService.ScheduleSilentInstallAndRestart(setupPath);

            // O instalador precisa que este processo já não esteja rodando pra substituir
            // seus arquivos; o script agendado espera este PID sumir e reabre o app depois.
            await Task.Delay(TimeSpan.FromMilliseconds(500));
            Application.Current.Shutdown();
        }
        catch
        {
            UpdateProgressText.Text = "Não foi possível instalar a atualização. Tente novamente.";
            UpdateProgressBar.Visibility = Visibility.Collapsed;
            CheckUpdateButton.IsEnabled = true;
            InstallUpdateButton.IsEnabled = true;
        }
    }

    // -------------------------------------------------------------
    // TEMA DA APLICAÇÃO
    // -------------------------------------------------------------

    private void RefreshThemeButtonsUi()
    {
        var current = ApplicationThemeManager.GetAppTheme();
        bool isDark = current == ApplicationTheme.Dark;

        LightThemeButton.Appearance = isDark ? ControlAppearance.Secondary : ControlAppearance.Primary;
        DarkThemeButton.Appearance = isDark ? ControlAppearance.Primary : ControlAppearance.Secondary;
    }

    private void ApplicationThemeManager_Changed(ApplicationTheme currentApplicationTheme, System.Windows.Media.Color systemAccent)
    {
        Dispatcher.BeginInvoke(RefreshThemeButtonsUi);
    }

    private void LightThemeButton_Click(object sender, RoutedEventArgs e)
    {
        ApplicationThemeManager.Apply(ApplicationTheme.Light);
        RefreshThemeButtonsUi();
        StatusText.Text = "Tema claro aplicado.";
    }

    private void DarkThemeButton_Click(object sender, RoutedEventArgs e)
    {
        ApplicationThemeManager.Apply(ApplicationTheme.Dark);
        RefreshThemeButtonsUi();
        StatusText.Text = "Tema escuro aplicado.";
    }

    // -------------------------------------------------------------
    // CACHE LOCAL
    // -------------------------------------------------------------

    private async void ClearCacheButton_Click(object sender, RoutedEventArgs e)
    {
        ClearCacheButton.IsEnabled = false;
        StatusText.Text = "Limpando catálogo, ícones e cache local...";

        try
        {
            await _cacheService.ClearAsync();
            Converters.AsyncImage.ClearCache();
            StatusText.Text = "Cache local limpo com sucesso.";
        }
        catch
        {
            StatusText.Text = "Não foi possível limpar o cache. Tente novamente.";
        }
        finally
        {
            ClearCacheButton.IsEnabled = true;
        }
    }
}
