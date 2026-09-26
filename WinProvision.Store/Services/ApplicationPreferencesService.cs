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
    private readonly string _filePath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "WinProvisionStore", "app-preferences.json");

    public AppThemePreference Theme { get; private set; } = AppThemePreference.System;

    public ApplicationPreferencesService()
    {
        try
        {
            if (!File.Exists(_filePath)) return;
            var saved = JsonSerializer.Deserialize<Preferences>(File.ReadAllText(_filePath));
            if (saved is not null && Enum.IsDefined(saved.Theme)) Theme = saved.Theme;
        }
        catch
        {
            Theme = AppThemePreference.System;
        }
    }

    public bool LaunchAtStartup
    {
        get
        {
            try
            {
                using var key = Registry.CurrentUser.OpenSubKey(StartupRegistryPath);
                return key?.GetValue(StartupValueName) is string value
                    && !string.IsNullOrWhiteSpace(value);
            }
            catch
            {
                return false;
            }
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
        using var key = Registry.CurrentUser.CreateSubKey(StartupRegistryPath, writable: true)
            ?? throw new InvalidOperationException("Não foi possível abrir a configuração de inicialização do usuário.");
        if (enabled)
        {
            string executable = Environment.ProcessPath
                ?? throw new InvalidOperationException("Não foi possível localizar o executável do WinProvision.");
            key.SetValue(StartupValueName, $"\"{executable}\"", RegistryValueKind.String);
        }
        else
        {
            key.DeleteValue(StartupValueName, throwOnMissingValue: false);
        }
    }

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