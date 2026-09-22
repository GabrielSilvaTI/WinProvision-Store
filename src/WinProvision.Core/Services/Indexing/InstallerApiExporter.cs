using System.Text.Json;
using System.Text.Json.Serialization;
using WinProvision.Core.Models;

namespace WinProvision.Core.Services.Indexing;

/// <param name="Packages">Pacotes gravados em packages/.</param>
/// <param name="Installers">Total de instaladores dentro desses pacotes.</param>
/// <param name="InstallersWithoutSilent">Instaladores com silentSupported=false (msix, zip, portable, exe genérico sem switch etc.).</param>
/// <param name="SkippedNoInstaller">Pacotes sem nenhum instalador com URL no manifesto.</param>
/// <param name="SkippedInvalidId">Pacotes cujo ID não serve como nome de arquivo.</param>
public record ApiExportStats(
    int Packages,
    int Installers,
    int InstallersWithoutSilent,
    int SkippedNoInstaller,
    int SkippedInvalidId);

/// <summary>
/// Gera a API estática de instaladores consumida pelo Worker da Cloudflare:
///
///   api/index.json                 lista leve (id, versão, arquiteturas)
///   api/packages/&lt;id&gt;.json    todos os instaladores normalizados do pacote
///
/// Os arquivos são artefatos de pipeline (publicados no R2 por upload_api_json.py,
/// que só reenvia os packages/*.json cujo sha256 mudou). Por isso o conteúdo de cada
/// packages/*.json é DETERMINÍSTICO: sem data/hora de geração e com instaladores em
/// ordem fixa. O generatedAt fica só no index.json, que é sempre reenviado.
///
/// Cobre apenas os pacotes do catálogo publicado (mesmo corte do apps.json) com
/// source "winget". Usa a mesma <see cref="WinProvisionJsonOptions"/> do
/// <see cref="CatalogExporter"/>; campos nulos são omitidos do JSON.
/// </summary>
public class InstallerApiExporter
{
    private const int SchemaVersion = 1;

    private static readonly JsonSerializerOptions JsonOptions = WinProvisionJsonOptions.Compact;

    // Tipos que a API trata como EXE/MSI. Fora disso (msix, appx, portable, pwa...) o
    // instalador é exportado, mas com silentSupported=false. "zip" não entra aqui de
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

    private static readonly char[] InvalidFileNameChars = ['\\', '/', ':', '*', '?', '"', '<', '>', '|', '\0'];

    public async Task<ApiExportStats> ExportAsync(
        IEnumerable<AppEntry> apps,
        IReadOnlyDictionary<string, RawManifestBundle> bundlesByAppId,
        string apiDir)
    {
        // A pasta é recriada do zero para não sobrar pacote de uma execução antiga.
        if (Directory.Exists(apiDir))
            Directory.Delete(apiDir, recursive: true);

        string packagesDir = Path.Combine(apiDir, "packages");
        Directory.CreateDirectory(packagesDir);

        var indexItems = new List<ApiIndexItem>();
        int installerCount = 0;
        int withoutSilent = 0;
        int skippedNoInstaller = 0;
        int skippedInvalidId = 0;

        foreach (var app in apps.OrderBy(a => a.Id, StringComparer.OrdinalIgnoreCase))
        {
            // Apps "msstore" não têm manifesto de instalador do winget-pkgs.
            if (!string.Equals(app.Source, "winget", StringComparison.OrdinalIgnoreCase))
                continue;

            if (!bundlesByAppId.TryGetValue(app.Id, out var bundle))
                continue;

            if (!IsSafeFileName(app.Id))
            {
                skippedInvalidId++;
                continue;
            }

            var installers = BuildInstallers(bundle);
            if (installers.Count == 0)
            {
                skippedNoInstaller++;
                continue;
            }

            var package = new ApiPackage
            {
                Schema = SchemaVersion,
                Id = app.Id,
                Version = app.Version,
                Installers = installers
            };

            await WriteAsync(Path.Combine(packagesDir, app.Id + ".json"), package);

            installerCount += installers.Count;
            withoutSilent += installers.Count(i => !i.SilentSupported);

            indexItems.Add(new ApiIndexItem
            {
                Id = app.Id,
                Version = app.Version,
                Architectures = installers
                    .Select(i => i.Architecture)
                    .Where(a => !string.IsNullOrWhiteSpace(a))
                    .Select(a => a!)
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .OrderBy(a => a, StringComparer.Ordinal)
                    .ToList()
            });
        }

        var index = new ApiIndex
        {
            Schema = SchemaVersion,
            GeneratedAt = DateTime.UtcNow.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'"),
            Count = indexItems.Count,
            Packages = indexItems
        };

        await WriteAsync(Path.Combine(apiDir, "index.json"), index);

        return new ApiExportStats(indexItems.Count, installerCount, withoutSilent, skippedNoInstaller, skippedInvalidId);
    }

