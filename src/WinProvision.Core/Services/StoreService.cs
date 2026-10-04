using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Threading;
using System.Collections.Concurrent;
using WinProvision.Core.Models;

namespace WinProvision.Core.Services;

public class StoreService
{
    private const string CatalogV2BaseUrl = "https://pub-166b41912a994dbe86583ba10596d673.r2.dev/Store/Catalog";
    private const string CatalogManifestUrl = CatalogV2BaseUrl + "/manifest.json";
    private const string MsStoreCatalogBaseUrl = CatalogV2BaseUrl + "/msstore";
    private const string MsStoreManifestUrl = MsStoreCatalogBaseUrl + "/manifest.json";
    private const string LegacyScreenshotBasePath = "/Store/Screenshot_Database/";
    private readonly string _cacheDirectory;
    private readonly string _legacyCacheFilePath;
    private readonly string _catalogManifestCacheFilePath;
    private readonly string _catalogManifestEtagPath;
    private readonly string _catalogManifestLastModifiedPath;
    private readonly string _catalogDetailsCacheDirectory;
    private readonly string _screenshotIndexCacheFilePath;
    private readonly string _screenshotIndexEtagPath;
    private readonly string _screenshotIndexLastModifiedPath;
    private readonly HttpClient _httpClient;
    private readonly IconService _iconService;

    // Opções centralizadas compartilhadas pelo app; o Python emite JSON UTF-8 compacto
    // compatível com o mesmo modelo AppEntry usado pelo Indexer e pelo cliente.
    private static readonly JsonSerializerOptions _jsonOptions = WinProvisionJsonOptions.Compact;

    private List<AppEntry> _cachedCatalog = [];
    private SearchDocument[] _searchIndex = [];
    private readonly SemaphoreSlim _loadLock = new(1, 1);
    private readonly object _refreshSync = new();
    private Task<List<AppEntry>>? _refreshTask;
    private CancellationTokenSource? _refreshCts;
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _detailLocks = new(StringComparer.OrdinalIgnoreCase);
    private string? _loadedSearchIndexCacheFilePath;
    private string? _catalogSha256;
    private int _cacheGeneration;
    private bool _startupRevalidationScheduled;

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

