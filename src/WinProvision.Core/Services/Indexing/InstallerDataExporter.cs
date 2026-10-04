using WinProvision.Core.Models;

namespace WinProvision.Core.Services.Indexing;

/// <param name="Packages">Apps WinGet com pelo menos um instalador HTTPS.</param>
/// <param name="Installers">Total de instaladores dentro desses pacotes.</param>
/// <param name="InstallersWithoutSilent">Instaladores com silentSupported=false (msix, zip, portable, exe genérico sem switch etc.).</param>
/// <param name="SkippedNoInstaller">Pacotes sem nenhum instalador com URL no manifesto.</param>
/// <param name="SkippedInsecureInstallerUrls">Instaladores descartados por não usarem URL HTTPS absoluta.</param>
public record InstallerExportStats(
    int Packages,
    int Installers,
    int InstallersWithoutSilent,
    int SkippedNoInstaller,
    int SkippedInsecureInstallerUrls);

/// <summary>
/// Acrescenta os dados dos instaladores diretamente aos apps antes de exportar o catálogo.
/// </summary>
public class InstallerDataExporter
{
    // Tipos com suporte silencioso conhecido. Outros formatos (msix, appx, portable...)
    // permanecem no JSON com silentSupported=false. "zip" não entra aqui de
    // propósito: é resolvido à parte em BuildInstallers via NestedInstallerType (o tipo
    // real do instalador de dentro do pacote), que é o valor efetivamente checado contra
    // este conjunto.
    private static readonly HashSet<string> SupportedTypes = new(StringComparer.OrdinalIgnoreCase)
    {
        "msi", "wix", "burn", "nullsoft", "inno", "exe"
    };

    // Switch silencioso padrão do winget por InstallerType, usado só quando o manifesto
    // não declara InstallerSwitches.Silent. "exe" genérico não tem padrão: sem switch
    // declarado no manifesto, não inventamos um.
    private static readonly Dictionary<string, string> DefaultSilentArgs = new(StringComparer.OrdinalIgnoreCase)
    {
        ["msi"] = "/quiet /norestart",
        ["wix"] = "/quiet /norestart",
        ["burn"] = "/quiet /norestart",
        ["nullsoft"] = "/S",
        ["inno"] = "/SP- /VERYSILENT /SUPPRESSMSGBOXES /NORESTART",
    };

    public InstallerExportStats EnrichApps(
        IEnumerable<AppEntry> apps,
        IReadOnlyDictionary<string, RawManifestBundle> bundlesByAppId)
    {
        int packageCount = 0;
        int installerCount = 0;
        int withoutSilent = 0;
        int skippedNoInstaller = 0;
        int skippedInsecureInstallerUrls = 0;

        foreach (var app in apps)
        {
            if (!string.Equals(app.Source, "winget", StringComparison.OrdinalIgnoreCase)
                || !bundlesByAppId.TryGetValue(app.Id, out var bundle))
                continue;

            var installers = BuildInstallers(bundle, out int insecureInstallerUrls);
            skippedInsecureInstallerUrls += insecureInstallerUrls;
            app.Installers = installers;
            if (installers.Count == 0)
            {
                skippedNoInstaller++;
                continue;
            }

            packageCount++;
            installerCount += installers.Count;
            withoutSilent += installers.Count(i => !i.SilentSupported);
        }

        return new InstallerExportStats(packageCount, installerCount, withoutSilent, skippedNoInstaller,
            skippedInsecureInstallerUrls);
    }

