using System.Net.Http;
using System.Text.Json;

namespace WinProvision.Core.Services.IconSync;

/// <summary>
/// Ícones extraídos manualmente pelo autor do projeto e publicados no bucket R2
/// próprio (Cloudflare) — mais de 500MB, um PackageIdentifier do winget por
/// arquivo. Diferente das demais fontes comunitárias, essa não precisa de
/// heurística de nome: as chaves do mapa já são o PackageIdentifier real, então
/// o match é direto.
///
/// Substitui o antigo <see cref="WinProvision.Store.Converters.WebpUrlToBitmapConverter"/>
/// (conversor de teste que só validava se os ícones do R2 apareciam na UI e nunca
/// foi ligado à pipeline de verdade).
///
/// Entra logo depois do manifesto oficial do WinGet: é curadoria própria, feita
/// diretamente em cima do catálogo do app, mais confiável que Winstall/external/
/// UniGetUI — mas o CDN da Microsoft ainda vem primeiro por já ser a mesma
/// infraestrutura usada pelo `winget install`.
/// </summary>
public class CloudflareIconRepository
{
    private const string MapUrl = "https://pub-166b41912a994dbe86583ba10596d673.r2.dev/Store/apps-icons-map.json";
    private const string BaseIconUrl = "https://pub-166b41912a994dbe86583ba10596d673.r2.dev/Store/icons";

    private static readonly JsonSerializerOptions ReadOptions = new() { PropertyNameCaseInsensitive = true };

    public async Task<Dictionary<string, string>> LoadAsync(HttpClient? httpClient = null)
    {
        var resolved = new Dictionary<string, string>();

        HttpClient http = httpClient ?? new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
        try
        {
            string json;
            try
            {
                json = await http.GetStringAsync(MapUrl);
            }
            catch
            {
                // Bucket fora do ar ou mapa não publicado no momento do sync — a
                // pipeline segue sem essa fonte, como já acontece com qualquer
                // outra fonte indisponível (ver comentário equivalente em
                // UniGetUiIconRepository.ParseFile).
                return resolved;
            }

            var map = JsonSerializer.Deserialize<Dictionary<string, string>>(json, ReadOptions) ?? [];

            foreach (var (packageId, fileName) in map)
            {
                string normalizedId = IconIdNormalizer.Normalize(packageId);
                if (normalizedId.Length == 0 || string.IsNullOrWhiteSpace(fileName)) continue;

                resolved.TryAdd(normalizedId, $"{BaseIconUrl}/{Uri.EscapeDataString(fileName)}");
            }
        }
        finally
        {
            if (httpClient is null) http.Dispose();
        }

        return resolved;
    }
}
