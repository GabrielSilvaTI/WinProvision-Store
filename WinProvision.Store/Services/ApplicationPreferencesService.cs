using System;
using System.IO;
using System.Text.Json;
using Microsoft.Win32;
using Wpf.Ui.Appearance;

namespace WinProvision.Store.Services;

public enum AppThemePreference
{
    System,
    Light,
    Dark
}

public sealed class ApplicationPreferencesService
{
    private const string StartupRegistryPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string StartupValueName = "WinProvisionStore";
    private const string BackgroundUpdateValueName = "WinProvisionStore.BackgroundUpdate";
    private readonly string _filePath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "WinProvisionStore", "app-preferences.json");

    public AppThemePreference Theme { get; private set; } = AppThemePreference.System;
    public bool LaunchAtStartup { get; private set; }
    public bool AutoUpdateAtStartup { get; private set; }

    public ApplicationPreferencesService()
    {
        try
        {
            if (File.Exists(_filePath))
            {
                var saved = JsonSerializer.Deserialize<Preferences>(File.ReadAllText(_filePath));
                if (saved is not null && Enum.IsDefined(saved.Theme)) Theme = saved.Theme;
            }
        }
        catch
        {
            Theme = AppThemePreference.System;
        }

        LoadStartupOptions();
    }

    private void LoadStartupOptions()
    {
        bool migrateLegacyRegistration = false;
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(StartupRegistryPath);
            string? appCommand = key?.GetValue(StartupValueName) as string;
            string? updateCommand = key?.GetValue(BackgroundUpdateValueName) as string;
            bool legacyBackgroundOnly = HasArgument(appCommand, "--background-update");
            bool legacyCombined = HasArgument(appCommand, "--update-at-startup");

            LaunchAtStartup = !string.IsNullOrWhiteSpace(appCommand) && !legacyBackgroundOnly;
            AutoUpdateAtStartup = !string.IsNullOrWhiteSpace(updateCommand)
                || legacyBackgroundOnly
                || legacyCombined;
            migrateLegacyRegistration = legacyBackgroundOnly || legacyCombined;
        }
        catch { LaunchAtStartup = false; AutoUpdateAtStartup = false; }

        // Versões anteriores combinavam a inicialização do app e do atualizador em um único
        // comando. Se ambas as opções estavam ligadas, isso abria a interface em vez de rodar
        // apenas o processo de atualização. Migra para duas entradas independentes no Run.
        if (migrateLegacyRegistration)
        {
            try { UpdateStartupRegistration(); }
            catch { /* A tela de Atualizações ainda permite corrigir o registro manualmente. */ }
        }
    }

    public void SetTheme(AppThemePreference theme)
    {
        if (!Enum.IsDefined(theme)) theme = AppThemePreference.System;
        Theme = theme;
        Save();
        ApplyTheme(theme);
    }

    public void SetLaunchAtStartup(bool enabled)
    {
        LaunchAtStartup = enabled;
        UpdateStartupRegistration();
    }

    public void SetAutoUpdateAtStartup(bool enabled)
    {
        AutoUpdateAtStartup = enabled;
        UpdateStartupRegistration();
    }

    private void UpdateStartupRegistration()
    {
        using var key = Registry.CurrentUser.CreateSubKey(StartupRegistryPath, writable: true)
            ?? throw new InvalidOperationException("Não foi possível abrir a configuração de inicialização do usuário.");
        string executable = Environment.ProcessPath
            ?? throw new InvalidOperationException("Não foi possível localizar o executável do WinProvision.");

        if (LaunchAtStartup)
            key.SetValue(StartupValueName, $"\"{executable}\"", RegistryValueKind.String);
        else
            key.DeleteValue(StartupValueName, throwOnMissingValue: false);

        if (AutoUpdateAtStartup)
            key.SetValue(BackgroundUpdateValueName, $"\"{executable}\" --background-update", RegistryValueKind.String);
        else
            key.DeleteValue(BackgroundUpdateValueName, throwOnMissingValue: false);
    }

    private static bool HasArgument(string? command, string argument) =>
        command?.Contains(argument, StringComparison.OrdinalIgnoreCase) == true;

    public static void ApplyTheme(AppThemePreference theme)
    {
        ApplicationThemeManager.Apply(theme switch
        {
            AppThemePreference.Light => ApplicationTheme.Light,
            AppThemePreference.Dark => ApplicationTheme.Dark,
            _ => ApplicationTheme.Unknown
        });
    }

    private void Save()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_filePath)!);
        string tempPath = _filePath + ".tmp";
        File.WriteAllText(tempPath, JsonSerializer.Serialize(new Preferences(Theme)));
        File.Move(tempPath, _filePath, overwrite: true);
    }

    private sealed record Preferences(AppThemePreference Theme);
}
