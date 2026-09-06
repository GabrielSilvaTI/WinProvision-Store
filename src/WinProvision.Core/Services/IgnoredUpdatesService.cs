using System.Text.Json;

namespace WinProvision.Core.Services;

public sealed class IgnoredUpdatesService
{
    private readonly string _filePath;
    private Dictionary<string, string> _ignored = new(StringComparer.OrdinalIgnoreCase);

    public IgnoredUpdatesService() : this(DefaultFilePath())
    {
    }

    internal IgnoredUpdatesService(string filePath)
    {
        _filePath = filePath;
        Load();
    }

    private static string DefaultFilePath() => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "WinProvision", "ignored-updates.json");

    private void Load()
    {
        if (!File.Exists(_filePath))
        {
            return;
        }

        try
        {
            string json = File.ReadAllText(_filePath);
            var data = JsonSerializer.Deserialize<Dictionary<string, string>>(json);
            if (data is not null)
            {
                _ignored = new Dictionary<string, string>(data, StringComparer.OrdinalIgnoreCase);
            }
        }
        catch (JsonException)
        {
            // Arquivo corrompido — melhor-esforço, começa do zero sem travar a tela de Atualizações.
        }
    }

    private void Save()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_filePath)!);
            File.WriteAllText(_filePath, JsonSerializer.Serialize(_ignored));
        }
        catch (IOException)
        {
            // Melhor-esforço, mesma filosofia do resto do app
        }
    }

    public void Ignore(string appId, string availableVersion)
    {
        _ignored[appId] = availableVersion;
        Save();
    }

    public void Unignore(string appId)
    {
        if (_ignored.Remove(appId))
        {
            Save();
        }
    }

    public bool IsIgnored(string appId, string availableVersion) =>
        _ignored.TryGetValue(appId, out string? ignoredVersion) &&
        string.Equals(ignoredVersion, availableVersion, StringComparison.OrdinalIgnoreCase);
}