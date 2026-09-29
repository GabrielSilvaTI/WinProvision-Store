using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using WinProvision.Core.Models;

namespace WinProvision.Core.Services;

public class StoreService
{
    private const string DatabaseUrl = "https://pub-166b41912a994dbe86583ba10596d673.r2.dev/Store/Database/apps.json";
    private readonly string _cacheDirectory;
    private readonly string _cacheFilePath;
    private readonly HttpClient _httpClient;
    private readonly IconService _iconService;

    // Mesmas opções centralizadas usadas pelo resto do app (ver WinProvisionJsonOptions) —
    // é o mesmo apps.json que o WinProvision.Indexer gera via CatalogExporter, então os
    // dois lados (gerador e consumidor) precisam concordar na mesma política de leitura.
    private static readonly JsonSerializerOptions _jsonOptions = WinProvisionJsonOptions.Compact;

    private List<AppEntry> _cachedCatalog = [];
    private SearchDocument[] _searchIndex = [];
    private readonly SemaphoreSlim _loadLock = new(1, 1);
    private readonly object _refreshSync = new();
    private Task<List<AppEntry>>? _refreshTask;
    private CancellationTokenSource? _refreshCts;
    private int _cacheGeneration;

    private static readonly TimeSpan CatalogCacheTtl = TimeSpan.FromHours(6);

