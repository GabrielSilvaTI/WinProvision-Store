using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Win32;
using WinProvision.Core.Services;

namespace WinProvision.Core.Services;

/// <summary>
/// Fonte única de verdade sobre quais aplicativos clássicos (EXE/MSI) estão instalados.
/// Ignora aplicativos nativos do Windows (UWP/AppX) varrendo diretamente o Registro.
/// </summary>
public class InstalledAppsService
{
    private readonly SemaphoreSlim _loadLock = new(1, 1);

    // Mudança: Deixamos de usar HashSet<string> para guardar a lista detalhada de apps
    private List<InstalledAppDetail> _installedApps = new();
    private bool _loaded;

    public event Action? Changed;

    public async Task EnsureLoadedAsync(CancellationToken cancellationToken = default)
    {
        if (_loaded)
            return;

        await RefreshAsync(cancellationToken);
    }

    public async Task RefreshAsync(CancellationToken cancellationToken = default)
    {
        await _loadLock.WaitAsync(cancellationToken);
        try
        {
            // Rodamos a varredura do registro em uma thread de background para não travar a UI
            _installedApps = await Task.Run(() => GetDesktopAppsFromRegistry(), cancellationToken);
            _loaded = true;
        }
        finally
        {
            _loadLock.Release();
        }

        Changed?.Invoke();
    }

    /// <summary>
    /// Retorna todos os aplicativos (EXE/MSI) detectados para a tela de Desinstalação.
    /// </summary>
    public IReadOnlyList<InstalledAppDetail> GetAllInstalledApps() => _installedApps.AsReadOnly();

    /// <summary>
    /// Compatibilidade para a Store UI: Verifica se um app está instalado pelo Nome.
    /// </summary>
    public bool IsInstalled(string displayName)
    {
        return _installedApps.Any(app =>
            app.DisplayName.Contains(displayName, StringComparison.OrdinalIgnoreCase));
    }

    // Os métodos MarkInstalled e MarkUninstalled continuam disparando o Changed
    public void MarkInstalled() => Changed?.Invoke();
    public void MarkUninstalled() => Changed?.Invoke();

    /// <summary>
    /// Motor de varredura profunda no Registro para capturar apenas EXE e MSI.
    /// </summary>
    private List<InstalledAppDetail> GetDesktopAppsFromRegistry()
    {
        var apps = new List<InstalledAppDetail>();

        // As 3 fontes principais de programas clássicos no Windows
        var registryLocations = new[]
        {
            new { Hive = RegistryHive.LocalMachine, View = RegistryView.Registry64, Path = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall" },
            new { Hive = RegistryHive.LocalMachine, View = RegistryView.Registry32, Path = @"SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall" },
            new { Hive = RegistryHive.CurrentUser, View = RegistryView.Registry64, Path = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall" }
        };

        foreach (var loc in registryLocations)
        {
            using var baseKey = RegistryKey.OpenBaseKey(loc.Hive, loc.View);
            using var uninstallKey = baseKey.OpenSubKey(loc.Path);

            if (uninstallKey == null) continue;

            foreach (var subKeyName in uninstallKey.GetSubKeyNames())
            {
                using var appKey = uninstallKey.OpenSubKey(subKeyName);
                if (appKey == null) continue;

                // 1. Filtro: Se for um componente de sistema do Windows, ignorar
                var systemComponent = appKey.GetValue("SystemComponent") as int?;
                if (systemComponent == 1) continue;

                string? displayName = appKey.GetValue("DisplayName") as string;
                string? uninstallString = appKey.GetValue("UninstallString") as string;
                string? quietUninstallString = appKey.GetValue("QuietUninstallString") as string;
                string? publisher = appKey.GetValue("Publisher") as string;
                string? version = appKey.GetValue("DisplayVersion") as string;

                // 2. Filtro: Só adicionamos se tiver Nome e tiver um executável de Desinstalação (ignora lixo)
                if (!string.IsNullOrWhiteSpace(displayName) && !string.IsNullOrWhiteSpace(uninstallString))
                {
                    // Evita duplicatas se o mesmo app estiver registrado no HKLM e HKCU
                    if (!apps.Any(a => a.DisplayName.Equals(displayName, StringComparison.OrdinalIgnoreCase)))
                    {
                        apps.Add(new InstalledAppDetail
                        {
                            Id = subKeyName,
                            DisplayName = displayName.Trim(),
                            Publisher = publisher?.Trim() ?? "Desconhecido",
                            DisplayVersion = version?.Trim() ?? "",
                            UninstallString = uninstallString.Trim(),
                            QuietUninstallString = quietUninstallString?.Trim() ?? string.Empty
                        });
                    }
                }
            }
        }

        // Retorna a lista ordenada por nome
        return apps.OrderBy(a => a.DisplayName).ToList();
    }
}

// Lembre-se de criar essa classe no seu projeto (ex: na pasta Models)
public class InstalledAppDetail
{
    public string Id { get; set; } = string.Empty;
    public string DisplayName { get; set; } = string.Empty;
    public string Publisher { get; set; } = string.Empty;
    public string DisplayVersion { get; set; } = string.Empty;
    public string UninstallString { get; set; } = string.Empty;
    public string QuietUninstallString { get; set; } = string.Empty;
    public string InstallLocation { get; set; } = string.Empty;
}
