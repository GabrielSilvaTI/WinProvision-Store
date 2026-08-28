using System.Net.Http;
using Microsoft.Data.Sqlite;
using WinProvision.Core.Models;
using YamlDotNet.Serialization;

namespace WinProvision.Core.Services.IconSync;

/// <summary>
/// Tier 1 do <see cref="IconSyncPipeline"/>: resolve ícones a partir do manifesto
/// oficial do WinGet, hospedado na CDN da própria Microsoft
/// (cdn.winget.microsoft.com) — a mesma infraestrutura que o `winget install` já
/// usa por baixo dos panos, então não é uma nova superfície de risco de
/// terceiro (ver docs/ICON_APPROVAL.md).
///
/// Fluxo:
///   1. Para cada app do catálogo, procura o Id "com o case original" na tabela
///      `ids` do index.db (arquivo de índice SQLite publicado pelo próprio
///      WinGet), usando COLLATE NOCASE — porque o catálogo interno normaliza
///      tudo pra minúsculas e remove separadores (ex.: "google.chrome"), mas o
///      index.db é indexado pelo PackageIdentifier com o case E os separadores
///      originais (ex.: "Google.Chrome"). Buscar direto com o Id normalizado é
///      a causa mais comum de "Id não encontrado" nesse Tier — por isso a
///      busca aqui sempre usa <see cref="AppEntry.Id"/> (original), nunca a
///      forma já normalizada.
///   2. Reconstrói o CAMINHO COMPLETO do arquivo de manifesto (não só uma
///      pasta) subindo a árvore `pathparts` a partir do `manifest.pathpart`.
///      IMPORTANTE: o nó folha dessa árvore já É o nome do arquivo final na
///      CDN (algo como "a1b2-1.2.3.yaml" — hash truncado + versão, gerado pelo
///      pipeline do próprio WinGet), não uma subpasta à qual ainda falta
///      concatenar "{PackageIdentifier}.yaml". Tentar montar o nome do arquivo
///      a partir do Id é o erro mais comum aqui — a única forma correta é
///      usar literalmente o texto desse nó folha.
///   3. Baixa esse único arquivo YAML (é um manifesto já mesclado — instalador
///      + locale padrão em um arquivo só) e procura o ícone, tentando tanto o
///      campo `Icons` (lista, schema atual do winget-pkgs) quanto `Icon`
///      (campo singular, schemas mais antigos), pois o formato exato do
///      manifesto mesclado pode variar por versão do pacote.
///
/// Nem todo manifesto tem algum campo de ícone preenchido (nem todo
/// mantenedor do winget-pkgs cadastra isso — ex.: Git.Git). Isso não é erro,
/// é cobertura parcial esperada deste tier. Qualquer falha (Id não encontrado
/// no index.db, schema inesperado, manifesto sem ícone, 404 na CDN, timeout)
/// é tratada como "não resolvido" e cai silenciosamente pro próximo tier da
/// cascata (Winstall aprovado → package-icons externo → UniGetUI); nunca
/// lança exceção nem interrompe o lote — mas cada etapa registra um resumo no
/// console pra dar visibilidade de onde exatamente a resolução está parando,
/// em vez de só devolver zero sem explicação.
/// </summary>
public sealed class WinGetOfficialManifestRepository : IDisposable
{
    private const string CdnBaseUrl = "https://cdn.winget.microsoft.com/cache";

    private readonly HttpClient _http;
    private readonly bool _ownsHttpClient;
    private readonly IDeserializer _yaml;

    public WinGetOfficialManifestRepository(HttpClient? httpClient = null)
    {
        _ownsHttpClient = httpClient is null;
        _http = httpClient ?? new HttpClient { Timeout = TimeSpan.FromSeconds(15) };

        // Os manifestos do winget-pkgs usam as chaves em PascalCase exatamente como
        // os nomes das propriedades abaixo (Icons, IconUrl, Icon...), então nenhuma
        // convenção de nomes precisa ser aplicada aqui.
        _yaml = new DeserializerBuilder()
            .IgnoreUnmatchedProperties()
            .Build();
    }