    /// <summary>
    /// Disparado sempre que _cachedCatalog é trocado por uma lista nova
    /// (refresh em background ou forçado). Quem mantém uma referência própria
    /// ao catálogo (ex.: HomePage._allApps) deve assinar isso para não ficar
    /// com dados/flags de instâncias antigas de AppEntry.
    /// </summary>
    public event Action<IReadOnlyList<AppEntry>>? CatalogUpdated;
    public event Action? CacheCleared;

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
        await _loadLock.WaitAsync(cancellationToken);
        try
        {
            if (forceRefresh)
                return await RefreshCacheInBackgroundAsync(cancellationToken);

            if (_cachedCatalog.Count > 0)
            {
                bool stale = IsCacheStale();
                if (stale)
                    _ = RefreshCacheInBackgroundAsync(CancellationToken.None);
                return _cachedCatalog;
            }

            if (!forceRefresh && File.Exists(_cacheFilePath))
            {
                try
                {
                    string localJson = await File.ReadAllTextAsync(_cacheFilePath, cancellationToken);
                    SetCachedCatalog(JsonSerializer.Deserialize<List<AppEntry>>(localJson, _jsonOptions) ?? []);
                    await _iconService.EnsureIconsDatabaseLoadedAsync(cancellationToken);
                    PopulateIcons(_cachedCatalog);

                    if (IsCacheStale())
                        _ = RefreshCacheInBackgroundAsync(CancellationToken.None);

                    return _cachedCatalog;
                }
                catch (Exception ex)
                {
                    Debug.WriteLine($"[StoreService] Cache local corrompido, baixando novamente: {ex.Message}");
                }
            }

            return await FetchAndSaveRemoteCatalogAsync(cancellationToken);
        }
        finally
        {
            _loadLock.Release();
        }
    }

    /// <summary>Catálogo completo já carregado em memória (o que LoadCatalogAsync populou).</summary>
    public IReadOnlyList<AppEntry> GetAll() => _cachedCatalog;

    /// <summary>Momento em que o cache local do apps.json foi gravado pela última sincronização.</summary>
    public DateTime? LastCatalogSyncUtc =>
        File.Exists(_cacheFilePath) ? File.GetLastWriteTimeUtc(_cacheFilePath) : null;

    public IEnumerable<AppEntry> Search(string query)
    {
        if (string.IsNullOrWhiteSpace(query))
            return _cachedCatalog;

        string cleanQuery = query.Replace("-", "").Replace(" ", "").Replace(".", "");

        return _searchIndex
            .Select(document => (Document: document, Score: ScoreMatch(document, query, cleanQuery)))
            .Where(x => x.Score < int.MaxValue)
            .OrderBy(x => x.Score)
            .ThenBy(x => x.Document.Name.Length)
            .ThenBy(x => x.Document.Name, StringComparer.OrdinalIgnoreCase)
            .Select(x => x.Document.App);
    }

    private static int ScoreMatch(SearchDocument document, string query, string cleanQuery)
    {
        string name = document.Name;
        string id = document.Id;

        if (name.Equals(query, StringComparison.OrdinalIgnoreCase) ||
            id.Equals(query, StringComparison.OrdinalIgnoreCase))
            return 0;

        if (document.CleanName.Equals(cleanQuery, StringComparison.OrdinalIgnoreCase) ||
            document.CleanId.Equals(cleanQuery, StringComparison.OrdinalIgnoreCase))
            return 1;

        if (name.StartsWith(query, StringComparison.OrdinalIgnoreCase) ||
            id.StartsWith(query, StringComparison.OrdinalIgnoreCase))
            return 2;

        if (document.NameTokens.Any(word => word.StartsWith(query, StringComparison.OrdinalIgnoreCase)) ||
            document.IdTokens.Any(word => word.StartsWith(query, StringComparison.OrdinalIgnoreCase)))
            return 3;

        if (document.CleanName.StartsWith(cleanQuery, StringComparison.OrdinalIgnoreCase) ||
            document.CleanId.StartsWith(cleanQuery, StringComparison.OrdinalIgnoreCase))
            return 4;

        if (name.Contains(query, StringComparison.OrdinalIgnoreCase) ||
            id.Contains(query, StringComparison.OrdinalIgnoreCase))
            return 5;

        if (document.CleanName.Contains(cleanQuery, StringComparison.OrdinalIgnoreCase) ||
            document.CleanId.Contains(cleanQuery, StringComparison.OrdinalIgnoreCase))
            return 6;

        if (document.Publisher.Contains(query, StringComparison.OrdinalIgnoreCase))
            return 7;

        if (document.Tags.Any(tag => tag.Contains(query, StringComparison.OrdinalIgnoreCase)))
            return 8;

        if (document.Description.Contains(query, StringComparison.OrdinalIgnoreCase))
            return 9;

        if (document.Homepage.Contains(query, StringComparison.OrdinalIgnoreCase) ||
            document.PublisherUrl.Contains(query, StringComparison.OrdinalIgnoreCase) ||
            document.PackageUrl.Contains(query, StringComparison.OrdinalIgnoreCase))
            return 10;

        return int.MaxValue;
    }

    private async Task<List<AppEntry>> FetchAndSaveRemoteCatalogAsync(CancellationToken cancellationToken = default)
    {
        int generation = Volatile.Read(ref _cacheGeneration);
        try
        {
            string remoteJson = await _httpClient.GetStringAsync(DatabaseUrl, cancellationToken);
            var catalog = JsonSerializer.Deserialize<List<AppEntry>>(remoteJson, _jsonOptions) ?? [];

            if (catalog.Count > 0)
            {
                // Preserva o flag IsInstalled ao trocar as instâncias de AppEntry.
                // Sem isso, buscas feitas após o refresh em background (que roda
                // fire-and-forget e substitui _cachedCatalog por objetos novos)
                // voltam sempre com IsInstalled = false, mesmo já sincronizado antes.
                var installedIds = _cachedCatalog
                    .Where(a => a.IsInstalled)
                    .Select(a => a.Id)
                    .ToHashSet(StringComparer.OrdinalIgnoreCase);

                foreach (var app in catalog)
                {
                    app.IsInstalled = installedIds.Contains(app.Id);
                }

                // Se o usuário limpou o cache enquanto a rede estava em andamento,
                // descarta o resultado antigo para não recriar o cache logo após a limpeza.
                if (generation != Volatile.Read(ref _cacheGeneration))
                    return _cachedCatalog;

                SetCachedCatalog(catalog);
                await _iconService.EnsureIconsDatabaseLoadedAsync(cancellationToken);
                PopulateIcons(_cachedCatalog);

                Directory.CreateDirectory(_cacheDirectory);
                await File.WriteAllTextAsync(_cacheFilePath, remoteJson, cancellationToken);

                CatalogUpdated?.Invoke(_cachedCatalog);
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

    public void ClearCache()
    {
        Interlocked.Increment(ref _cacheGeneration);
        SetCachedCatalog([]);

        lock (_refreshSync)
        {
            try { _refreshCts?.Cancel(); } catch { }
            _refreshCts?.Dispose();
            _refreshCts = null;
            _refreshTask = null;
        }

        try
        {
            if (File.Exists(_cacheFilePath))
                File.Delete(_cacheFilePath);
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[StoreService] Não foi possível remover o cache do catálogo: {ex.Message}");
        }

        CacheCleared?.Invoke();
    }

    private void SetCachedCatalog(List<AppEntry> catalog)
    {
        _cachedCatalog = catalog;
        _searchIndex = catalog.Select(app =>
        {
            string name = app.Name ?? string.Empty;
            string id = app.Id ?? string.Empty;
            return new SearchDocument(
                app,
                name,
                id,
                app.Publisher ?? string.Empty,
                CleanSearchText(name, removeSpace: true, removeDot: true),
                CleanSearchText(id, removeSpace: false, removeDot: true),
                name.Split([' ', '.', '-'], StringSplitOptions.RemoveEmptyEntries),
                id.Split([' ', '.', '-'], StringSplitOptions.RemoveEmptyEntries),
                app.Tags?.ToArray() ?? [],
                app.Description ?? string.Empty,
                app.Homepage ?? string.Empty,
                app.PublisherUrl ?? string.Empty,
                app.PackageUrl ?? string.Empty);
        }).ToArray();
    }

    private static string CleanSearchText(string value, bool removeSpace, bool removeDot)
    {
        string clean = value.Replace("-", "");
        if (removeSpace) clean = clean.Replace(" ", "");
        if (removeDot) clean = clean.Replace(".", "");
        return clean;
    }

    private sealed record SearchDocument(
        AppEntry App,
        string Name,
        string Id,
        string Publisher,
        string CleanName,
        string CleanId,
        string[] NameTokens,
        string[] IdTokens,
        string[] Tags,
        string Description,
        string Homepage,
        string PublisherUrl,
        string PackageUrl);

    private bool IsCacheStale()
    {
        try
        {
            return !File.Exists(_cacheFilePath) || DateTime.UtcNow - File.GetLastWriteTimeUtc(_cacheFilePath) >= CatalogCacheTtl;
        }
        catch
        {
            return true;
        }
    }

    private Task<List<AppEntry>> RefreshCacheInBackgroundAsync(CancellationToken cancellationToken = default)
    {
        lock (_refreshSync)
        {
            if (_refreshTask is { IsCompleted: false })
                return _refreshTask;

            _refreshCts?.Dispose();
            _refreshCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            CancellationToken token = _refreshCts.Token;
            Task<List<AppEntry>> task = FetchAndSaveRemoteCatalogAsync(token);
            _refreshTask = task;

            _ = task.ContinueWith(_ =>
            {
                lock (_refreshSync)
                {
                    if (ReferenceEquals(_refreshTask, task))
                    {
                        _refreshTask = null;
                        _refreshCts?.Dispose();
                        _refreshCts = null;
                    }
                }
            }, CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);

            return task;
        }
    }
}
