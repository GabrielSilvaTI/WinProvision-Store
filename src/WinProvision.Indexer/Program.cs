using System.Diagnostics;
using System.Text.Json;
using WinProvision.Core.Models;
using WinProvision.Core.Services;
using WinProvision.Core.Services.Indexing;
using WinProvision.Indexer;

// Subcomando separado: sincronização de ícones (Winstall + UniGetUI + package-icons).
// Despachado antes de tudo pra não interferir no parsing posicional do modo padrão
// abaixo, que continua funcionando sem alteração pra quem já chama
// `WinProvision.Indexer.dll <manifests> <output>` diretamente.
if (args.Length > 0 && args[0].Equals("sync-icons", StringComparison.OrdinalIgnoreCase))
{
    return await SyncIconsCommand.RunAsync(args);
}

if (args.Length < 2)
{
    Console.Error.WriteLine("Uso:");
    Console.Error.WriteLine("  WinProvision.Indexer <caminho-manifests-winget-pkgs> <pasta-de-saida>");
    Console.Error.WriteLine("  WinProvision.Indexer sync-icons --catalog <arquivo-ou-url> --winstall-dir <pasta> --external-dir <pasta> --unigetui-dir <pasta> --approved-mappings <arquivo> --output-dir <pasta>");
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

// 1. Varredura + dedup pela versão mais recente de cada pacote
Console.WriteLine("\n[1/7] Varrendo manifests do winget-pkgs...");
var scanner = new ManifestScanner();
var (bundles, scanStats) = scanner.Scan(manifestsRoot);
Console.WriteLine($"      {scanStats.VersionFoldersFound:N0} pastas de versão encontradas");
Console.WriteLine($"      {scanStats.ParseErrors:N0} manifestos com erro de parsing (ignorados)");
Console.WriteLine($"      {scanStats.PackagesAfterDedup:N0} pacotes únicos após manter só a última versão");

// 2. Mapeamento para AppEntry + filtro de ruído
Console.WriteLine("\n[2/7] Aplicando filtro de ruído...");
var noiseFilter = new NoiseFilter(LoadNoiseRules());
var candidates = new List<AppEntry>();
var installerUrlsByAppId = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
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
}

Console.WriteLine($"      {discarded:N0} pacotes descartados como ruído");
Console.WriteLine($"      {candidates.Count:N0} pacotes seguem para enriquecimento");

// 3. Classificação regional
Console.WriteLine("\n[3/7] Classificando apelo regional...");
var regionalClassifier = new RegionalClassifier();
foreach (var app in candidates)
{
    app.RegionTags = regionalClassifier.Classify(app.Id, app.Publisher, app.Homepage, app.Description, app.Tags);
}
Console.WriteLine($"      {candidates.Count(a => a.RegionTags.Count > 0):N0} pacotes com tag regional");

// 4. Enriquecimento via API do GitHub (com cache em disco) + cálculo do score
Console.WriteLine("\n[4/7] Consultando métricas do GitHub (stars/forks/atividade)...");
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

// 5. Corte final por score mínimo
Console.WriteLine("\n[5/7] Aplicando corte de score mínimo...");
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
Console.WriteLine("\n[6/7] Estimando tamanho dos instaladores (HTTP HEAD/Range)...");
int sizeResolved = 0;
await Parallel.ForEachAsync(
    published,
    new ParallelOptions { MaxDegreeOfParallelism = 8 },
    async (app, ct) =>
    {
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

// 7. Exportação dos JSONs segmentados
Console.WriteLine("\n[7/7] Exportando catálogo...");
var exporter = new CatalogExporter();
await exporter.ExportAsync(published, outputDir);

totalTimer.Stop();
Console.WriteLine($"\n[SUCESSO] Pipeline concluída em {totalTimer.Elapsed.TotalSeconds:N1}s. {published.Count:N0} apps publicados em '{outputDir}'.");
return 0;

static NoiseRules LoadNoiseRules() => LoadConfig("noise-rules.json", NoiseRules.Default);

static ScoringWeights LoadScoringWeights() => LoadConfig("scoring-weights.json", new ScoringWeights());

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
    await File.WriteAllTextAsync(path, json);
}