        _legacyCacheFilePath = Path.Combine(_cacheDirectory, "apps.json");
        _catalogManifestCacheFilePath = Path.Combine(_cacheDirectory, "catalog-v2-manifest.json");
        _catalogManifestEtagPath = _catalogManifestCacheFilePath + ".etag";
        _catalogManifestLastModifiedPath = _catalogManifestCacheFilePath + ".lastmodified";
        _catalogDetailsCacheDirectory = Path.Combine(_cacheDirectory, "catalog-v2-details");
        _screenshotIndexCacheFilePath = Path.Combine(_cacheDirectory, "screenshot-index.json");
        _screenshotIndexEtagPath = _screenshotIndexCacheFilePath + ".etag";
        _screenshotIndexLastModifiedPath = _screenshotIndexCacheFilePath + ".lastmodified";
    }

    public async Task<List<AppEntry>> LoadCatalogAsync(bool forceRefresh = false, CancellationToken cancellationToken = default)
    {
        await _loadLock.WaitAsync(cancellationToken);
        try
        {
            if (forceRefresh)
            {
                _startupRevalidationScheduled = true;
                return await RefreshCacheInBackgroundAsync(cancellationToken, forceRefresh: true);
            }

            if (_cachedCatalog.Count > 0)
            {
                if (!_startupRevalidationScheduled)
                {
                    _startupRevalidationScheduled = true;
                    _ = RefreshCacheInBackgroundAsync(CancellationToken.None, revalidateRemote: true);
                }
                else if (IsCacheStale())
                {
                    _ = RefreshCacheInBackgroundAsync(CancellationToken.None);
                }
                return _cachedCatalog;
            }

            if (!forceRefresh && TryLoadLocalCatalog())
            {
                RemoveLegacyScreenshotLinks(_cachedCatalog);
                PopulateIcons(_cachedCatalog);

                _startupRevalidationScheduled = true;
                _ = RefreshCacheInBackgroundAsync(CancellationToken.None, revalidateRemote: true);

                return _cachedCatalog;
            }

            _startupRevalidationScheduled = true;
            return await FetchAndSaveRemoteCatalogAsync(cancellationToken);
        }
        finally
        {
            _loadLock.Release();
        }
    }

    /// <summary>Catálogo completo já carregado em memória (o que LoadCatalogAsync populou).</summary>
    public IReadOnlyList<AppEntry> GetAll() => _cachedCatalog;

    /// <summary>Carrega o registro completo do app selecionado sem baixar o catálogo completo.</summary>
    public async Task<AppEntry> LoadDetailsAsync(AppEntry app, CancellationToken cancellationToken = default)
    {
        int cacheGeneration = Volatile.Read(ref _cacheGeneration);
        if (string.IsNullOrWhiteSpace(app.CatalogDetailPath))
        {
            AppEntry? catalogEntry = _cachedCatalog.FirstOrDefault(entry =>
                string.Equals(entry.Id, app.Id, StringComparison.OrdinalIgnoreCase));
            if (catalogEntry is not null && !ReferenceEquals(catalogEntry, app))
                return await LoadDetailsAsync(catalogEntry, cancellationToken);
        }
        string? catalogHash = app.CatalogDetailCatalogSha256 ?? _catalogSha256;
        if (string.IsNullOrWhiteSpace(app.CatalogDetailPath) || string.IsNullOrWhiteSpace(catalogHash))
            return app;

        string relativePath = app.CatalogDetailPath.Replace('\\', '/');
        if (!IsSafeDetailPath(relativePath))
            return app;

        SemaphoreSlim detailLock = _detailLocks.GetOrAdd(app.Id, _ => new SemaphoreSlim(1, 1));
        await detailLock.WaitAsync(cancellationToken);
        try
        {
            string detailRoot = Path.Combine(_catalogDetailsCacheDirectory, catalogHash);
            string detailFile = Path.GetFullPath(Path.Combine(detailRoot, relativePath.Replace('/', Path.DirectorySeparatorChar)));
            string normalizedRoot = Path.GetFullPath(detailRoot) + Path.DirectorySeparatorChar;
            if (!detailFile.StartsWith(normalizedRoot, StringComparison.OrdinalIgnoreCase))
                return app;

            string? json = null;
            string? expectedDetailHash = app.CatalogDetailSha256;
            if (File.Exists(detailFile))
            {
                string? cachedJson = null;
                try
                {
                    cachedJson = await File.ReadAllTextAsync(detailFile, cancellationToken);
                }
                catch (IOException) { }
                if (cachedJson is not null && HasExpectedHash(cachedJson, expectedDetailHash))
                    json = cachedJson;
            }

            if (cacheGeneration != Volatile.Read(ref _cacheGeneration))
                return app;

            if (json is null)
            {
                string detailBaseUrl = app.CatalogDetailBaseUrl
                    ?? (string.Equals(app.Source, "msstore", StringComparison.OrdinalIgnoreCase) ? MsStoreCatalogBaseUrl : CatalogV2BaseUrl);
                string url = $"{detailBaseUrl}/{relativePath}";
                using var response = await _httpClient.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
                response.EnsureSuccessStatusCode();
                json = await response.Content.ReadAsStringAsync(cancellationToken);
                if (!HasExpectedHash(json, expectedDetailHash))
                    throw new JsonException($"O hash do detalhe de {app.Id} não corresponde ao índice de busca.");
                AppEntry? downloaded = JsonSerializer.Deserialize<AppEntry>(json, _jsonOptions);
                if (downloaded is null || !string.Equals(downloaded.Id, app.Id, StringComparison.OrdinalIgnoreCase))
                    return app;
                if (cacheGeneration != Volatile.Read(ref _cacheGeneration))
                    return app;

                Directory.CreateDirectory(Path.GetDirectoryName(detailFile)!);
                string temporaryPath = detailFile + ".tmp";
                await File.WriteAllTextAsync(temporaryPath, json, cancellationToken);
                File.Move(temporaryPath, detailFile, overwrite: true);
                return CompleteDetails(downloaded, app);
            }

            AppEntry? cached = JsonSerializer.Deserialize<AppEntry>(json, _jsonOptions);
            return cacheGeneration == Volatile.Read(ref _cacheGeneration)
                   && cached is not null && string.Equals(cached.Id, app.Id, StringComparison.OrdinalIgnoreCase)
                ? CompleteDetails(cached, app)
                : app;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[StoreService] Não foi possível carregar detalhes de {app.Id}: {ex.Message}");
            return app;
        }
        finally
        {
            detailLock.Release();
        }
    }

    private AppEntry CompleteDetails(AppEntry details, AppEntry summary)
    {
        details.IsInstalled = summary.IsInstalled;
        details.IsInstalling = summary.IsInstalling;
        details.IsSelectedForInstall = summary.IsSelectedForInstall;
        details.CatalogDetailPath = summary.CatalogDetailPath;
        details.CatalogDetailSha256 = summary.CatalogDetailSha256;
        details.CatalogDetailBaseUrl = summary.CatalogDetailBaseUrl;
        details.CatalogDetailCatalogSha256 = summary.CatalogDetailCatalogSha256;
        details.Media = summary.Media;
        details.IconUrl = _iconService.ResolveIconUrl(details);
        details.StoreBannerUrl = _iconService.ResolveBannerUrl(details);
        if (string.Equals(details.Source, "msstore", StringComparison.OrdinalIgnoreCase))
            details.StoreScreenshotUrls = _iconService.ResolveScreenshotUrls(details);
        RemoveLegacyScreenshotLinks([details]);
        return details;
    }

    private static bool IsSafeDetailPath(string path) =>
        path.StartsWith("apps/", StringComparison.Ordinal)
        && !Path.IsPathRooted(path)
        && !path.Split('/').Any(segment => segment is "" or "." or "..");

    /// <summary>Momento em que o manifesto do catálogo v2 foi gravado localmente.</summary>
    public DateTime? LastCatalogSyncUtc =>
        File.Exists(_catalogManifestCacheFilePath) ? File.GetLastWriteTimeUtc(_catalogManifestCacheFilePath) : null;

    private bool TryLoadLocalCatalog()
    {
        try
        {
            if (!File.Exists(_catalogManifestCacheFilePath))
                return TryLoadLegacyCatalog();

            var manifest = JsonSerializer.Deserialize<CatalogManifest>(
                File.ReadAllText(_catalogManifestCacheFilePath), _jsonOptions);
            if (!IsValidManifest(manifest))
                throw new JsonException("Manifesto local do catálogo v2 inválido.");

            string indexPath = GetSearchIndexCachePath(manifest!.CatalogSha256!);
            if (!File.Exists(indexPath))
                return TryLoadLegacyCatalog();

            string indexJson = File.ReadAllText(indexPath);
            if (!HasExpectedHash(indexJson, manifest.IndexSha256))
                throw new JsonException("O índice local não corresponde ao hash do manifesto.");

            var catalog = JsonSerializer.Deserialize<List<AppEntry>>(indexJson, _jsonOptions);
            if (catalog is null || catalog.Count != manifest.AppCount || catalog.Count == 0 || !ValidateSearchEntries(catalog))
                throw new JsonException("Índice local do catálogo v2 incompleto ou inválido.");

            SetCatalogRoute(catalog, CatalogV2BaseUrl, manifest.CatalogSha256);
            catalog = MergeMsStoreCatalog(catalog, TryLoadLocalMsStoreCatalog());

            _catalogSha256 = manifest.CatalogSha256;
            _loadedSearchIndexCacheFilePath = indexPath;
            SetCachedCatalog(catalog);
            return true;
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[StoreService] Cache local do catálogo v2 inválido: {ex.Message}");
            return TryLoadLegacyCatalog();
        }
    }

    private bool TryLoadLegacyCatalog()
    {
        try
        {
            if (!File.Exists(_legacyCacheFilePath))
                return false;
            var catalog = JsonSerializer.Deserialize<List<AppEntry>>(File.ReadAllText(_legacyCacheFilePath), _jsonOptions);
            if (catalog is null || catalog.Count == 0)
                return false;
            _catalogSha256 = null;
            _loadedSearchIndexCacheFilePath = _legacyCacheFilePath;
            SetCachedCatalog(catalog);
            return true;
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[StoreService] Cache legado do catálogo inválido: {ex.Message}");
            return false;
        }
    }

    public IEnumerable<AppEntry> Search(string query, int maxResults = int.MaxValue)
    {
        if (string.IsNullOrWhiteSpace(query))
            return _cachedCatalog;

        if (maxResults <= 0)
            return [];

        string cleanQuery = query.Replace("-", "").Replace(" ", "").Replace(".", "");

        if (maxResults == int.MaxValue)
        {
            return _searchIndex
                .Select(document => (Document: document, Score: ScoreMatch(document, query, cleanQuery)))
                .Where(x => x.Score < int.MaxValue)
                .OrderBy(x => x.Score)
                .ThenBy(x => x.Document.Name.Length)
                .ThenBy(x => x.Document.Name, StringComparer.OrdinalIgnoreCase)
                .Select(x => x.Document.App);
        }

        // A tela principal só precisa de candidatos suficientes para combinar
        // resultados locais e ao vivo. Mantém os melhores N durante a varredura,
        // evitando ordenar milhares de correspondências a cada busca.
        var worstFirstComparer = Comparer<(int Score, int NameLength, string Name, int Index)>.Create((left, right) =>
        {
            int comparison = right.Score.CompareTo(left.Score);
            if (comparison != 0) return comparison;
            comparison = right.NameLength.CompareTo(left.NameLength);
            if (comparison != 0) return comparison;
            comparison = StringComparer.OrdinalIgnoreCase.Compare(right.Name, left.Name);
            return comparison != 0 ? comparison : right.Index.CompareTo(left.Index);
        });
        var bestMatches = new PriorityQueue<SearchDocument, (int Score, int NameLength, string Name, int Index)>(worstFirstComparer);

        for (int index = 0; index < _searchIndex.Length; index++)
        {
            SearchDocument document = _searchIndex[index];
            int score = ScoreMatch(document, query, cleanQuery);
            if (score == int.MaxValue)
                continue;

            var priority = (score, document.Name.Length, document.Name, index);
            bestMatches.Enqueue(document, priority);
            if (bestMatches.Count > maxResults)
                bestMatches.Dequeue();
        }

        return bestMatches.UnorderedItems
            .OrderBy(item => item.Priority.Score)
            .ThenBy(item => item.Priority.NameLength)
            .ThenBy(item => item.Priority.Name, StringComparer.OrdinalIgnoreCase)
            .ThenBy(item => item.Priority.Index)
            .Select(item => item.Element.App)
            .ToArray();
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

        if (HasTokenPrefix(document.Name, query) || HasTokenPrefix(document.Id, query))
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

        if (document.App.Tags.Any(tag => tag.Contains(query, StringComparison.OrdinalIgnoreCase)))
            return 8;

        if (document.Description.Contains(query, StringComparison.OrdinalIgnoreCase))
            return 9;

        if (document.Homepage.Contains(query, StringComparison.OrdinalIgnoreCase) ||
            document.PublisherUrl.Contains(query, StringComparison.OrdinalIgnoreCase) ||
            document.PackageUrl.Contains(query, StringComparison.OrdinalIgnoreCase))
            return 10;

        return int.MaxValue;
    }

    private static bool HasTokenPrefix(string text, string query)
    {
        ReadOnlySpan<char> value = text.AsSpan();
        int tokenStart = 0;
        for (int index = 0; index <= value.Length; index++)
        {
            if (index < value.Length && value[index] is not (' ' or '.' or '-'))
                continue;

            if (value[tokenStart..index].StartsWith(query, StringComparison.OrdinalIgnoreCase))
                return true;
            tokenStart = index + 1;
        }

        return false;
    }

    private async Task<List<AppEntry>> FetchAndSaveRemoteCatalogAsync(
        CancellationToken cancellationToken = default,
        bool forceRefresh = false,
        bool revalidateRemote = false)
    {
        int generation = Volatile.Read(ref _cacheGeneration);
        try
        {
            Task<RemoteTextResult> manifestDownload = FetchTextWithCacheAsync(
                CatalogManifestUrl, _catalogManifestCacheFilePath, _catalogManifestEtagPath,
                _catalogManifestLastModifiedPath, forceRefresh || revalidateRemote, cancellationToken);
            RemoteTextResult manifestResult = await manifestDownload;
            if (manifestResult.Content is null)
                return _cachedCatalog;

            CatalogManifest? manifest = JsonSerializer.Deserialize<CatalogManifest>(manifestResult.Content, _jsonOptions);
            if (!IsValidManifest(manifest))
                throw new JsonException("Manifesto remoto do catálogo v2 inválido.");

            Task<List<AppEntry>> msStoreCatalogTask = FetchMsStoreCatalogAsync(forceRefresh, cancellationToken);
            string indexPath = GetSearchIndexCachePath(manifest!.CatalogSha256!);
            string indexUrl = $"{CatalogV2BaseUrl}/manifest/search-index.json";
            RemoteTextResult indexResult = await FetchTextWithCacheAsync(
                indexUrl, indexPath, indexPath + ".etag", indexPath + ".lastmodified", forceRefresh, cancellationToken);
            if (indexResult.Content is null)
                return _cachedCatalog;
            if (!string.IsNullOrWhiteSpace(manifest.IndexSha256)
                && !string.Equals(
                    Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(indexResult.Content))).ToLowerInvariant(),
                    manifest.IndexSha256,
                    StringComparison.Ordinal))
                throw new JsonException("O hash do índice de busca não corresponde ao manifesto.");

            var catalog = JsonSerializer.Deserialize<List<AppEntry>>(indexResult.Content, _jsonOptions) ?? [];
            if (catalog.Count == 0 || catalog.Count != manifest.AppCount || !ValidateSearchEntries(catalog))
                throw new JsonException("Índice remoto do catálogo v2 inválido ou incompleto.");

            SetCatalogRoute(catalog, CatalogV2BaseUrl, manifest.CatalogSha256);
            var msStoreCatalog = await msStoreCatalogTask;
            catalog = MergeMsStoreCatalog(catalog, msStoreCatalog);

            RemoveLegacyScreenshotLinks(catalog);

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
                _catalogSha256 = manifest.CatalogSha256;
                _loadedSearchIndexCacheFilePath = indexPath;
                PopulateIcons(_cachedCatalog);

                Directory.CreateDirectory(_cacheDirectory);
                await CommitRemoteTextAsync(_catalogManifestCacheFilePath, manifestResult, cancellationToken);
                await CommitRemoteTextAsync(indexPath, indexResult, cancellationToken);
                PruneSearchIndexCache(indexPath);
                PruneMsStoreIndexCache(msStoreCatalog);
                CatalogUpdated?.Invoke(_cachedCatalog);
            }

            return _cachedCatalog;
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[StoreService] Falha ao baixar catálogo remoto: {ex.Message}");
            if (_cachedCatalog.Count == 0 && TryLoadLegacyCatalog())
            {
                RemoveLegacyScreenshotLinks(_cachedCatalog);
                PopulateIcons(_cachedCatalog);
            }
            return _cachedCatalog;
        }
    }

    private void PopulateIcons(List<AppEntry> catalog)
    {
        foreach (var app in catalog)
        {
            app.IconUrl = _iconService.ResolveIconUrl(app);
            app.StoreBannerUrl = _iconService.ResolveBannerUrl(app);
            if (string.Equals(app.Source, "msstore", StringComparison.OrdinalIgnoreCase))
                app.StoreScreenshotUrls = _iconService.ResolveScreenshotUrls(app);
        }
    }

    public void ClearCache()
    {
        Interlocked.Increment(ref _cacheGeneration);
        SetCachedCatalog([]);
        _catalogSha256 = null;
        _loadedSearchIndexCacheFilePath = null;

        lock (_refreshSync)
        {
            try { _refreshCts?.Cancel(); } catch { }
            _refreshCts?.Dispose();
            _refreshCts = null;
            _refreshTask = null;
        }

        try
        {
            foreach (string path in Directory.Exists(_cacheDirectory)
                         ? Directory.EnumerateFiles(_cacheDirectory, "catalog-v2-search-*.json").ToArray()
                         : Array.Empty<string>())
                File.Delete(path);
            foreach (string path in Directory.Exists(_cacheDirectory)
                         ? Directory.EnumerateFiles(_cacheDirectory, "catalog-v2-search-*.json.*").ToArray()
                         : Array.Empty<string>())
                File.Delete(path);
            if (File.Exists(_legacyCacheFilePath))
                File.Delete(_legacyCacheFilePath);
            if (File.Exists(_catalogManifestCacheFilePath))
                File.Delete(_catalogManifestCacheFilePath);
            if (File.Exists(_screenshotIndexCacheFilePath))
                File.Delete(_screenshotIndexCacheFilePath);
            foreach (string path in Directory.Exists(_cacheDirectory)
                         ? Directory.EnumerateFiles(_cacheDirectory, "msstore-catalog-*.json*").ToArray()
                         : Array.Empty<string>())
                File.Delete(path);
            if (Directory.Exists(_catalogDetailsCacheDirectory))
                Directory.Delete(_catalogDetailsCacheDirectory, recursive: true);
            foreach (string metadataPath in new[]
                     {
                         _catalogManifestEtagPath, _catalogManifestLastModifiedPath,
                         _legacyCacheFilePath + ".etag", _legacyCacheFilePath + ".lastmodified",
                         _screenshotIndexEtagPath, _screenshotIndexLastModifiedPath
                     })
            {
                if (File.Exists(metadataPath))
                    File.Delete(metadataPath);
            }
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
                app.Description ?? string.Empty,
                app.Homepage ?? string.Empty,
                app.PublisherUrl ?? string.Empty,
                app.PackageUrl ?? string.Empty);
        }).ToArray();
    }

    private string GetSearchIndexCachePath(string catalogSha256) =>
        Path.Combine(_cacheDirectory, $"catalog-v2-search-{catalogSha256}.json");

    private void PruneSearchIndexCache(string currentPath)
    {
        PruneVersionedCacheFiles("catalog-v2-search-*.json*", currentPath);
    }

    private void PruneMsStoreIndexCache(IReadOnlyList<AppEntry> currentCatalog)
    {
        string? catalogHash = currentCatalog.FirstOrDefault()?.CatalogDetailCatalogSha256;
        string? currentPath = catalogHash is null
            ? null
            : Path.Combine(_cacheDirectory, $"msstore-catalog-{catalogHash}.json");
        PruneVersionedCacheFiles("msstore-catalog-*.json*", currentPath);
    }

    private void PruneVersionedCacheFiles(string pattern, string? currentPath)
    {
        try
        {
            if (!Directory.Exists(_cacheDirectory))
                return;

            string? fullCurrentPath = currentPath is null ? null : Path.GetFullPath(currentPath);
            foreach (string path in Directory.EnumerateFiles(_cacheDirectory, pattern))
            {
                if (fullCurrentPath is not null)
                {
                    string fullPath = Path.GetFullPath(path);
                    if (string.Equals(fullPath, fullCurrentPath, StringComparison.OrdinalIgnoreCase)
                        || string.Equals(fullPath, fullCurrentPath + ".etag", StringComparison.OrdinalIgnoreCase)
                        || string.Equals(fullPath, fullCurrentPath + ".lastmodified", StringComparison.OrdinalIgnoreCase))
                        continue;
                }
                File.Delete(path);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Debug.WriteLine($"[StoreService] Não foi possível remover índices de catálogo antigos: {ex.Message}");
        }
    }

    private static bool IsValidManifest(CatalogManifest? manifest) =>
        manifest is { SchemaVersion: 2, AppCount: > 0 }
        && manifest.CatalogSha256 is { Length: 64 } hash
        && hash.All(character => character is >= '0' and <= '9' or >= 'a' and <= 'f')
        && manifest.IndexSha256 is { Length: 64 } indexHash
        && indexHash.All(character => character is >= '0' and <= '9' or >= 'a' and <= 'f');

    private static bool HasExpectedHash(string json, string? expectedHash)
    {
        if (string.IsNullOrWhiteSpace(expectedHash))
            return false;
        string actualHash = Convert.ToHexString(
            System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(json)))
            .ToLowerInvariant();
        return string.Equals(actualHash, expectedHash, StringComparison.Ordinal);
    }

    private static bool ValidateSearchEntries(List<AppEntry> catalog)
    {
        var ids = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (AppEntry app in catalog)
        {
            if (string.IsNullOrWhiteSpace(app.Id)
                || string.IsNullOrWhiteSpace(app.Name)
                || !ids.Add(app.Id)
                || !IsSafeDetailPath(app.CatalogDetailPath ?? string.Empty))
                return false;
        }
        return true;
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
        string Description,
        string Homepage,
        string PublisherUrl,
        string PackageUrl);

    private async Task<RemoteTextResult> FetchTextWithCacheAsync(
        string url,
        string cachePath,
        string etagPath,
        string lastModifiedPath,
        bool forceRefresh,
        CancellationToken cancellationToken)
    {
        string? cachedContent = null;
        string? cachedEtag = null;
        string? cachedLastModified = null;
        try
        {
            if (File.Exists(cachePath))
                cachedContent = await File.ReadAllTextAsync(cachePath, cancellationToken);
            if (File.Exists(etagPath))
                cachedEtag = await File.ReadAllTextAsync(etagPath, cancellationToken);
            if (File.Exists(lastModifiedPath))
                cachedLastModified = await File.ReadAllTextAsync(lastModifiedPath, cancellationToken);

            if (!forceRefresh && cachedContent is not null && !IsFileStale(cachePath))
                return new RemoteTextResult(cachedContent, cachedEtag, cachedLastModified, ShouldCommit: false);

            using var request = new HttpRequestMessage(HttpMethod.Get, url);
            if (!string.IsNullOrWhiteSpace(cachedEtag) && EntityTagHeaderValue.TryParse(cachedEtag, out var entityTag))
                request.Headers.IfNoneMatch.Add(entityTag);
            if (!string.IsNullOrWhiteSpace(cachedLastModified) &&
                DateTimeOffset.TryParse(cachedLastModified, out DateTimeOffset modifiedSince))
                request.Headers.IfModifiedSince = modifiedSince;

            using HttpResponseMessage response = await _httpClient.SendAsync(
                request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
            if (response.StatusCode == System.Net.HttpStatusCode.NotModified && cachedContent is not null)
            {
                return new RemoteTextResult(
                    cachedContent,
                    response.Headers.ETag?.ToString() ?? cachedEtag,
                    response.Content.Headers.LastModified?.ToString("R") ?? cachedLastModified,
                    ShouldCommit: true,
                    NotModified: true);
            }

            response.EnsureSuccessStatusCode();
            string content = await response.Content.ReadAsStringAsync(cancellationToken);
            return new RemoteTextResult(
                content,
                response.Headers.ETag?.ToString(),
                response.Content.Headers.LastModified?.ToString("R"),
                ShouldCommit: true);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[StoreService] Recurso remoto indisponível; usando cache local ({Path.GetFileName(cachePath)}): {ex.Message}");
            return new RemoteTextResult(cachedContent, cachedEtag, cachedLastModified, ShouldCommit: false);
        }
    }

    private async Task CommitRemoteTextAsync(string cachePath, RemoteTextResult result, CancellationToken cancellationToken)
    {
        if (!result.ShouldCommit)
            return;

        if (result.NotModified)
        {
            if (File.Exists(cachePath))
                File.SetLastWriteTimeUtc(cachePath, DateTime.UtcNow);
        }
        else if (result.Content is not null)
        {
            string temporaryPath = cachePath + ".tmp";
            await File.WriteAllTextAsync(temporaryPath, result.Content, cancellationToken);
            File.Move(temporaryPath, cachePath, overwrite: true);
        }

        await WriteMetadataAsync(cachePath + ".etag", result.ETag, cancellationToken);
        await WriteMetadataAsync(cachePath + ".lastmodified", result.LastModified, cancellationToken);
    }

    private static async Task WriteMetadataAsync(string path, string? value, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            try { if (File.Exists(path)) File.Delete(path); } catch { }
            return;
        }

        string temporaryPath = path + ".tmp";
        await File.WriteAllTextAsync(temporaryPath, value, cancellationToken);
        File.Move(temporaryPath, path, overwrite: true);
    }

    private static bool IsFileStale(string path)
    {
        try
        {
            return !File.Exists(path) || DateTime.UtcNow - File.GetLastWriteTimeUtc(path) >= CatalogCacheTtl;
        }
        catch
        {
            return true;
        }
    }

    private sealed record RemoteTextResult(string? Content, string? ETag, string? LastModified, bool ShouldCommit, bool NotModified = false);

    private sealed class CatalogManifest
    {
        public int SchemaVersion { get; set; }
        public int AppCount { get; set; }
        public string? CatalogSha256 { get; set; }
        public string? IndexSha256 { get; set; }
    }

    private List<AppEntry> TryLoadLocalMsStoreCatalog()
    {
        try
        {
            string? indexPath = Directory.Exists(_cacheDirectory)
                ? Directory.EnumerateFiles(_cacheDirectory, "msstore-catalog-*.json")
                    .OrderByDescending(File.GetLastWriteTimeUtc)
                    .FirstOrDefault()
                : null;
            if (indexPath is null)
                return [];
            string hash = Path.GetFileNameWithoutExtension(indexPath).Replace("msstore-catalog-", "", StringComparison.Ordinal);
            var apps = JsonSerializer.Deserialize<List<AppEntry>>(File.ReadAllText(indexPath), _jsonOptions) ?? [];
            if (apps.Count == 0 || !ValidateSearchEntries(apps))
                return [];
            SetCatalogRoute(apps, MsStoreCatalogBaseUrl, hash);
            return apps;
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[StoreService] Cache MS Store local inválido: {ex.Message}");
            return [];
        }
    }

    private async Task<List<AppEntry>> FetchMsStoreCatalogAsync(bool forceRefresh, CancellationToken cancellationToken)
    {
        try
        {
            using var manifestResponse = await _httpClient.GetAsync(MsStoreManifestUrl, cancellationToken);
            manifestResponse.EnsureSuccessStatusCode();
            string manifestJson = await manifestResponse.Content.ReadAsStringAsync(cancellationToken);
            var manifest = JsonSerializer.Deserialize<CatalogManifest>(manifestJson, _jsonOptions);
            if (!IsValidManifest(manifest))
                throw new JsonException("Manifesto do subcatálogo MS Store inválido.");

            string indexPath = Path.Combine(_cacheDirectory, $"msstore-catalog-{manifest!.CatalogSha256}.json");
            string indexUrl = $"{MsStoreCatalogBaseUrl}/manifest/search-index.json";
            string indexJson;
            if (!forceRefresh && File.Exists(indexPath))
            {
                indexJson = await File.ReadAllTextAsync(indexPath, cancellationToken);
            }
            else
            {
                using var indexResponse = await _httpClient.GetAsync(indexUrl, cancellationToken);
                indexResponse.EnsureSuccessStatusCode();
                indexJson = await indexResponse.Content.ReadAsStringAsync(cancellationToken);
            }
            if (!HasExpectedHash(indexJson, manifest.IndexSha256))
            {
                using var indexResponse = await _httpClient.GetAsync(indexUrl, cancellationToken);
                indexResponse.EnsureSuccessStatusCode();
                indexJson = await indexResponse.Content.ReadAsStringAsync(cancellationToken);
                if (!HasExpectedHash(indexJson, manifest.IndexSha256))
                    throw new JsonException("Hash do índice do subcatálogo MS Store divergente.");
            }
            var apps = JsonSerializer.Deserialize<List<AppEntry>>(indexJson, _jsonOptions) ?? [];
            if (apps.Count != manifest.AppCount || !ValidateSearchEntries(apps)
                || apps.Any(app => !string.Equals(app.Source, "msstore", StringComparison.OrdinalIgnoreCase)))
                throw new JsonException("Índice do subcatálogo MS Store incompleto ou inválido.");
            SetCatalogRoute(apps, MsStoreCatalogBaseUrl, manifest.CatalogSha256);
            Directory.CreateDirectory(_cacheDirectory);
            string temporaryPath = indexPath + ".tmp";
            await File.WriteAllTextAsync(temporaryPath, indexJson, cancellationToken);
            File.Move(temporaryPath, indexPath, overwrite: true);
            return apps;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return TryLoadLocalMsStoreCatalog();
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[StoreService] Subcatálogo MS Store indisponível: {ex.Message}");
            return TryLoadLocalMsStoreCatalog();
        }
    }

    private static void SetCatalogRoute(IEnumerable<AppEntry> apps, string baseUrl, string catalogHash)
    {
        foreach (AppEntry app in apps)
        {
            app.CatalogDetailBaseUrl = baseUrl;
            app.CatalogDetailCatalogSha256 = catalogHash;
            app.StoreBannerUrl = IconService.ResolveCatalogMediaUrl(app, app.Media?.Banner, app.Media?.BannerSha256)
                ?? app.StoreBannerUrl;
            if (string.Equals(app.Source, "msstore", StringComparison.OrdinalIgnoreCase)
                && app.Media?.Screenshots is { Count: > 0 })
            {
                app.StoreScreenshotUrls = app.Media.Screenshots
                    .Select(path => IconService.ResolveCatalogMediaUrl(
                        app,
                        path,
                        app.Media?.ScreenshotSha256?.GetValueOrDefault(path)))
                    .Where(url => !string.IsNullOrWhiteSpace(url))
                    .Cast<string>()
                    .ToList();
            }
        }
    }

    private static List<AppEntry> MergeMsStoreCatalog(List<AppEntry> wingetCatalog, List<AppEntry> msStoreCatalog)
    {
        if (msStoreCatalog.Count == 0)
            return wingetCatalog;
        var storeIds = msStoreCatalog.Select(app => app.Id).ToHashSet(StringComparer.OrdinalIgnoreCase);
        wingetCatalog.RemoveAll(app => string.Equals(app.Source, "msstore", StringComparison.OrdinalIgnoreCase)
            || storeIds.Contains(app.Id));
        wingetCatalog.AddRange(msStoreCatalog);
        return wingetCatalog;
    }

    private static void RemoveLegacyScreenshotLinks(IEnumerable<AppEntry> catalog)
    {
        foreach (AppEntry app in catalog)
        {
            if (!string.Equals(app.Source, "msstore", StringComparison.OrdinalIgnoreCase)
                && app.ScreenshotUrls is { Count: > 0 } urls)
            {
                app.ScreenshotUrls = urls
                    .Where(url => !url.Contains(LegacyScreenshotBasePath, StringComparison.OrdinalIgnoreCase))
                    .ToList();
                if (app.ScreenshotUrls.Count == 0)
                    app.ScreenshotUrls = null;
            }
        }
    }

    private bool IsCacheStale()
    {
        try
        {
            string? searchIndexPath = _loadedSearchIndexCacheFilePath;
            return searchIndexPath is null ||
                   !File.Exists(searchIndexPath) ||
                   !File.Exists(_catalogManifestCacheFilePath) ||
                   DateTime.UtcNow - File.GetLastWriteTimeUtc(searchIndexPath) >= CatalogCacheTtl ||
                   DateTime.UtcNow - File.GetLastWriteTimeUtc(_catalogManifestCacheFilePath) >= CatalogCacheTtl;
        }
        catch
        {
            return true;
        }
    }

    private Task<List<AppEntry>> RefreshCacheInBackgroundAsync(
        CancellationToken cancellationToken = default,
        bool forceRefresh = false,
        bool revalidateRemote = false)
    {
        lock (_refreshSync)
        {
            if (_refreshTask is { IsCompleted: false })
                return _refreshTask;

            _refreshCts?.Dispose();
            _refreshCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            CancellationToken token = _refreshCts.Token;
            Task<List<AppEntry>> task = FetchAndSaveRemoteCatalogAsync(token, forceRefresh, revalidateRemote);
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
