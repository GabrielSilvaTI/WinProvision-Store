using System.Collections.Concurrent;
using System.Threading;

namespace WinProvision.Core.Services.Indexing;

public record ScanStats(int VersionFoldersFound, int PackagesAfterDedup, int ParseErrors);

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

        int parseErrors = 0;
        var bundles = new ConcurrentBag<RawManifestBundle>();

        // O winget-pkgs tem centenas de milhares de pastas de versão; ler e parsear
        // YAML uma pasta por vez em série é o principal gargalo da pipeline. É I/O-bound
        // e cada pasta é independente, então paraleliza pelo número de núcleos do runner.
        Parallel.ForEach(versionFolders, () => 0, (folder, _, localErrors) =>
        {
            var bundle = TryBuildBundle(folder, ref localErrors);
            if (bundle != null)
                bundles.Add(bundle);
            return localErrors;
        },
        localErrors => Interlocked.Add(ref parseErrors, localErrors));

        var latestPerPackage = bundles
            .GroupBy(b => b.PackageIdentifier, StringComparer.OrdinalIgnoreCase)
            .Select(g => g.OrderByDescending(b => b.PackageVersion, VersionComparer.Instance).First())
            .ToList();

        var stats = new ScanStats(versionFolders.Count, latestPerPackage.Count, parseErrors);
        return (latestPerPackage, stats);
    }

    private static RawManifestBundle? TryBuildBundle(string folder, ref int parseErrors)
    {
        Dictionary<string, object?>? versionManifest = null;
        Dictionary<string, object?>? installerManifest = null;
        Dictionary<string, object?>? defaultLocaleManifest = null;

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
                // "locale" (não-padrão) é ignorado de propósito: só exibimos o idioma
                // padrão do pacote na loja.
            }
        }

        string? id = versionManifest?.GetString("PackageIdentifier") ?? installerManifest?.GetString("PackageIdentifier");
        string? version = versionManifest?.GetString("PackageVersion") ?? installerManifest?.GetString("PackageVersion");

        // Sem PackageIdentifier/Version ou sem manifesto de locale, não há o que exibir
        // de forma decente na loja — descarta.
        if (string.IsNullOrWhiteSpace(id) || string.IsNullOrWhiteSpace(version) || defaultLocaleManifest == null)
            return null;

        return new RawManifestBundle(id, version, defaultLocaleManifest, installerManifest, folder);
    }
}
