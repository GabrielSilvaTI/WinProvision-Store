using System.Net.Http;
using System.Collections.Concurrent;
using System.Text.Json.Serialization;
using WinProvision.Core.Models;

namespace WinProvision.Core.Services.Indexing;

/// <summary>
/// Resolve apps curados manualmente (config/msstore-curated.json no Indexer) contra a
/// Display Catalog API pública da Microsoft Store — o mesmo backend que o winget usa
/// pra source "msstore". Existe porque a source msstore não tem um repositório
/// enumerável como o winget-pkgs: não há como "escanear tudo", só resolver Product IDs
/// conhecidos um a um. Ver Program.cs, passo [+], para onde essas entradas entram na
/// pipeline (junto de "candidates", antes do corte por score).
///
/// Endpoint não documentado oficialmente, mas usado sem autenticação por ferramentas
/// de terceiros consolidadas (winget-cli, StoreLib, rg-adguard) pra consulta pública de
/// metadados de apps — não requer chave nem token.
/// </summary>
public class MsStoreCatalogService
{
    private const string DisplayCatalogUrl = "https://displaycatalog.mp.microsoft.com/v7.0/products";

    // Limite conservador por chamada, só pra não montar uma query string gigante quando
    // a lista curada crescer — a API aceita bigIds em lote via vírgula.
    private const int BatchSize = 20;

    private readonly HttpClient _httpClient;
    private readonly ConcurrentDictionary<string, AppEntry> _productCache = new(StringComparer.OrdinalIgnoreCase);
    private readonly SemaphoreSlim _fetchLock = new(1, 1);

    public MsStoreCatalogService(HttpClient? httpClient = null)
    {
        _httpClient = httpClient ?? new HttpClient { Timeout = TimeSpan.FromSeconds(15) };
    }

    public async Task<List<AppEntry>> SearchAsync(
        string query,
        CancellationToken cancellationToken = default,
        string market = "BR",
        string language = "pt-BR")
    {
        if (string.IsNullOrWhiteSpace(query))
            return [];

        string url = $"https://displaycatalog.mp.microsoft.com/v7.0/productFamilies/Apps/products" +
            $"?query={Uri.EscapeDataString(query.Trim())}&market={Uri.EscapeDataString(market)}&languages={Uri.EscapeDataString(language)}" +
            "&fieldsTemplate=details&platformdependencyname=windows.xbox";

        try
        {
            using var response = await SendWithRetryAsync(url, cancellationToken);
            if (!response.IsSuccessStatusCode)
                return [];

            await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
            var payload = await System.Text.Json.JsonSerializer.DeserializeAsync<DisplayCatalogSearchResponse>(
                stream, WinProvisionJsonOptions.Default, cancellationToken);

            var results = new List<AppEntry>();
            foreach (var product in payload?.Products ?? [])
            {
                var entry = MapToAppEntry(product, language);
                if (entry is null)
                    continue;

                _productCache[entry.Id] = entry;
                results.Add(entry);
            }

            return results
                .DistinctBy(app => app.Id, StringComparer.OrdinalIgnoreCase)
                .ToList();
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[MsStoreCatalogService] Busca Store falhou: {ex.Message}");
            return [];
        }
    }

    public async Task<List<AppEntry>> FetchAsync(
        IReadOnlyList<string> storeProductIds,
        CancellationToken cancellationToken = default,
        string market = "BR",
        string language = "pt-BR")
    {
        var ids = storeProductIds.Where(id => !string.IsNullOrWhiteSpace(id)).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        await _fetchLock.WaitAsync(cancellationToken);
        try
        {
            var missingIds = ids.Where(id => !_productCache.ContainsKey(id)).ToList();

            for (int offset = 0; offset < missingIds.Count; offset += BatchSize)
            {
                var batch = missingIds.Skip(offset).Take(BatchSize).ToList();
                string bigIds = string.Join(',', batch);
                string url = $"{DisplayCatalogUrl}?bigIds={Uri.EscapeDataString(bigIds)}&market={Uri.EscapeDataString(market)}&languages={Uri.EscapeDataString(language)}&fieldsTemplate=Details";

                try
                {
                    using var response = await SendWithRetryAsync(url, cancellationToken);
                    if (!response.IsSuccessStatusCode)
                    {
                        Console.WriteLine($"      [AVISO] Display Catalog respondeu {(int)response.StatusCode} para o lote atual, pulando.");
                        continue;
                    }

                    await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
                    var payload = await System.Text.Json.JsonSerializer.DeserializeAsync<DisplayCatalogResponse>(
                        stream, WinProvisionJsonOptions.Default, cancellationToken);

                    foreach (var product in payload?.Products ?? [])
                    {
                        var entry = MapToAppEntry(product, language);
                        if (entry != null)
                        {
                            _productCache[entry.Id] = entry;
                        }
                    }
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"      [AVISO] Falha ao consultar Display Catalog: {ex.Message}");
                }
            }
        }
        finally
        {
            _fetchLock.Release();
        }

