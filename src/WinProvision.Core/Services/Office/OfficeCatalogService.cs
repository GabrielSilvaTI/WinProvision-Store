using System.Net;
using System.Text.Json;
using System.Text.RegularExpressions;
using WinProvision.Core.Models.Office;

namespace WinProvision.Core.Services.Office;

/// <summary>
/// Catálogo Office próprio: usa o arquivo publicado no R2, mantém uma cópia local
/// validada e conserva os planos embutidos quando a rede/API não estiver disponível.
/// O endpoint entrega metadados e IDs comerciais; nenhum dado da Microsoft Store é
/// consultado em tempo de execução.
/// </summary>
public sealed class OfficeCatalogService : IDisposable
{
    public const string CatalogUrl = "https://pub-166b41912a994dbe86583ba10596d673.r2.dev/Office/Database/catalog.json";
    private static readonly HttpClient Http = new(new SocketsHttpHandler
    {
        AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate | DecompressionMethods.Brotli,
        PooledConnectionLifetime = TimeSpan.FromMinutes(10),
    }) { Timeout = TimeSpan.FromSeconds(5) };
    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNameCaseInsensitive = true, MaxDepth = 16 };
    private static readonly Regex SafeId = new("^[A-Za-z0-9._-]{2,100}$", RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private readonly string _cachePath;
    private readonly SemaphoreSlim _refreshLock = new(1, 1);
    private DateTimeOffset _nextRetryAt;
    private bool _hasValidCache;
    private bool _hasFeaturedScreenshots;
    private bool _disposed;

    public OfficeCatalogService(string? cacheDirectory = null)
    {
        _cachePath = Path.Combine(cacheDirectory ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "WinProvisionStore"), "office-catalog.json");
        LoadBundledCatalog();
        LoadLastKnownGood();
    }

    private void LoadBundledCatalog()
    {
        try
        {
            using var stream = typeof(OfficeCatalogService).Assembly
                .GetManifestResourceStream("WinProvision.Core.Office.catalog.json");
            if (stream is null) return;

            var document = JsonSerializer.Deserialize<OfficeCatalogDocument>(stream, JsonOptions);
            var plans = Validate(document);
            var offers = ValidateOffers(document, plans);
            OfficePlanCatalog.ApplyRemote(plans);
            OfficeStoreOfferCatalog.ApplyRemote(offers);
            _hasFeaturedScreenshots = OfficeStoreOfferCatalog.HasFeaturedScreenshots;
        }
        catch
        {
            // O catálogo embutido é um fallback de mídia; os planos built-in permanecem válidos.
        }
    }

    public async Task<bool> RefreshAsync(bool force = false, CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        await _refreshLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (!force && DateTimeOffset.UtcNow < _nextRetryAt)
                return false;
            if (!force && _hasValidCache && _hasFeaturedScreenshots && File.Exists(_cachePath) &&
                DateTime.UtcNow - File.GetLastWriteTimeUtc(_cachePath) < TimeSpan.FromHours(24))
                return false;

            using var response = await Http.GetAsync(CatalogUrl, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
            response.EnsureSuccessStatusCode();
            if (response.Content.Headers.ContentLength is > 2_000_000)
                throw new InvalidDataException("O catálogo Office excede 2 MB.");

            await using var responseStream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
            using var boundedContent = new MemoryStream();
            var buffer = new byte[64 * 1024];
            int bytesRead;
            while ((bytesRead = await responseStream.ReadAsync(buffer, cancellationToken).ConfigureAwait(false)) > 0)
            {
                if (boundedContent.Length + bytesRead > 2_000_000)
                    throw new InvalidDataException("O catálogo Office excede 2 MB.");
                await boundedContent.WriteAsync(buffer.AsMemory(0, bytesRead), cancellationToken).ConfigureAwait(false);
            }
            boundedContent.Position = 0;
            var document = await JsonSerializer.DeserializeAsync<OfficeCatalogDocument>(boundedContent, JsonOptions, cancellationToken).ConfigureAwait(false);
            var plans = Validate(document);
            var offers = ValidateOffers(document, plans);
            OfficePlanCatalog.ApplyRemote(plans);
            OfficeStoreOfferCatalog.ApplyRemote(offers);
            _hasFeaturedScreenshots = OfficeStoreOfferCatalog.HasFeaturedScreenshots;

            Directory.CreateDirectory(Path.GetDirectoryName(_cachePath)!);
            var tempPath = _cachePath + ".tmp";
            await using (var stream = File.Create(tempPath))
                await JsonSerializer.SerializeAsync(stream, document, JsonOptions, cancellationToken).ConfigureAwait(false);
            File.Move(tempPath, _cachePath, overwrite: true);
            _hasValidCache = true;
            // Catálogo válido sem as capturas em destaque recebe nova tentativa
            // limitada, em vez de ficar preso no cache por 24 horas ou requisitar
            // a API a cada navegação.
            _nextRetryAt = _hasFeaturedScreenshots ? DateTimeOffset.MinValue : DateTimeOffset.UtcNow.AddHours(6);
            return true;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch
        {
            // API opcional: catálogo em memória/último cache válido permanece ativo.
            _nextRetryAt = DateTimeOffset.UtcNow.AddMinutes(30);
            return false;
        }
        finally
        {
            _refreshLock.Release();
        }
    }

    private void LoadLastKnownGood()
    {
        try
        {
            if (!File.Exists(_cachePath) || new FileInfo(_cachePath).Length > 2_000_000) return;
            var document = JsonSerializer.Deserialize<OfficeCatalogDocument>(File.ReadAllText(_cachePath), JsonOptions);
            var plans = Validate(document);
            var offers = ValidateOffers(document, plans);
            OfficePlanCatalog.ApplyRemote(plans);
            OfficeStoreOfferCatalog.ApplyRemote(offers);
            _hasValidCache = true;
            _hasFeaturedScreenshots = OfficeStoreOfferCatalog.HasFeaturedScreenshots;
        }
        catch
        {
            // O catálogo embutido sempre permite instalar sem depender do cache.
        }
    }

    private static IReadOnlyList<OfficePlan> Validate(OfficeCatalogDocument? document)
    {
        if (document is null || document.SchemaVersion is < 1 or > 2 || document.Products is null || document.Products.Count > 200)
            throw new InvalidDataException("Formato do catálogo Office inválido.");

        var result = new List<OfficePlan>(document.Products.Count);
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var item in document.Products)
        {
            var productId = item?.ProductId ?? string.Empty;
            var displayName = item?.DisplayName;
            if (item is null || !SafeId.IsMatch(productId) || string.IsNullOrWhiteSpace(displayName) || displayName.Length > 160 ||
                !Enum.TryParse<OfficeEditionCategory>(item.Category, true, out var category) || !seen.Add(productId))
                throw new InvalidDataException("Produto inválido no catálogo Office.");
            if (item.Channel is { Length: > 0 } && !SafeId.IsMatch(item.Channel))
                throw new InvalidDataException("Canal inválido no catálogo Office.");

            result.Add(new OfficePlan(displayName.Trim(), category, productId, item.Channel, item.IsVolumeLicensed)
            {
                Description = CleanText(item.Description, 500),
                IconUrl = SafeHttps(item.IconUrl),
                BannerUrl = SafeHttps(item.BannerUrl),
                Screenshots = (item.Screenshots ?? []).Where(s => SafeHttps(s) is not null).Take(12).Select(s => s.Trim()).ToArray(),
            });
        }
        return result;
    }

    private static IReadOnlyList<OfficeStoreOffer> ValidateOffers(OfficeCatalogDocument? document, IReadOnlyList<OfficePlan> plans)
    {
        var offers = new List<OfficeStoreOffer>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var offer in document?.StoreOffers ?? [])
        {
            var storeProductId = offer?.StoreProductId ?? string.Empty;
            var displayName = offer?.DisplayName;
            var odtProductId = offer?.OdtProductId ?? string.Empty;
            if (offer is null || !SafeId.IsMatch(storeProductId) || string.IsNullOrWhiteSpace(displayName) ||
                displayName.Length > 160 || !SafeId.IsMatch(odtProductId) || !seen.Add(storeProductId) ||
                !plans.Concat(OfficePlanCatalog.All).Any(p => string.Equals(p.ProductId, odtProductId, StringComparison.OrdinalIgnoreCase)))
                throw new InvalidDataException("Oferta Store sem vínculo ODT válido.");
            var screenshots = (offer.Screenshots ?? []).Where(s => SafeHttps(s) is not null)
                .Take(12).Select(s => s.Trim()).ToArray();
            // Alguns catálogos publicam as capturas junto ao produto ODT e deixam
            // StoreOffers sem a cópia comercial. Reutilizar a mídia do produto
            // associado evita mostrar a galeria vazia para Family/Personal.
            var relatedPlan = plans.FirstOrDefault(plan =>
                string.Equals(plan.ProductId, odtProductId, StringComparison.OrdinalIgnoreCase))
                ?? OfficePlanCatalog.ByProductId(odtProductId);
            if (screenshots.Length == 0 && relatedPlan is not null)
                screenshots = relatedPlan.Screenshots
                    .Where(s => SafeHttps(s) is not null)
                    .Take(12)
                    .ToArray();
            offers.Add(new OfficeStoreOffer(storeProductId, displayName.Trim(), odtProductId,
                CleanText(offer.Description, 500), SafeHttps(offer.IconUrl), SafeHttps(offer.BannerUrl),
                screenshots.Length > 0 ? screenshots : null));
        }
        return offers;
    }

    private static string? SafeHttps(string? value) =>
        Uri.TryCreate(value, UriKind.Absolute, out var uri) && uri.Scheme == Uri.UriSchemeHttps ? uri.AbsoluteUri : null;

    private static string? CleanText(string? value, int maxLength) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim()[..Math.Min(value.Trim().Length, maxLength)];

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _refreshLock.Dispose();
    }

    private sealed class OfficeCatalogDocument
    {
        public OfficeCatalogDocument() { }
        public int SchemaVersion { get; set; }
        public DateTimeOffset? UpdatedAt { get; set; }
        public List<OfficeCatalogProduct>? Products { get; set; }
        public List<OfficeStoreOfferItem>? StoreOffers { get; set; }
    }

    private sealed class OfficeCatalogProduct
    {
        public OfficeCatalogProduct() { }
        public string? ProductId { get; set; }
        public string? DisplayName { get; set; }
        public string? Category { get; set; }
        public string? Channel { get; set; }
        public bool IsVolumeLicensed { get; set; }
        public string? Description { get; set; }
        public string? IconUrl { get; set; }
        public string? BannerUrl { get; set; }
        public List<string>? Screenshots { get; set; }
    }

    private sealed class OfficeStoreOfferItem
    {
        public OfficeStoreOfferItem() { }
        public string? StoreProductId { get; set; }
        public string? DisplayName { get; set; }
        public string? OdtProductId { get; set; }
        public string? Description { get; set; }
        public string? IconUrl { get; set; }
        public string? BannerUrl { get; set; }
        public List<string>? Screenshots { get; set; }
    }
}