    /// <summary>
    /// Um item por entrada de "Installers". Campos da raiz do manifesto valem como padrão
    /// e o item sobrescreve (InstallerSwitches é mesclado chave a chave).
    /// </summary>
    private static List<CatalogInstaller> BuildInstallers(RawManifestBundle bundle, out int insecureUrlCount)
    {
        insecureUrlCount = 0;
        var root = bundle.InstallerManifest;
        if (root is null)
            return [];

        var rootSwitches = ReadSwitches(root);
        var rootModes = root.GetStringList("InstallModes");
        var result = new List<CatalogInstaller>();

        foreach (var item in root.GetObjectList("Installers"))
        {
            string? url = Clean(Pick(item, root, "InstallerUrl"));
            if (url is null || !IsHttpsUrl(url))
            {
                if (url is not null)
                    insecureUrlCount++;
                continue;
            }

            string? type = Clean(Pick(item, root, "InstallerType"))?.ToLowerInvariant();

            // "zip" não é um instalador em si: o executável/MSI de verdade vem dentro do
            // pacote (winget-pkgs documenta isso via NestedInstallerType/NestedInstallerFiles).
            // Resolvemos aqui o tipo e o caminho relativo de dentro do zip para o cliente
            // saber o que extrair e rodar, sem precisar reparsear o manifesto.
            string? nestedType = null;
            string? nestedRelativePath = null;
            List<Dictionary<string, object?>> nestedFiles = [];
            if (string.Equals(type, "zip", StringComparison.OrdinalIgnoreCase))
            {
                nestedType = Clean(Pick(item, root, "NestedInstallerType"))?.ToLowerInvariant();
                nestedFiles = item.ContainsKey("NestedInstallerFiles")
                    ? item.GetObjectList("NestedInstallerFiles")
                    : root.GetObjectList("NestedInstallerFiles");
                // Normalmente há só uma entrada relevante (o instalador real); quando o
                // manifesto lista mais de uma, a primeira é o suficiente para o nosso uso.
                nestedRelativePath = Clean(nestedFiles.FirstOrDefault()?.GetString("RelativeFilePath"));
            }

            // Pra fins de suporte a instalação silenciosa, o tipo que importa é o de
            // dentro do zip (quando existir) — "zip" nunca está em SupportedTypes, então
            // sem isso todo zip cairia em silentSupported=false mesmo quando o conteúdo
            // é um Inno/NSIS/MSI perfeitamente silencioso.
            string? effectiveTypeForSilent = nestedType ?? type;

            var switches = new Dictionary<string, string>(rootSwitches, StringComparer.OrdinalIgnoreCase);
            foreach (var (key, value) in ReadSwitches(item))
                switches[key] = value;

            var modes = item.ContainsKey("InstallModes") ? item.GetStringList("InstallModes") : rootModes;
            var silent = ResolveSilent(effectiveTypeForSilent, switches, modes);
            var nestedInstaller = nestedFiles.FirstOrDefault(file => string.Equals(
                    Clean(file.GetString("RelativeFilePath")), nestedRelativePath, StringComparison.OrdinalIgnoreCase))
                ?? nestedFiles.FirstOrDefault();

            // Um zip sem NestedInstallerType/NestedInstallerFiles resolvíveis não tem o
            // que o cliente extraia e rode — mesmo que o tipo aninhado fosse suportado,
            // sem o caminho do arquivo não dá pra montar o comando.
            bool hasUsableNestedFile = !string.Equals(type, "zip", StringComparison.OrdinalIgnoreCase)
                || !string.IsNullOrEmpty(nestedRelativePath);
            bool supported = silent.Supported && hasUsableNestedFile;

            result.Add(new CatalogInstaller
            {
                Architecture = Clean(Pick(item, root, "Architecture"))?.ToLowerInvariant(),
                Type = type,
                NestedType = nestedType,
                NestedInstallerFile = nestedRelativePath,
                PortableCommandAlias = Clean(nestedInstaller?.GetString("PortableCommandAlias")),
                Scope = Clean(Pick(item, root, "Scope"))?.ToLowerInvariant(),
                Locale = Clean(Pick(item, root, "InstallerLocale")),
                Url = url,
                Sha256 = Clean(Pick(item, root, "InstallerSha256"))?.ToUpperInvariant(),
                SilentArgs = supported ? silent.Args : null,
                SilentSource = supported ? silent.Source : "none",
                SilentSupported = supported,
                ProductCode = Clean(Pick(item, root, "ProductCode")),
                SuccessCodes = item.GetStringList("InstallerSuccessCodes")
                    .Select(value => int.TryParse(value, System.Globalization.NumberStyles.Integer,
                        System.Globalization.CultureInfo.InvariantCulture, out int code) ? code : (int?)null)
                    .Where(code => code.HasValue)
                    .Select(code => code!.Value)
                    .Distinct()
                    .ToList()
            });
        }

        // Ordem fixa: o hash do arquivo não pode mudar só porque o YAML foi reordenado.
        return result
            .OrderBy(i => i.Architecture ?? string.Empty, StringComparer.Ordinal)
            .ThenBy(i => i.Type ?? string.Empty, StringComparer.Ordinal)
            .ThenBy(i => i.Scope ?? string.Empty, StringComparer.Ordinal)
            .ThenBy(i => i.Locale ?? string.Empty, StringComparer.Ordinal)
            .ThenBy(i => i.Url, StringComparer.Ordinal)
            .ToList();
    }

    private static bool IsHttpsUrl(string url) =>
        Uri.TryCreate(url, UriKind.Absolute, out var uri)
        && uri.Scheme.Equals(Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase)
        && !string.IsNullOrWhiteSpace(uri.Host);

    /// <summary>
    /// Ordem: InstallerSwitches.Silent do manifesto → SilentWithProgress do manifesto →
    /// padrão do winget para o InstallerType. "Custom" é anexado ao final quando existe.
    /// </summary>
    private static (string? Args, string Source, bool Supported) ResolveSilent(
        string? type,
        Dictionary<string, string> switches,
        List<string> modes)
    {
        // Portable não executa um setup: o cliente apenas extrai o pacote e cria um atalho.
        if (string.Equals(type, "portable", StringComparison.OrdinalIgnoreCase))
            return (string.Empty, "portable", true);

        if (type is null || !SupportedTypes.Contains(type))
            return (null, "none", false);

        // Manifesto que declara InstallModes sem nenhum modo silencioso.
        if (modes.Count > 0 && !modes.Any(m =>
                m.Equals("silent", StringComparison.OrdinalIgnoreCase) ||
                m.Equals("silentWithProgress", StringComparison.OrdinalIgnoreCase)))
            return (null, "none", false);

        string args;
        string source;

        if (switches.TryGetValue("Silent", out var silent))
        {
            args = silent;
            source = "manifest";
        }
        else if (switches.TryGetValue("SilentWithProgress", out var withProgress))
        {
            args = withProgress;
            source = "manifest";
        }
        else if (DefaultSilentArgs.TryGetValue(type, out var fallback))
        {
            args = fallback;
            source = "default";
        }
        else
        {
            return (null, "none", false);
        }

        if (switches.TryGetValue("Custom", out var custom))
            args = $"{args} {custom}";

        return (args, source, true);
    }

    private static Dictionary<string, string> ReadSwitches(Dictionary<string, object?> manifest)
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        if (!manifest.TryGetValue("InstallerSwitches", out var value) || value is not Dictionary<object, object> dict)
            return result;

        foreach (var kv in dict)
        {
            string? key = Clean(kv.Key?.ToString());
            string? val = Clean(kv.Value?.ToString());
            if (key is not null && val is not null)
                result[key] = val;
        }

        return result;
    }

    private static string? Pick(Dictionary<string, object?> item, Dictionary<string, object?> root, string key)
        => item.GetString(key) ?? root.GetString(key);

    private static string? Clean(string? value)
        => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

}
