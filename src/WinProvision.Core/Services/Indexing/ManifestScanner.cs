using System.Collections.Concurrent;
using System.Threading;

namespace WinProvision.Core.Services.Indexing;

/// <param name="VersionFoldersFound">Total de pastas de versão no winget-pkgs (todas as versões de todos os pacotes).</param>
/// <param name="PackagesAfterDedup">Pacotes únicos que sobraram (a versão mais recente de cada um).</param>
/// <param name="ParseErrors">Manifestos que não puderam ser parseados (só conta as pastas de fato lidas).</param>
/// <param name="FoldersParsed">Pastas de versão de fato abertas e parseadas (≈ 1 por pacote).</param>
public record ScanStats(int VersionFoldersFound, int PackagesAfterDedup, int ParseErrors, int FoldersParsed = 0);

public record RawManifestBundle(
    string PackageIdentifier,
    string PackageVersion,
    Dictionary<string, object?> LocaleManifest,
    Dictionary<string, object?>? InstallerManifest,
    string SourceFolder);

/// <summary>
/// Varre a árvore manifests/ do winget-pkgs, agrupa por PackageIdentifier e mantém
/// apenas a versão mais recente de cada pacote.
///
/// Isso é importante: o winget-pkgs guarda TODAS as versões já publicadas de cada
/// pacote (cada uma em sua própria pasta), então sem esse dedup a contagem final
/// fica bem maior que o número real de aplicativos distintos (por isso o catálogo
/// de ~14 mil registros mencionado — provavelmente estava contando pasta de versão
/// em vez de pacote único).
/// </summary>
public class ManifestScanner
{
    public (List<RawManifestBundle> Packages, ScanStats Stats) Scan(string manifestsRoot)
    {
        var versionFolders = Directory.EnumerateDirectories(manifestsRoot, "*", SearchOption.AllDirectories)
            .Where(dir => Directory.EnumerateFiles(dir, "*.yaml").Any())
            .ToList();

        // Ler e parsear YAML é o gargalo da pipeline, e o winget-pkgs guarda TODAS as
        // versões de cada pacote (hoje ~690 mil arquivos .yaml em ~174 mil pastas de
        // versão, para ~15 mil pacotes). Só a versão mais recente interessa, então ela é
        // escolhida ANTES de abrir qualquer arquivo: a pasta de versão é sempre filha da
        // pasta do pacote (manifests/<letra>/<Publisher>/<App>/<versão>/), e o nome da
        // pasta é a própria versão. Isso reduz o parse em ~12x.
        var candidatesPerPackage = versionFolders
            .GroupBy(folder => Path.GetDirectoryName(folder) ?? folder, StringComparer.OrdinalIgnoreCase)
            .Select(group => group
                .OrderByDescending(folder => Path.GetFileName(folder), VersionComparer.Instance)
                .ToList())
            .ToList();

        int parseErrors = 0;
        int foldersParsed = 0;
        var bundles = new ConcurrentBag<RawManifestBundle>();

        // I/O-bound e cada pacote é independente: paraleliza pelo número de núcleos.
        Parallel.ForEach(candidatesPerPackage, candidates =>
        {
            int errors = 0;
            int parsed = 0;
            RawManifestBundle? bundle = null;

            // Normalmente a 1a pasta (a mais recente) já resolve. Se ela estiver
            // quebrada/incompleta (sem locale, YAML inválido), cai pra versão anterior —
            // mesmo resultado prático de antes, quando todas as versões eram lidas.
            foreach (var folder in candidates)
            {
                parsed++;
                bundle = TryBuildBundle(folder, ref errors);
                if (bundle != null)
                    break;
            }

            if (bundle != null)
                bundles.Add(bundle);

            Interlocked.Add(ref parseErrors, errors);
            Interlocked.Add(ref foldersParsed, parsed);
        });

        // Rede de segurança: agrupa por PackageIdentifier (e não por pasta) e mantém a
        // maior versão, como antes. Cobre o caso raro de o mesmo ID aparecer em duas pastas.
        var latestPerPackage = bundles
            .GroupBy(b => b.PackageIdentifier, StringComparer.OrdinalIgnoreCase)
            .Select(g => g.OrderByDescending(b => b.PackageVersion, VersionComparer.Instance).First())
            .ToList();

        var stats = new ScanStats(versionFolders.Count, latestPerPackage.Count, parseErrors, foldersParsed);
        return (latestPerPackage, stats);
    }

    private static RawManifestBundle? TryBuildBundle(string folder, ref int parseErrors)
    {
        Dictionary<string, object?>? versionManifest = null;
        Dictionary<string, object?>? installerManifest = null;
        Dictionary<string, object?>? defaultLocaleManifest = null;
        Dictionary<string, object?>? ptBrLocaleManifest = null;

        foreach (var file in Directory.EnumerateFiles(folder, "*.yaml"))
        {
            var parsed = ManifestParser.TryParse(file);
            if (parsed == null)
            {
                parseErrors++;
                continue;
            }

            string manifestType = parsed.GetString("ManifestType")?.ToLowerInvariant() ?? string.Empty;

            switch (manifestType)
            {
                case "version":
                    versionManifest = parsed;
                    break;
                case "installer":
                    installerManifest = parsed;
                    break;
                case "defaultlocale":
                    defaultLocaleManifest = parsed;
                    break;
                case "singleton":
                    // Formato legado (manifesto único combinando tudo)
                    versionManifest ??= parsed;
                    installerManifest ??= parsed;
                    defaultLocaleManifest ??= parsed;
                    break;
                case "locale":
                    // A grande maioria dos manifestos "locale" (não-padrão) não é pt-BR e
                    // continua sendo ignorada de propósito. Mas quando o publicador já
                    // forneceu uma tradução oficial pt-BR (locale.pt-BR.yaml), vale a pena
                    // usá-la em vez de mostrar a descrição em inglês na loja — ver o merge
                    // com defaultLocaleManifest logo abaixo.
                    string? locale = parsed.GetString("PackageLocale");
                    if (string.Equals(locale, "pt-BR", StringComparison.OrdinalIgnoreCase))
                    {
                        ptBrLocaleManifest = parsed;
                    }
                    break;
            }
        }

        string? id = versionManifest?.GetString("PackageIdentifier") ?? installerManifest?.GetString("PackageIdentifier");
        string? version = versionManifest?.GetString("PackageVersion") ?? installerManifest?.GetString("PackageVersion");

        // Sem PackageIdentifier/Version ou sem manifesto de locale, não há o que exibir
        // de forma decente na loja — descarta.
        if (string.IsNullOrWhiteSpace(id) || string.IsNullOrWhiteSpace(version) || defaultLocaleManifest == null)
            return null;

        // Um manifesto "locale" pt-BR normalmente só sobrescreve alguns campos (ex.:
        // ShortDescription, Description, Tags) — não repete tudo que já está no
        // defaultlocale (ex.: PublisherUrl, License). Por isso mescla em vez de
        // substituir: parte do defaultlocale como base e só troca as chaves que o
        // manifesto pt-BR realmente traz.
        var effectiveLocale = defaultLocaleManifest;
        if (ptBrLocaleManifest != null)
        {
            effectiveLocale = new Dictionary<string, object?>(defaultLocaleManifest, StringComparer.OrdinalIgnoreCase);
            foreach (var (key, value) in ptBrLocaleManifest)
            {
                effectiveLocale[key] = value;
            }
        }

        return new RawManifestBundle(id, version, effectiveLocale, installerManifest, folder);
    }
}
