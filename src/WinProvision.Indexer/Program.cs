using System.Diagnostics;
using System.Net.Http;
using System.Text.Json;
using WinProvision.Core.Models;
using WinProvision.Core.Services;
using WinProvision.Core.Services.Indexing;

if (args.Length >= 1 && args[0] == "--msstore")
{
    if (args.Length < 2)
    {
        Console.Error.WriteLine("Uso:");
        Console.Error.WriteLine("  WinProvision.Indexer --msstore <pasta-de-saida>");
        return 1;
    }

    return await RunMsStoreOnlyAsync(args[1]);
}

if (args.Length < 2)
{
    Console.Error.WriteLine("Uso:");
    Console.Error.WriteLine("  WinProvision.Indexer <caminho-manifests-winget-pkgs> <pasta-de-saida>");
    Console.Error.WriteLine("  WinProvision.Indexer --msstore <pasta-de-saida>");
    return 1;
}

string manifestsRoot = args[0];
string outputDir = args[1];
string cachePath = Path.Combine(outputDir, "metrics-cache.json");
string? githubToken = Environment.GetEnvironmentVariable("GITHUB_TOKEN");

Console.WriteLine("==================================================");
Console.WriteLine("  WinProvision Store - Engine de Curadoria WinGet");
Console.WriteLine("==================================================");

var totalTimer = Stopwatch.StartNew();
var stepTimer = Stopwatch.StartNew();

// Marca quanto tempo cada etapa levou (aparece no log do Actions) — pra saber onde
// está o gargalo sem precisar adivinhar.
void Lap(string label)
{
    Console.WriteLine($"      [tempo] {label}: {stepTimer.Elapsed.TotalSeconds:N1}s");
    stepTimer.Restart();
}

// 1. Varredura + dedup pela versão mais recente de cada pacote
Console.WriteLine("\n[1/8] Varrendo manifests do winget-pkgs...");
var scanner = new ManifestScanner();
var (bundles, scanStats) = scanner.Scan(manifestsRoot);
Console.WriteLine($"      {scanStats.VersionFoldersFound:N0} pastas de versão encontradas");
Console.WriteLine($"      {scanStats.ParseErrors:N0} manifestos com erro de parsing (ignorados)");
Console.WriteLine($"      {scanStats.PackagesAfterDedup:N0} pacotes únicos após manter só a última versão");
Console.WriteLine($"      {scanStats.FoldersParsed:N0} pastas de versão de fato lidas (só a mais recente de cada pacote)");
Lap("varredura dos manifests");

// 2. Mapeamento para AppEntry + filtro de ruído
Console.WriteLine("\n[2/8] Aplicando filtro de ruído...");
var noiseFilter = new NoiseFilter(LoadNoiseRules());
var candidates = new List<AppEntry>();
var installerUrlsByAppId = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
// Bundle bruto de cada pacote que passou pelo filtro de ruído. O passo [8/8] lê daqui o
// InstallerManifest (hash, switches, tipo) sem reparsear YAML.
var bundlesByAppId = new Dictionary<string, RawManifestBundle>(StringComparer.OrdinalIgnoreCase);
int discarded = 0;

foreach (var bundle in bundles)
{
    var app = ManifestMapper.ToAppEntry(bundle);
    var noiseResult = noiseFilter.Evaluate(app.Id, app.Name, app.Publisher, app.Tags);

    if (noiseResult.IsNoise)
    {
        discarded++;
        continue;
    }

    candidates.Add(app);
    // Guardado à parte (não persistido em AppEntry): usado só pelo passo 6 abaixo,
    // pra evitar reabrir/reparsear os YAMLs quando formos estimar o tamanho do
    // instalador dos pacotes que sobreviverem ao corte de score.
    installerUrlsByAppId[app.Id] = ManifestMapper.GetInstallerUrls(bundle);
    bundlesByAppId[app.Id] = bundle;
}

Console.WriteLine($"      {discarded:N0} pacotes descartados como ruído");
Console.WriteLine($"      {candidates.Count:N0} pacotes seguem para enriquecimento");
Lap("filtro de ruído");