    /// <summary>
    /// Resolve ícones para os apps do catálogo a partir do index.db + CDN oficial.
    /// Retorna dicionário chaveado pelo Id já normalizado
    /// (<see cref="IconIdNormalizer.Normalize"/>), no mesmo formato usado pelas
    /// outras fontes do <see cref="IconSyncPipeline"/> — a normalização acontece
    /// só na saída, depois que o Id original já foi usado pra montar a URL.
    /// </summary>
    public async Task<Dictionary<string, string>> LoadAsync(
        IReadOnlyList<AppEntry> catalog,
        string? indexDbPath,
        int maxConcurrency = 8)
    {
        var resolved = new Dictionary<string, string>();

        if (string.IsNullOrWhiteSpace(indexDbPath) || !File.Exists(indexDbPath))
        {
            // Flag --winget-index-db não foi passada, ou o arquivo ainda não foi
            // baixado. Tier opcional: a pipeline segue normalmente pros fallbacks.
            Console.WriteLine(
                "[Tier1/WinGet] --winget-index-db não informado ou arquivo não encontrado. Tier 1 pulado.");
            return resolved;
        }

        List<ManifestLookup> lookups;
        try
        {
            lookups = ResolveManifestPaths(catalog, indexDbPath);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine(
                $"[Tier1/WinGet] Não foi possível ler '{indexDbPath}' " +
                $"(schema inesperado ou arquivo corrompido): {ex.Message}. Tier 1 pulado " +
                "para este lote; a cascata segue com as fontes de fallback.");
            return resolved;
        }

        Console.WriteLine(
            $"[Tier1/WinGet] {lookups.Count:N0} de {catalog.Count:N0} Ids do catálogo encontrados no index.db.");

        if (lookups.Count == 0)
        {
            Console.WriteLine(
                "[Tier1/WinGet] Nenhum Id do catálogo bateu com o index.db. Verifique se o " +
                "index.db baixado é recente e se os Ids do catálogo (apps.json) correspondem a " +
                "PackageIdentifiers reais do winget-pkgs (ex.: \"Google.Chrome\", \"Git.Git\").");
            return resolved;
        }

        int manifestsFetched = 0, manifestsFailed = 0, withoutIcon = 0;
        using var gate = new SemaphoreSlim(maxConcurrency);
        var syncRoot = new object();

        var tasks = lookups.Select(async lookup =>
        {
            await gate.WaitAsync().ConfigureAwait(false);
            try
            {
                var (iconUrl, fetched) = await TryResolveIconUrlAsync(lookup).ConfigureAwait(false);

                lock (syncRoot)
                {
                    if (!fetched) manifestsFailed++;
                    else manifestsFetched++;

                    if (fetched && iconUrl is null) withoutIcon++;
                    if (iconUrl is not null) resolved.TryAdd(lookup.NormalizedId, iconUrl);
                }
            }
            finally
            {
                gate.Release();
            }
        });

        await Task.WhenAll(tasks).ConfigureAwait(false);

        Console.WriteLine(
            $"[Tier1/WinGet] Manifestos baixados: {manifestsFetched:N0} | " +
            $"Falha ao baixar (404/timeout/etc.): {manifestsFailed:N0} | " +
            $"Baixados sem campo de ícone: {withoutIcon:N0} | " +
            $"Ícones resolvidos: {resolved.Count:N0}.");

        return resolved;
    }

    // ---- index.db ----------------------------------------------------------

    private readonly record struct ManifestLookup(string NormalizedId, string RelativeManifestPath);

    /// <summary>
    /// Para cada Id do catálogo, busca no index.db a linha correspondente usando
    /// <c>COLLATE NOCASE</c> a partir do Id ORIGINAL (com pontos/case de
    /// exibição) — nunca a forma já normalizada pelo <see cref="IconIdNormalizer"/>,
    /// que remove justamente os separadores que a tabela `ids` preserva. Ids
    /// que não existem nesse index.db (app fora do winget-pkgs, ou index.db
    /// desatualizado) são simplesmente ignorados.
    /// </summary>
    private static List<ManifestLookup> ResolveManifestPaths(IReadOnlyList<AppEntry> catalog, string indexDbPath)
    {
        var results = new List<ManifestLookup>();

        using var connection = new SqliteConnection($"Data Source={indexDbPath};Mode=ReadOnly");
        connection.Open();

        // pathpart -> (parent, name), carregado uma vez só; a árvore tem no máximo
        // algumas dezenas de milhares de nós, cabe tranquilamente em memória e evita
        // uma query recursiva por app do catálogo.
        var pathparts = LoadPathpartsTree(connection);

        using var idCommand = connection.CreateCommand();
        idCommand.CommandText =
            """
            SELECT m.rowid, i.id, m.pathpart
            FROM manifest m
            JOIN ids i ON i.rowid = m.id
            WHERE i.id = $id COLLATE NOCASE
            ORDER BY m.rowid DESC
            LIMIT 1
            """;
        var idParam = idCommand.CreateParameter();
        idParam.ParameterName = "$id";
        idCommand.Parameters.Add(idParam);

        foreach (var app in catalog)
        {
            if (string.IsNullOrWhiteSpace(app.Id)) continue;

            string normalizedId = IconIdNormalizer.Normalize(app.Id);
            if (normalizedId.Length == 0) continue;

            idParam.Value = app.Id;

            using var reader = idCommand.ExecuteReader();
            if (!reader.Read()) continue; // Id não existe nesse index.db.

            long leafPathpart = reader.GetInt64(2);

            // O caminho reconstruído aqui já é o ARQUIVO completo (ex.:
            // "RubyInstallerTeam/Ruby/a1b2-2.7.2.yaml") — o nó folha da árvore
            // pathparts referenciado por manifest.pathpart já é o nome do
            // arquivo gerado pelo próprio pipeline do WinGet, não uma pasta.
            string? relativeManifestPath = BuildRelativePath(pathparts, leafPathpart);
            if (relativeManifestPath is null) continue;

            results.Add(new ManifestLookup(normalizedId, relativeManifestPath));
        }

        return results;
    }

