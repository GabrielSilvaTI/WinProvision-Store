using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using WinProvision.Core.Models;

namespace WinProvision.Core.Services;

public class StoreService
{
    private const string DatabaseUrl = "https://raw.githubusercontent.com/GabrielSilvaTI/WinProvision-Store/database/apps.json";
    private readonly string _cacheDirectory;
    private readonly string _cacheFilePath;
    private readonly HttpClient _httpClient;
    private readonly IconService _iconService;

    private readonly JsonSerializerOptions _jsonOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

    private List<AppEntry> _cachedCatalog = [];

    public StoreService(HttpClient? httpClient = null, IconService? iconService = null)
    {
        _httpClient = httpClient ?? new HttpClient { Timeout = TimeSpan.FromSeconds(15) };
        _iconService = iconService ?? new IconService();

        _cacheDirectory = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "WinProvisionStore"
        );

        _cacheFilePath = Path.Combine(_cacheDirectory, "apps.json");
    }

    public async Task<List<AppEntry>> LoadCatalogAsync(bool forceRefresh = false, CancellationToken cancellationToken = default)
    {
        if (!forceRefresh && File.Exists(_cacheFilePath))
        {
            try
            {
                string localJson = await File.ReadAllTextAsync(_cacheFilePath, cancellationToken);
                _cachedCatalog = JsonSerializer.Deserialize<List<AppEntry>>(localJson, _jsonOptions) ?? [];

                await _iconService.EnsureIconsDatabaseLoadedAsync(cancellationToken);
                PopulateIcons(_cachedCatalog);

                _ = RefreshCacheInBackgroundAsync(cancellationToken);

                return _cachedCatalog;
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[StoreService] Cache local corrompido, baixando novamente: {ex.Message}");
            }
        }

        return await FetchAndSaveRemoteCatalogAsync(cancellationToken);
    }

    /// <summary>Catálogo completo já carregado em memória (o que LoadCatalogAsync populou).</summary>
    public IReadOnlyList<AppEntry> GetAll() => _cachedCatalog;

    public IEnumerable<AppEntry> Search(string query)
    {
        if (string.IsNullOrWhiteSpace(query))
            return _cachedCatalog;

        string cleanQuery = query.Replace("-", "").Replace(" ", "").Replace(".", "");

        return _cachedCatalog
            .Select(app => (App: app, Score: ScoreMatch(app, query, cleanQuery)))
            .Where(x => x.Score < int.MaxValue)
            .OrderBy(x => x.Score)
            .ThenBy(x => x.App.Name.Length)
            .ThenBy(x => x.App.Name, StringComparer.OrdinalIgnoreCase)
            .Select(x => x.App);
    }

    private static int ScoreMatch(AppEntry app, string query, string cleanQuery)
    {
        string name = app.Name ?? string.Empty;
        string id = app.Id ?? string.Empty;
        string publisher = app.Publisher ?? string.Empty;
        string cleanName = name.Replace("-", "").Replace(" ", "").Replace(".", "");
        string cleanId = id.Replace("-", "").Replace(".", "");

        if (name.Equals(query, StringComparison.OrdinalIgnoreCase) ||
            id.Equals(query, StringComparison.OrdinalIgnoreCase))
            return 0;

        if (cleanName.Equals(cleanQuery, StringComparison.OrdinalIgnoreCase) ||
            cleanId.Equals(cleanQuery, StringComparison.OrdinalIgnoreCase))
            return 1;

        if (name.StartsWith(query, StringComparison.OrdinalIgnoreCase) ||
            id.StartsWith(query, StringComparison.OrdinalIgnoreCase))
            return 2;

        if (name.Split([' ', '.', '-'], StringSplitOptions.RemoveEmptyEntries)
                .Any(word => word.StartsWith(query, StringComparison.OrdinalIgnoreCase)) ||
            id.Split([' ', '.', '-'], StringSplitOptions.RemoveEmptyEntries)
                .Any(word => word.StartsWith(query, StringComparison.OrdinalIgnoreCase)))
            return 3;

        if (cleanName.StartsWith(cleanQuery, StringComparison.OrdinalIgnoreCase) ||
            cleanId.StartsWith(cleanQuery, StringComparison.OrdinalIgnoreCase))
            return 4;

        if (name.Contains(query, StringComparison.OrdinalIgnoreCase) ||
            id.Contains(query, StringComparison.OrdinalIgnoreCase))
            return 5;

        if (cleanName.Contains(cleanQuery, StringComparison.OrdinalIgnoreCase) ||
            cleanId.Contains(cleanQuery, StringComparison.OrdinalIgnoreCase))
            return 6;

        if (publisher.Contains(query, StringComparison.OrdinalIgnoreCase))
            return 7;

        if (app.Tags.Any(tag => tag.Contains(query, StringComparison.OrdinalIgnoreCase)))
            return 8;

        return int.MaxValue;
    }

    private async Task<List<AppEntry>> FetchAndSaveRemoteCatalogAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            string remoteJson = await _httpClient.GetStringAsync(DatabaseUrl, cancellationToken);
            var catalog = JsonSerializer.Deserialize<List<AppEntry>>(remoteJson, _jsonOptions) ?? [];

            if (catalog.Count > 0)
            {
                _cachedCatalog = catalog;
                await _iconService.EnsureIconsDatabaseLoadedAsync(cancellationToken);
                PopulateIcons(_cachedCatalog);

                Directory.CreateDirectory(_cacheDirectory);
                await File.WriteAllTextAsync(_cacheFilePath, remoteJson, cancellationToken);
            }

            return _cachedCatalog;
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[StoreService] Falha ao baixar catálogo remoto: {ex.Message}");
            return _cachedCatalog;
        }
    }

    private void PopulateIcons(List<AppEntry> catalog)
    {
        foreach (var app in catalog)
        {
            app.IconUrl = _iconService.ResolveIconUrl(app);
        }
    }

    private async Task RefreshCacheInBackgroundAsync(CancellationToken cancellationToken = default)
    {
        await FetchAndSaveRemoteCatalogAsync(cancellationToken);
    }
}