// [+] Apps curados da Microsoft Store (source "msstore"). Não roda a consulta à
// Display Catalog aqui — isso fica no workflow separado "Update msstore-catalog.json"
// (mais leve, cadência própria, ver RunMsStoreOnlyAsync abaixo), que publica o
// resultado em Store/Database/msstore-catalog.json no R2. Este passo só baixa esse
// arquivo já pronto e mescla em "candidates", pra não acoplar a disponibilidade da
// Display Catalog ao scan pesado do winget-pkgs (que roda todo dia). Curadoria manual
// já cumpre o papel do NoiseFilter aqui; segue pra classificação regional e corte por
// score como qualquer outro pacote.
Console.WriteLine("\n[+] Baixando apps curados da Microsoft Store (R2)...");
var msstoreApps = await DownloadMsStoreCatalogAsync();
candidates.AddRange(msstoreApps);
Console.WriteLine($"      {msstoreApps.Count:N0} apps da Microsoft Store mesclados");
Lap("catálogo msstore");

// 3. Classificação regional
Console.WriteLine("\n[3/8] Classificando apelo regional...");
var regionalClassifier = new RegionalClassifier();
foreach (var app in candidates)
{
    app.RegionTags = regionalClassifier.Classify(app.Id, app.Publisher, app.Homepage, app.Description, app.Tags);
}
Console.WriteLine($"      {candidates.Count(a => a.RegionTags.Count > 0):N0} pacotes com tag regional");

// 4. Enriquecimento via API do GitHub (com cache em disco) + cálculo do score
Console.WriteLine("\n[4/8] Consultando métricas do GitHub (stars/forks/atividade)...");
var existingCache = LoadMetricsCache(cachePath);
var githubService = new GitHubMetricsService(githubToken, existingCache);
var scoringWeights = LoadScoringWeights();
var scoringEngine = new ScoringEngine(scoringWeights);

// O GitHubMetricsService já limita a concorrência internamente (SemaphoreSlim de 5),
// mas isso só faz efeito se as chamadas forem disparadas em paralelo. Um "foreach"
// sequencial aqui reduzia isso a 1 requisição por vez, multiplicando o tempo total
// da pipeline por 5 sem necessidade — era o gargalo real do pipeline.
int withRepo = 0;
await Parallel.ForEachAsync(
    candidates,
    new ParallelOptions { MaxDegreeOfParallelism = 5 },
    async (app, ct) =>
    {
        string? repoSlug = GitHubMetricsService.ExtractRepoSlug(app.Homepage, app.PackageUrl, app.PublisherUrl);
        GitHubRepoMetrics? metrics = null;

        if (repoSlug != null)
        {
            Interlocked.Increment(ref withRepo);
            metrics = await githubService.GetMetricsAsync(repoSlug);
        }

        app.HasGitHubMetrics = metrics != null;
        app.GitHubStars = metrics?.Stars;
        app.Score = scoringEngine.Compute(app, metrics);
    });

Console.WriteLine($"      {withRepo:N0} pacotes com repositório GitHub identificado");
Console.WriteLine($"      {githubService.RequestsMade:N0} requisições feitas à API do GitHub nesta execução");
if (githubService.RateLimitHit)
{
    Console.WriteLine("      [AVISO] Rate limit da API do GitHub atingido - o restante usou cache/score neutro.");
}

await SaveMetricsCacheAsync(cachePath, githubService.ExportCache());
Lap("métricas do GitHub");

// 5. Corte final por score mínimo
Console.WriteLine("\n[5/8] Aplicando corte de score mínimo...");
var published = candidates.Where(a => a.Score >= scoringWeights.MinimumScoreThreshold).ToList();
int cutByScore = candidates.Count - published.Count;
Console.WriteLine($"      {cutByScore:N0} pacotes descartados por score < {scoringWeights.MinimumScoreThreshold}");
Console.WriteLine($"      {published.Count:N0} pacotes seguem para o catálogo final");

// 6. Estimativa de tamanho do instalador (HTTP HEAD/Range contra a InstallerUrl do
// manifesto — não existe um campo "InstallerSize" no schema do winget-pkgs, então
// essa é a única fonte confiável; ver InstallerSizeResolver). Feito só sobre os
// pacotes que sobreviveram ao corte de score (published), não sobre os ~10x mais
// candidatos descartados — é o que mantém essa etapa rápida o suficiente para rodar
// diariamente. O resultado vai para AppEntry.InstallerSizeBytes e é persistido no
// apps.json, então o app cliente (WinProvision.Store) não precisa mais rodar
// "winget show" nem HEAD/Range ao vivo pra maioria dos pacotes — só como fallback
// para os que não resolverem aqui.
Console.WriteLine("\n[6/8] Estimando tamanho dos instaladores (HTTP HEAD/Range)...");
// Reaproveita o tamanho do catálogo anterior (apps.previous.json, baixado do R2 pelo
// workflow, no mesmo estilo do metrics-cache.json): se o Id e a Version são os mesmos,
// o instalador é o mesmo e não precisa de HEAD/Range de novo. Na prática só os pacotes
// novos ou atualizados desde a última rodada fazem requisição de rede.
var previousApps = LoadPreviousApps(Path.Combine(outputDir, "apps.previous.json"));
Console.WriteLine($"      {previousApps.Count:N0} apps do catálogo anterior disponíveis para reaproveitar tamanhos");

