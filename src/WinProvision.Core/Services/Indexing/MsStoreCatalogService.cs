using System.Net.Http;
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

    public MsStoreCatalogService(HttpClient? httpClient = null)
    {
        _httpClient = httpClient ?? new HttpClient { Timeout = TimeSpan.FromSeconds(15) };
    }

    public async Task<List<AppEntry>> FetchAsync(IReadOnlyList<string> storeProductIds, CancellationToken cancellationToken = default)
    {
        var result = new List<AppEntry>();
        var ids = storeProductIds.Where(id => !string.IsNullOrWhiteSpace(id)).Distinct(StringComparer.OrdinalIgnoreCase).ToList();

        for (int offset = 0; offset < ids.Count; offset += BatchSize)
        {
            var batch = ids.Skip(offset).Take(BatchSize).ToList();
            string bigIds = string.Join(',', batch);
            string url = $"{DisplayCatalogUrl}?bigIds={bigIds}&market=US&languages=en-us&fieldsTemplate=Details";

            try
            {
                using var response = await _httpClient.GetAsync(url, cancellationToken);
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
                    var entry = MapToAppEntry(product);
                    if (entry != null)
                    {
                        result.Add(entry);
                    }
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"      [AVISO] Falha ao consultar Display Catalog: {ex.Message}");
            }
        }

        return result;
    }

    private static AppEntry? MapToAppEntry(DisplayCatalogProduct product)
    {
        var localized = product.LocalizedProperties?.FirstOrDefault();
        if (localized is null || string.IsNullOrWhiteSpace(product.ProductId))
        {
            return null;
        }

        string? iconUrl = localized.Images?
            .Where(i => string.Equals(i.ImagePurpose, "Logo", StringComparison.OrdinalIgnoreCase)
                     || string.Equals(i.ImagePurpose, "Tile", StringComparison.OrdinalIgnoreCase)
                     || string.Equals(i.ImagePurpose, "BoxArt", StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(i => i.Height)
            .Select(i => NormalizeImageUrl(i.Uri))
            .FirstOrDefault(u => u != null);

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
            Homepage = $"https://apps.microsoft.com/detail/{product.ProductId}",
            Description = localized.ShortDescription ?? localized.Description,
            StoreIconUrl = iconUrl,
        };
    }

    private static string? NormalizeImageUrl(string? uri)
    {
        if (string.IsNullOrWhiteSpace(uri))
        {
            return null;
        }

        return uri.StartsWith("//", StringComparison.Ordinal) ? $"https:{uri}" : uri;
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
    }

    private class LocalizedProperty
    {
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
    }
}