    /// <summary>
    /// Um item por entrada de "Installers". Campos da raiz do manifesto valem como padrão
    /// e o item sobrescreve (InstallerSwitches é mesclado chave a chave).
    /// </summary>
    private static List<ApiInstaller> BuildInstallers(RawManifestBundle bundle)
    {
        var root = bundle.InstallerManifest;
        if (root is null)
            return [];

        var rootSwitches = ReadSwitches(root);
        var rootModes = root.GetStringList("InstallModes");
        var result = new List<ApiInstaller>();

        foreach (var item in root.GetObjectList("Installers"))
        {
            string? url = Clean(Pick(item, root, "InstallerUrl"));
            if (url is null)
                continue;

            string? type = Clean(Pick(item, root, "InstallerType"))?.ToLowerInvariant();

            // "zip" não é um instalador em si: o executável/MSI de verdade vem dentro do
            // pacote (winget-pkgs documenta isso via NestedInstallerType/NestedInstallerFiles).
            // Resolvemos aqui o tipo e o caminho relativo de dentro do zip para o cliente
            // saber o que extrair e rodar, sem precisar reparsear o manifesto.
            string? nestedType = null;
            string? nestedRelativePath = null;
            if (string.Equals(type, "zip", StringComparison.OrdinalIgnoreCase))
            {
                nestedType = Clean(Pick(item, root, "NestedInstallerType"))?.ToLowerInvariant();
                var nestedFiles = item.ContainsKey("NestedInstallerFiles")
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

            // Um zip sem NestedInstallerType/NestedInstallerFiles resolvíveis não tem o
            // que o cliente extraia e rode — mesmo que o tipo aninhado fosse suportado,
            // sem o caminho do arquivo não dá pra montar o comando.
            bool hasUsableNestedFile = !string.Equals(type, "zip", StringComparison.OrdinalIgnoreCase)
                || !string.IsNullOrEmpty(nestedRelativePath);
            bool supported = silent.Supported && hasUsableNestedFile;

            result.Add(new ApiInstaller
            {
                Architecture = Clean(Pick(item, root, "Architecture"))?.ToLowerInvariant(),
                Type = type,
                NestedType = nestedType,
                NestedInstallerFile = nestedRelativePath,
                Scope = Clean(Pick(item, root, "Scope"))?.ToLowerInvariant(),
                Locale = Clean(Pick(item, root, "InstallerLocale")),
                Url = url,
                Sha256 = Clean(Pick(item, root, "InstallerSha256"))?.ToUpperInvariant(),
                SilentArgs = supported ? silent.Args : null,
                SilentSource = supported ? silent.Source : "none",
                SilentSupported = supported,
                ProductCode = Clean(Pick(item, root, "ProductCode"))
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

    /// <summary>
    /// Ordem: InstallerSwitches.Silent do manifesto → SilentWithProgress do manifesto →
    /// padrão do winget para o InstallerType. "Custom" é anexado ao final quando existe.
    /// </summary>
    private static (string? Args, string Source, bool Supported) ResolveSilent(
        string? type,
        Dictionary<string, string> switches,
        List<string> modes)
    {
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

    private static bool IsSafeFileName(string id)
        => !string.IsNullOrWhiteSpace(id) && id.IndexOfAny(InvalidFileNameChars) < 0;

    private static async Task WriteAsync<T>(string path, T data)
    {
        await using var stream = File.Create(path);
        await JsonSerializer.SerializeAsync(stream, data, JsonOptions);
    }
}

public class ApiIndex
{
    [JsonPropertyName("schema")] public int Schema { get; set; }
    [JsonPropertyName("generatedAt")] public string GeneratedAt { get; set; } = string.Empty;
    [JsonPropertyName("count")] public int Count { get; set; }
    [JsonPropertyName("packages")] public List<ApiIndexItem> Packages { get; set; } = [];
}

public class ApiIndexItem
{
    [JsonPropertyName("id")] public string Id { get; set; } = string.Empty;
    [JsonPropertyName("version")] public string Version { get; set; } = string.Empty;
    [JsonPropertyName("architectures")] public List<string> Architectures { get; set; } = [];
}

public class ApiPackage
{
    [JsonPropertyName("schema")] public int Schema { get; set; }
    [JsonPropertyName("id")] public string Id { get; set; } = string.Empty;
    [JsonPropertyName("version")] public string Version { get; set; } = string.Empty;
    [JsonPropertyName("installers")] public List<ApiInstaller> Installers { get; set; } = [];
}

public class ApiInstaller
{
    [JsonPropertyName("architecture")] public string? Architecture { get; set; }
    /// <summary>Tipo do instalador publicado (msi, wix, burn, nullsoft, inno, exe, zip, msix...).</summary>
    [JsonPropertyName("type")] public string? Type { get; set; }
    /// <summary>Só preenchido quando <see cref="Type"/> é "zip": o InstallerType real de dentro do pacote.</summary>
    [JsonPropertyName("nestedType")] public string? NestedType { get; set; }
    /// <summary>Só preenchido quando <see cref="Type"/> é "zip": caminho relativo, dentro do zip, do instalador a extrair e rodar.</summary>
    [JsonPropertyName("nestedInstallerFile")] public string? NestedInstallerFile { get; set; }
    [JsonPropertyName("scope")] public string? Scope { get; set; }
    [JsonPropertyName("locale")] public string? Locale { get; set; }
    [JsonPropertyName("url")] public string Url { get; set; } = string.Empty;
    [JsonPropertyName("sha256")] public string? Sha256 { get; set; }
    [JsonPropertyName("silentArgs")] public string? SilentArgs { get; set; }
    /// <summary>"manifest", "default" ou "none" (sem instalação silenciosa suportada).</summary>
    [JsonPropertyName("silentSource")] public string SilentSource { get; set; } = "none";
    [JsonPropertyName("silentSupported")] public bool SilentSupported { get; set; }
    [JsonPropertyName("productCode")] public string? ProductCode { get; set; }
}