int sizeResolved = 0;
int sizeReused = 0;
await Parallel.ForEachAsync(
    published,
    new ParallelOptions { MaxDegreeOfParallelism = 16 },
    async (app, ct) =>
    {
        if (previousApps.TryGetValue(app.Id, out var previous)
            && previous.InstallerSizeBytes is > 0
            && string.Equals(previous.Version, app.Version, StringComparison.Ordinal))
        {
            app.InstallerSizeBytes = previous.InstallerSizeBytes;
            Interlocked.Increment(ref sizeResolved);
            Interlocked.Increment(ref sizeReused);
            return;
        }

        if (!installerUrlsByAppId.TryGetValue(app.Id, out var urls) || urls.Count == 0)
            return;

        foreach (string url in urls)
        {
            long? size = await InstallerSizeResolver.TryGetRemoteContentLengthAsync(url, ct);
            if (size is > 0)
            {
                app.InstallerSizeBytes = size;
                Interlocked.Increment(ref sizeResolved);
                return;
            }
        }
    });

Console.WriteLine($"      {sizeResolved:N0} de {published.Count:N0} pacotes com tamanho estimado ({(published.Count == 0 ? 0 : sizeResolved * 100.0 / published.Count):N1}%)");
Console.WriteLine($"      {sizeReused:N0} reaproveitados do catálogo anterior, {sizeResolved - sizeReused:N0} consultados na rede");
Lap("tamanhos dos instaladores");

// 7. Exportação do catálogo
Console.WriteLine("\n[7/8] Exportando catálogo...");
var exporter = new CatalogExporter();
await exporter.ExportAsync(published, outputDir);
Lap("exportação");

// 8. Exportação da API de instaladores (index.json + packages/<id>.json). Sai em
// <pasta-de-saida>/api, que é o diretório que o upload_api_json.py publica no R2.
Console.WriteLine("\n[8/8] Exportando API de instaladores...");
var apiExporter = new InstallerApiExporter();
var apiStats = await apiExporter.ExportAsync(published, bundlesByAppId, Path.Combine(outputDir, "api"));
Console.WriteLine($"      {apiStats.Packages:N0} pacotes e {apiStats.Installers:N0} instaladores exportados");
Console.WriteLine($"      {apiStats.InstallersWithoutSilent:N0} instaladores sem instalação silenciosa suportada (silentSupported=false)");
Console.WriteLine($"      {apiStats.SkippedNoInstaller:N0} pacotes ignorados por não terem instalador com URL, {apiStats.SkippedInvalidId:N0} por ID inválido como nome de arquivo");
Lap("exportação da API");

totalTimer.Stop();
Console.WriteLine($"\n[SUCESSO] Pipeline concluída em {totalTimer.Elapsed.TotalSeconds:N1}s. {published.Count:N0} apps publicados em '{outputDir}'.");
return 0;

static NoiseRules LoadNoiseRules() => LoadConfig("noise-rules.json", NoiseRules.Default);

static ScoringWeights LoadScoringWeights() => LoadConfig("scoring-weights.json", new ScoringWeights());

static List<string> LoadMsStoreCuratedIds() => LoadConfig("msstore-curated.json", new List<string>());

// URL pública do catálogo msstore, publicado pelo workflow separado (ver
// RunMsStoreOnlyAsync). Mesmo bucket/padrão do apps.json principal.
const string MsStoreCatalogR2Url =
    "https://pub-166b41912a994dbe86583ba10596d673.r2.dev/Store/Database/msstore-catalog.json";