        var results = new List<AppEntry>(ids.Count);
        foreach (string id in ids)
        {
            if (_productCache.TryGetValue(id, out var app))
                results.Add(app);
        }
        return results;
    }

    private async Task<HttpResponseMessage> SendWithRetryAsync(string url, CancellationToken cancellationToken)
    {
        const int maxAttempts = 3;
        for (int attempt = 1; ; attempt++)
        {
            HttpResponseMessage response;
            try
            {
                response = await _httpClient.GetAsync(url, cancellationToken);
            }
            catch (HttpRequestException) when (attempt < maxAttempts)
            {
                await Task.Delay(TimeSpan.FromMilliseconds(350 * attempt), cancellationToken);
                continue;
            }
            catch (TaskCanceledException) when (!cancellationToken.IsCancellationRequested && attempt < maxAttempts)
            {
                await Task.Delay(TimeSpan.FromMilliseconds(350 * attempt), cancellationToken);
                continue;
            }

            if ((int)response.StatusCode < 500 && response.StatusCode != System.Net.HttpStatusCode.TooManyRequests)
                return response;

            if (attempt == maxAttempts)
                return response;

            TimeSpan delay = response.Headers.RetryAfter?.Delta ?? TimeSpan.FromMilliseconds(350 * attempt);
            response.Dispose();
            await Task.Delay(delay > TimeSpan.FromSeconds(10) ? TimeSpan.FromSeconds(10) : delay, cancellationToken);
        }
    }

    private static AppEntry? MapToAppEntry(DisplayCatalogProduct product, string requestedLanguage)
    {
        var localizedProperties = product.LocalizedProperties ?? [];
        var localized = localizedProperties.FirstOrDefault(property =>
                            string.Equals(property.Language, requestedLanguage, StringComparison.OrdinalIgnoreCase))
                        ?? localizedProperties.FirstOrDefault();
        if (localized is null || string.IsNullOrWhiteSpace(product.ProductId))
        {
            return null;
        }

        var images = localized.Images ?? [];
        string? iconUrl = images
            .Where(image => !string.IsNullOrWhiteSpace(image.Uri))
            .OrderBy(image => GetIconPurposeRank(image.ImagePurpose))
            .ThenBy(image => GetSquareAspectDifference(image.Width, image.Height))
            .ThenByDescending(image => Math.Min(image.Width, image.Height))
            .Select(image => NormalizeImageUrl(image.Uri))
            .FirstOrDefault(uri => uri is not null);

        string? bannerUrl = images
            .Where(image => !string.IsNullOrWhiteSpace(image.Uri) && image.Width > 0 && image.Height > 0)
            .OrderBy(image => string.Equals(image.ImagePurpose, "SuperHeroArt", StringComparison.OrdinalIgnoreCase) ? 0 : 1)
            .ThenByDescending(image => image.Width / (double)image.Height)
            .ThenByDescending(image => image.Width)
            .Select(image => NormalizeImageUrl(image.Uri))
            .FirstOrDefault(uri => uri is not null);

        var allTimeRating = product.MarketProperties?
            .SelectMany(market => market.UsageData ?? [])
            .FirstOrDefault(usage => string.Equals(
                usage.AggregateTimeSpan, "AllTime", StringComparison.OrdinalIgnoreCase));
        var rating = allTimeRating ?? product.MarketProperties?
            .SelectMany(market => market.UsageData ?? [])
            .OrderByDescending(usage => usage.RatingCount)
            .FirstOrDefault();

        return new AppEntry
        {
            Id = product.ProductId,
            Source = "msstore",
            // A Display Catalog não expõe uma versão fixa e comparável ao esquema do
            // winget-pkgs (winget/COM resolvem a versão vigente na hora da instalação),
            // então não há um número real pra publicar aqui.
            Version = "-",
            Name = localized.ProductTitle ?? product.ProductId,
            Publisher = localized.PublisherName ?? "Microsoft Store",
            // A consulta pode devolver a localização padrão quando não há tradução
            // para o idioma solicitado. Usa o idioma informado pela própria Store.
            PackageLocale = localized.Language,
            Homepage = $"https://apps.microsoft.com/detail/{product.ProductId}",
            Description = localized.ShortDescription ?? localized.Description,
            StoreIconUrl = iconUrl,
            StoreBannerUrl = bannerUrl,
            StoreCategory = product.Properties?.Category,
            StoreSubCategory = product.Properties?.SubCategory,
            StoreScreenshotUrls = images
                .Where(image => string.Equals(image.ImagePurpose, "Screenshot", StringComparison.OrdinalIgnoreCase))
                .OrderByDescending(image => (long)image.Width * image.Height)
                .Select(image => NormalizeScreenshotUrl(image.Uri))
                .Where(uri => uri is not null)
                .Cast<string>()
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList(),
            StoreRating = rating?.AverageRating is > 0 and <= 5 ? rating.AverageRating : null,
            StoreRatingCount = rating?.RatingCount is > 0 ? rating.RatingCount : null,
        };
    }

    private static int GetIconPurposeRank(string? purpose) => purpose?.ToLowerInvariant() switch
    {
        "logo" => 0,
        "tile" => 1,
        "boxart" => 2,
        _ => 3
    };

    private static double GetSquareAspectDifference(int width, int height)
    {
        if (width <= 0 || height <= 0)
            return double.MaxValue;

        return Math.Abs(1d - width / (double)height);
    }

    private static string? NormalizeImageUrl(string? uri)
    {
        if (string.IsNullOrWhiteSpace(uri))
        {
            return null;
        }

        return uri.StartsWith("//", StringComparison.Ordinal) ? $"https:{uri}" : uri;
    }

    private static string? NormalizeScreenshotUrl(string? uri)
    {
        string? normalized = NormalizeImageUrl(uri);
        if (!Uri.TryCreate(normalized, UriKind.Absolute, out var parsed) ||
            !(parsed.Host.Equals("s-microsoft.com", StringComparison.OrdinalIgnoreCase) ||
              parsed.Host.EndsWith(".s-microsoft.com", StringComparison.OrdinalIgnoreCase)))
        {
            return normalized;
        }

        // A Display Catalog screenshot URI may already be constrained to a small
        // rendition (commonly 480x270). Request the CDN's high-quality 1080p variant.
        var builder = new UriBuilder(parsed);
        var query = builder.Query.TrimStart('?')
            .Split('&', StringSplitOptions.RemoveEmptyEntries)
            .Where(parameter =>
            {
                int separator = parameter.IndexOf('=');
                string name = separator < 0 ? parameter : parameter[..separator];
                return !name.Equals("q", StringComparison.OrdinalIgnoreCase) &&
                       !name.Equals("w", StringComparison.OrdinalIgnoreCase) &&
                       !name.Equals("h", StringComparison.OrdinalIgnoreCase);
            })
            .ToList();
        query.Add("q=97");
        query.Add("w=1920");
        query.Add("h=1080");
        builder.Query = string.Join('&', query);
        return builder.Uri.AbsoluteUri;
    }

    private class DisplayCatalogSearchResponse
    {
        [JsonPropertyName("Products")]
        public List<DisplayCatalogProduct>? Products { get; set; }
    }

    private class DisplayCatalogResponse
    {
        [JsonPropertyName("Products")]
        public List<DisplayCatalogProduct>? Products { get; set; }
    }

    private class DisplayCatalogProduct
    {
        [JsonPropertyName("ProductId")]
        public string? ProductId { get; set; }

        [JsonPropertyName("LocalizedProperties")]
        public List<LocalizedProperty>? LocalizedProperties { get; set; }

        [JsonPropertyName("MarketProperties")]
        public List<MarketProperty>? MarketProperties { get; set; }

        [JsonPropertyName("Properties")]
        public DisplayCatalogProductProperties? Properties { get; set; }
    }

    private class DisplayCatalogProductProperties
    {
        [JsonPropertyName("Category")]
        public string? Category { get; set; }

        [JsonPropertyName("SubCategory")]
        public string? SubCategory { get; set; }
    }

    private class MarketProperty
    {
        [JsonPropertyName("UsageData")]
        public List<UsageData>? UsageData { get; set; }
    }

    private class UsageData
    {
        [JsonPropertyName("AggregateTimeSpan")]
        public string AggregateTimeSpan { get; set; } = string.Empty;

        [JsonPropertyName("AverageRating")]
        public double AverageRating { get; set; }

        [JsonPropertyName("RatingCount")]
        public int RatingCount { get; set; }
    }

    private class LocalizedProperty
    {
        [JsonPropertyName("Language")]
        public string? Language { get; set; }

        [JsonPropertyName("ProductTitle")]
        public string? ProductTitle { get; set; }

        [JsonPropertyName("PublisherName")]
        public string? PublisherName { get; set; }

        [JsonPropertyName("ShortDescription")]
        public string? ShortDescription { get; set; }

        [JsonPropertyName("Description")]
        public string? Description { get; set; }

        [JsonPropertyName("Images")]
        public List<ProductImage>? Images { get; set; }
    }

    private class ProductImage
    {
        [JsonPropertyName("Uri")]
        public string? Uri { get; set; }

        [JsonPropertyName("ImagePurpose")]
        public string? ImagePurpose { get; set; }

        [JsonPropertyName("Height")]
        public int Height { get; set; }

        [JsonPropertyName("Width")]
        public int Width { get; set; }
    }
}
