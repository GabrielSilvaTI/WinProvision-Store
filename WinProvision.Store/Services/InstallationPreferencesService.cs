using System;
using System.IO;
using System.Text.Json;
using WinProvision.Core.Models;

namespace WinProvision.Store.Services;

public sealed class InstallationPreferencesService
{
    private readonly string _filePath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "WinProvisionStore", "installation-preferences.json");

    public PackageInstallMethod PreferredMethod { get; private set; } = PackageInstallMethod.Automatic;

    public InstallationPreferencesService()
    {
        try
        {
            if (File.Exists(_filePath))
            {
                var value = JsonSerializer.Deserialize<Preferences>(File.ReadAllText(_filePath));
                if (value is not null && Enum.IsDefined(value.PreferredMethod))
                    PreferredMethod = value.PreferredMethod;
            }
        }
        catch
        {
            PreferredMethod = PackageInstallMethod.Automatic;
        }
    }

    public void SetPreferredMethod(PackageInstallMethod method)
    {
        if (!Enum.IsDefined(method)) method = PackageInstallMethod.Automatic;
        PreferredMethod = method;
        Directory.CreateDirectory(Path.GetDirectoryName(_filePath)!);
        string tempPath = _filePath + ".tmp";
        File.WriteAllText(tempPath, JsonSerializer.Serialize(new Preferences(method)));
        File.Move(tempPath, _filePath, overwrite: true);
    }

    private sealed record Preferences(PackageInstallMethod PreferredMethod);
}