static async Task<List<AppEntry>> DownloadMsStoreCatalogAsync()
{
    try
    {
        using var httpClient = new HttpClient { Timeout = TimeSpan.FromSeconds(15) };
        string json = await httpClient.GetStringAsync(MsStoreCatalogR2Url);
        return JsonSerializer.Deserialize<List<AppEntry>>(json, WinProvisionJsonOptions.Compact) ?? [];
    }
    catch (Exception ex)
    {
        // Não derruba o scan do winget-pkgs por causa disso — o catálogo msstore é um
        // extra, publicado por um workflow independente; se estiver indisponível ou
        // ainda não tiver rodado a primeira vez, o resto da pipeline segue normalmente.
        Console.WriteLine($"      [AVISO] Falha ao baixar msstore-catalog.json do R2, seguindo sem ele: {ex.Message}");
        return [];
    }
}

/// <summary>
/// Modo "--msstore": roda só a consulta à Display Catalog contra os IDs curados em
/// config/msstore-curated.json e exporta o resultado em msstore-catalog.json — sem
/// tocar no winget-pkgs. Chamado pelo workflow "Update msstore-catalog.json", separado
/// do scan diário pesado (ver comentário no passo [+] acima).
/// </summary>
static async Task<int> RunMsStoreOnlyAsync(string outputDir)
{
    Console.WriteLine("==================================================");
    Console.WriteLine("  WinProvision Store - Curadoria Microsoft Store");
    Console.WriteLine("==================================================");

    var curatedIds = LoadMsStoreCuratedIds();
    Console.WriteLine($"\n[1/2] Consultando Display Catalog para {curatedIds.Count:N0} app(s) curado(s)...");
    var apps = await new MsStoreCatalogService().FetchAsync(curatedIds);
    Console.WriteLine($"      {apps.Count:N0} de {curatedIds.Count:N0} apps resolvidos");

    Console.WriteLine("\n[2/2] Exportando msstore-catalog.json...");
    Directory.CreateDirectory(outputDir);
    string outputPath = Path.Combine(outputDir, "msstore-catalog.json");
    await using (var stream = File.Create(outputPath))
    {
        await JsonSerializer.SerializeAsync(stream, apps, WinProvisionJsonOptions.Compact);
    }
    Console.WriteLine($"      {apps.Count:N0} apps publicados em '{outputPath}'.");

    return 0;
}

static T LoadConfig<T>(string fileName, T fallback)
{
    string path = Path.Combine(AppContext.BaseDirectory, "config", fileName);
    if (!File.Exists(path)) return fallback;

    try
    {
        string json = File.ReadAllText(path);
        return JsonSerializer.Deserialize<T>(json) ?? fallback;
    }
    catch (Exception ex)
    {
        Console.WriteLine($"      [AVISO] Falha ao ler '{fileName}', usando padrão embutido: {ex.Message}");
        return fallback;
    }
}

static Dictionary<string, GitHubRepoMetrics> LoadMetricsCache(string path)
{
    if (!File.Exists(path)) return [];

    try
    {
        string json = File.ReadAllText(path);
        return JsonSerializer.Deserialize<Dictionary<string, GitHubRepoMetrics>>(json) ?? [];
    }
    catch
    {
        return [];
    }
}

static async Task SaveMetricsCacheAsync(string path, Dictionary<string, GitHubRepoMetrics> cache)
{
    Directory.CreateDirectory(Path.GetDirectoryName(path)!);
    string json = JsonSerializer.Serialize(cache);

    // Escrita atômica: se o job for cancelado/cair no meio, não deixa um JSON pela
    // metade (que LoadMetricsCache descartaria, zerando o cache inteiro).
    string tempPath = path + ".tmp";
    await File.WriteAllTextAsync(tempPath, json);
    File.Move(tempPath, path, overwrite: true);
}

static Dictionary<string, AppEntry> LoadPreviousApps(string path)
{
    var result = new Dictionary<string, AppEntry>(StringComparer.OrdinalIgnoreCase);
    if (!File.Exists(path)) return result;

    try
    {
        string json = File.ReadAllText(path);
        var apps = JsonSerializer.Deserialize<List<AppEntry>>(json, WinProvisionJsonOptions.Compact) ?? [];
        foreach (var app in apps)
        {
            if (!string.IsNullOrWhiteSpace(app.Id))
                result.TryAdd(app.Id, app);
        }
    }
    catch (Exception ex)
    {
        Console.WriteLine($"      [AVISO] Não foi possível ler o catálogo anterior, todos os tamanhos serão consultados: {ex.Message}");
        result.Clear();
    }

    return result;
}
