using System;
using System.Reflection;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using Microsoft.Extensions.DependencyInjection;
using Wpf.Ui.Appearance;
using Wpf.Ui.Controls;
using WinProvision.Core.Services;

namespace WinProvision.Store;

public partial class SettingsPage : Page
{
    private readonly CacheService _cacheService;

    public SettingsPage()
    {
        InitializeComponent();

        _cacheService = App.Services.GetRequiredService<CacheService>();

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
        catch (Exception ex)
        {
            StatusText.Text = $"Erro ao limpar o cache: {ex.Message}";
        }
        finally
        {
            ClearCacheButton.IsEnabled = true;
        }
    }
}
