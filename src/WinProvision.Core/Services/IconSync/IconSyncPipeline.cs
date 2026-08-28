using System.Net.Http;
using System.Text.Json;
using WinProvision.Core.Models;

namespace WinProvision.Core.Services.IconSync;

/// <summary>
/// Orquestra a sincronização de ícones a partir do manifesto oficial do WinGet e
/// das três fontes comunitárias, contra o catálogo publicado (apps.json). Ordem
/// de prioridade quando mais de uma fonte resolve o mesmo Id (a primeira que
/// resolver vence, as demais só preenchem lacunas):
///
///   1. Manifesto oficial do WinGet    — CDN + index.db (WinGetOfficialManifestRepository).
///                                        Mesma infraestrutura que o `winget install` já usa,
///                                        não é uma dependência nova de terceiro. Cobertura
///                                        parcial esperada (nem todo manifesto declara Icons)
///                                        e opcional (só roda se --winget-index-db for passado).
///   2. Winstall aprovado manualmente  — curadoria humana, a mais confiável entre as fontes
///                                        comunitárias
///   3. package-icons externo          — curado especificamente para winget, match exato
///                                        por nome de arquivo, baixo risco de ícone errado
///   4. UniGetUI                       — maior cobertura, mas agrega vários gerenciadores
///                                        de pacote (winget, scoop, chocolatey) numa base só;
///                                        testes manuais mostraram ícones incorretos/quebrados
///                                        em parte das entradas, então entra por último —
///                                        só preenche o que as fontes mais confiáveis não cobrem
///
/// Gera dois arquivos em OutputDir:
///   icons-database.json              Dictionary&lt;PackageIdentifier normalizado, URL do ícone&gt;
///   winstall-review-candidates.json  Sugestões para aprovação manual (nunca aplicadas)
/// </summary>
public class IconSyncPipeline
{
    private static readonly JsonSerializerOptions ReadOptions = new() { PropertyNameCaseInsensitive = true };
    private static readonly JsonSerializerOptions WriteOptions = new() { WriteIndented = true };

    private readonly UniGetUiIconRepository _unigetui = new();
    private readonly ExternalIconRepository _external = new();
    private readonly WinstallApprovedMappingRepository _winstallApproved = new();
    private readonly WinstallReviewCandidateGenerator _reviewGenerator = new();

    public async Task<IconSyncStats> RunAsync(IconSyncOptions options)
    {
        var catalog = await LoadCatalogAsync(options.CatalogPath);
        var catalogIds = catalog.Select(a => IconIdNormalizer.Normalize(a.Id)).Where(id => id.Length > 0).ToHashSet();

        // Tier 1 usa o catálogo original (com Id não-normalizado) porque precisa
        // consultar o index.db pelo PackageIdentifier real — ver comentário em
        // WinGetOfficialManifestRepository sobre por que a forma já normalizada
        // (minúscula, sem pontos) não serve pra essa busca.
        using var winGetOfficialRepo = new WinGetOfficialManifestRepository();
        var winGetOfficialResolved = await winGetOfficialRepo.LoadAsync(catalog, options.WinGetIndexDbPath);

        var winstallResolved = _winstallApproved.Load(options.ApprovedMappingsPath, options.WinstallDir);
        var unigetuiResolved = _unigetui.Load(options.UniGetUiDir);
        var externalResolved = _external.Load(options.ExternalDir);

        var final = new Dictionary<string, string>();
        int fromWinGetOfficial = 0, fromWinstall = 0, fromUniGetUi = 0, fromExternal = 0;

        foreach (var id in catalogIds)
        {
            if (winGetOfficialResolved.TryGetValue(id, out var winGetOfficialUrl))
            {
                final[id] = winGetOfficialUrl;
                fromWinGetOfficial++;
            }
            else if (winstallResolved.TryGetValue(id, out var winstallUrl))
            {
                final[id] = winstallUrl;
                fromWinstall++;
            }
            else if (externalResolved.TryGetValue(id, out var externalUrl))
            {
                final[id] = externalUrl;
                fromExternal++;
            }
            else if (unigetuiResolved.TryGetValue(id, out var unigetuiUrl))
            {
                final[id] = unigetuiUrl;
                fromUniGetUi++;
            }
        }

        var approvedFileNames = _winstallApproved.GetApprovedFileNames(options.ApprovedMappingsPath);
        var reviewCandidates = _reviewGenerator.Generate(options.WinstallDir, approvedFileNames, catalog);

        Directory.CreateDirectory(options.OutputDir);
        await WriteJsonAsync(Path.Combine(options.OutputDir, "icons-database.json"), final);
        await WriteJsonAsync(Path.Combine(options.OutputDir, "winstall-review-candidates.json"), reviewCandidates);

        return new IconSyncStats(
            CatalogSize: catalog.Count,
            ResolvedFromWinGetManifest: fromWinGetOfficial,
            ResolvedFromWinstallApproved: fromWinstall,
            ResolvedFromUniGetUi: fromUniGetUi,
            ResolvedFromExternal: fromExternal,
            Unresolved: catalog.Count - final.Count,
            WinstallReviewCandidatesGenerated: reviewCandidates.Count);
    }

    private static async Task<List<AppEntry>> LoadCatalogAsync(string catalogPath)
    {
        string json;

        if (catalogPath.StartsWith("http", StringComparison.OrdinalIgnoreCase))
        {
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
            json = await http.GetStringAsync(catalogPath);
        }
        else
        {
            json = await File.ReadAllTextAsync(catalogPath);
        }

        return JsonSerializer.Deserialize<List<AppEntry>>(json, ReadOptions) ?? [];
    }

    private static async Task WriteJsonAsync<T>(string path, T data)
    {
        await using var stream = File.Create(path);
        await JsonSerializer.SerializeAsync(stream, data, WriteOptions);
    }
}
