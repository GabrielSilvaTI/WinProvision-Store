using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace WinProvision.Core.Services;

public sealed class FeaturedCatalogDocument
{
    [JsonPropertyName("schemaVersion")] public int SchemaVersion { get; set; }
    [JsonPropertyName("generatedUtc")] public DateTimeOffset GeneratedUtc { get; set; }
    [JsonPropertyName("metricWindowDays")] public int MetricWindowDays { get; set; }
    [JsonPropertyName("installMetricsAvailable")] public bool InstallMetricsAvailable { get; set; }
    [JsonPropertyName("hero")] public List<FeaturedAppReference> Hero { get; set; } = [];
    [JsonPropertyName("popular")] public List<FeaturedAppReference> Popular { get; set; } = [];
    [JsonPropertyName("categories")] public List<FeaturedCategory> Categories { get; set; } = [];
}

public sealed class FeaturedAppReference
{
    [JsonPropertyName("id")] public string Id { get; set; } = string.Empty;
    [JsonPropertyName("source")] public string Source { get; set; } = string.Empty;
    [JsonPropertyName("name")] public string Name { get; set; } = string.Empty;
    [JsonPropertyName("publisher")] public string Publisher { get; set; } = string.Empty;
    [JsonPropertyName("installCount30d")] public int InstallCount30d { get; set; }
    [JsonPropertyName("category")] public string? Category { get; set; }
}

public sealed class FeaturedCategory
{
    [JsonPropertyName("id")] public string Id { get; set; } = string.Empty;
    [JsonPropertyName("title")] public string Title { get; set; } = string.Empty;
    [JsonPropertyName("apps")] public List<FeaturedAppReference> Apps { get; set; } = [];
}

/// <summary>Cache local e atualização em segundo plano do catálogo editorial de Destaques.</summary>
public sealed class FeaturedCatalogService
{
    private const string CatalogUrl = "https://pub-166b41912a994dbe86583ba10596d673.r2.dev/Store/Catalog/manifest/featured.json";
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(12) };
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private static readonly TimeSpan RefreshInterval = TimeSpan.FromHours(6);
    private static readonly TimeSpan RetryInterval = TimeSpan.FromMinutes(15);
    private readonly string _cachePath;
    private readonly SemaphoreSlim _refreshGate = new(1, 1);
    private FeaturedCatalogDocument? _current;
    private DateTimeOffset _lastRefreshAttemptUtc;

    public event Action<FeaturedCatalogDocument>? Updated;

    public FeaturedCatalogService(string? cacheDirectory = null)
    {
        string root = cacheDirectory ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "WinProvisionStore", "Cache");
        _cachePath = Path.Combine(root, "featured-catalog.json");
    }

    public async Task<FeaturedCatalogDocument?> LoadAsync(CancellationToken cancellationToken = default)
    {
        if (_current is null)
        {
            _current = await ReadCacheAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                if (File.Exists(_cachePath))
                    _lastRefreshAttemptUtc = new DateTimeOffset(File.GetLastWriteTimeUtc(_cachePath), TimeSpan.Zero);
            }
            catch (IOException) { }
        }

        TimeSpan interval = _current is null ? RetryInterval : RefreshInterval;
        if (DateTimeOffset.UtcNow - _lastRefreshAttemptUtc >= interval)
            _ = RefreshInBackgroundAsync();
        return _current;
    }

    private async Task<FeaturedCatalogDocument?> ReadCacheAsync(CancellationToken cancellationToken)
    {
        try
        {
            if (!File.Exists(_cachePath)) return null;
            string json = await File.ReadAllTextAsync(_cachePath, cancellationToken).ConfigureAwait(false);
            return DeserializeValid(json);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            Debug.WriteLine($"[FeaturedCatalog] Cache local indisponível: {ex.Message}");
            return null;
        }
    }

    private async Task RefreshInBackgroundAsync()
    {
        if (!await _refreshGate.WaitAsync(0).ConfigureAwait(false)) return;
        _lastRefreshAttemptUtc = DateTimeOffset.UtcNow;
        try
        {
            using HttpResponseMessage response = await Http.GetAsync(CatalogUrl, HttpCompletionOption.ResponseHeadersRead)
                .ConfigureAwait(false);
            response.EnsureSuccessStatusCode();
            string json = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
            FeaturedCatalogDocument? document = DeserializeValid(json);
            if (document is null) return;

            Directory.CreateDirectory(Path.GetDirectoryName(_cachePath)!);
            string temp = _cachePath + ".tmp";
            await File.WriteAllTextAsync(temp, json).ConfigureAwait(false);
            File.Move(temp, _cachePath, overwrite: true);
            _current = document;
            Updated?.Invoke(document);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or IOException
                                   or UnauthorizedAccessException or JsonException)
        {
            Debug.WriteLine($"[FeaturedCatalog] Atualização remota indisponível; usando cache: {ex.Message}");
        }
        finally
        {
            _refreshGate.Release();
        }
    }

    private static FeaturedCatalogDocument? DeserializeValid(string json)
    {
        FeaturedCatalogDocument? document = JsonSerializer.Deserialize<FeaturedCatalogDocument>(json, JsonOptions);
        if (document is not { SchemaVersion: 1, MetricWindowDays: > 0 }
            || document.Hero is null || document.Popular is null || document.Categories is null)
            return null;

        static bool Valid(FeaturedAppReference app) =>
            !string.IsNullOrWhiteSpace(app.Id)
            && app.Id.Length <= 128
            && app.Source is "winget" or "msstore"
            && !string.IsNullOrWhiteSpace(app.Name);

        document.Hero = document.Hero.Where(Valid).Take(3).ToList();
        document.Popular = document.Popular.Where(Valid).Take(48).ToList();
        document.Categories = document.Categories
            .Where(category => category.Apps is not null && !string.IsNullOrWhiteSpace(category.Title))
            .Select(category => new FeaturedCategory
            {
                Id = category.Id,
                Title = category.Title,
                Apps = category.Apps.Where(Valid).Take(8).ToList()
            })
            .Where(category => category.Apps.Count > 0)
            .Take(12)
            .ToList();
        return document;
    }
}