    private static Dictionary<long, (long Parent, string Name)> LoadPathpartsTree(SqliteConnection connection)
    {
        var tree = new Dictionary<long, (long Parent, string Name)>();

        using var command = connection.CreateCommand();
        command.CommandText = "SELECT rowid, parent, pathpart FROM pathparts";
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            long rowid = reader.GetInt64(0);
            long parent = reader.IsDBNull(1) ? 0 : reader.GetInt64(1);
            string name = reader.GetString(2);
            tree[rowid] = (parent, name);
        }

        return tree;
    }

    /// <summary>
    /// Sobe a árvore de `pathparts` a partir da folha (que já é o nome do
    /// arquivo) até a raiz (parent == 0 ou ausente), acumulando os nomes, e
    /// devolve o caminho já na ordem correta (raiz -> .../arquivo.yaml).
    /// `guard` evita loop infinito caso o index.db venha com uma árvore
    /// corrompida/cíclica — nesse caso trata como "não resolvido" em vez de
    /// travar a pipeline.
    /// </summary>
    private static string? BuildRelativePath(Dictionary<long, (long Parent, string Name)> tree, long leafId)
    {
        var segments = new List<string>();
        long current = leafId;
        int guard = 0;

        while (tree.TryGetValue(current, out var node) && guard++ < 64)
        {
            segments.Add(node.Name);
            if (node.Parent == 0) break;
            current = node.Parent;
        }

        if (segments.Count == 0) return null;

        segments.Reverse();
        return string.Join('/', segments);
    }

    // ---- CDN + manifesto YAML -----------------------------------------------

    /// <summary>
    /// Retorna a URL do ícone (ou null se o manifesto não tiver um) e um flag
    /// indicando se o manifesto em si foi baixado com sucesso — isso permite
    /// ao chamador diferenciar "404 / falha de rede" de "baixou certinho mas
    /// não tem campo de ícone", que são situações bem diferentes pra debugar.
    /// </summary>
    private async Task<(string? IconUrl, bool Fetched)> TryResolveIconUrlAsync(ManifestLookup lookup)
    {
        try
        {
            string manifestUrl = $"{CdnBaseUrl}/{lookup.RelativeManifestPath}";
            string? yaml = await FetchStringOrNullAsync(manifestUrl).ConfigureAwait(false);
            if (yaml is null) return (null, false);

            var manifest = _yaml.Deserialize<MergedManifestYaml>(yaml);

            string? iconUrl = manifest?.Icons?
                .Select(icon => icon.IconUrl)
                .FirstOrDefault(url => !string.IsNullOrWhiteSpace(url));

            // Fallback pra schemas mais antigos que usavam campo singular `Icon`
            // em vez da lista `Icons`.
            iconUrl ??= !string.IsNullOrWhiteSpace(manifest?.Icon) ? manifest!.Icon : null;

            // null aqui é esperado e normal: manifesto sem nenhum campo de
            // ícone (ex.: Git.Git) — cobertura parcial de Tier 1, não é falha.
            return (iconUrl, true);
        }
        catch
        {
            // Timeout, YAML malformado, etc. — não interrompe o batch, só não
            // resolve esse app específico neste tier.
            return (null, false);
        }
    }

    private async Task<string?> FetchStringOrNullAsync(string url)
    {
        using var response = await _http.GetAsync(url).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode) return null; // inclui 404 esperado.
        return await response.Content.ReadAsStringAsync().ConfigureAwait(false);
    }

    private sealed class MergedManifestYaml
    {
        public List<IconEntryYaml>? Icons { get; set; }
        public string? Icon { get; set; }
    }

    private sealed class IconEntryYaml
    {
        public string? IconUrl { get; set; }
    }

    public void Dispose()
    {
        if (_ownsHttpClient) _http.Dispose();
    }
}
