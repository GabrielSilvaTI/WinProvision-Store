using System.Linq;
using System.Text.Json;

namespace WinProvision.Core.Services.IconSync;

/// <summary>
/// Carrega os JSONs "screenshot-database" do UniGetUI (Devolutions/UniGetUI). O
/// projeto manteve esse arquivo em múltiplos branches/variantes ao longo do tempo
/// (main, app-icons, app-icon-badges; cada um em v1 e v2 de schema) e nem todo
/// branch necessariamente existe/está atualizado a qualquer momento — o passo do
/// workflow que baixa esses arquivos já tolera falha individual por branch.
///
/// Aqui carregamos todos os que existirem, na ordem de prioridade abaixo (primeiro
/// que resolver um Id vence; os demais só preenchem lacunas que sobrarem):
///
///   main-v2 > main-v1 > app-icons-v2 > app-icons-v1 > app-icon-badges-v2 > app-icon-badges-v1
///
/// O schema já variou entre versões/branches, então a extração de campo tenta
/// várias chaves conhecidas em vez de assumir uma única forma fixa.
/// </summary>
public class UniGetUiIconRepository
{
    private static readonly string[] PriorityOrder =
    [
        "main-v2", "main-v1",
        "app-icons-v2", "app-icons-v1",
        "app-icon-badges-v2", "app-icon-badges-v1"
    ];

    private static readonly string[] IconFieldCandidates =
        ["icon", "Icon", "iconUrl", "IconUrl", "icon_url"];

    private static readonly string[] IdFieldCandidates =
        ["id", "Id", "packageId", "PackageId", "packageIdentifier", "PackageIdentifier"];

    public Dictionary<string, string> Load(string unigetuiDir)
    {
        var resolved = new Dictionary<string, string>();
        if (!Directory.Exists(unigetuiDir)) return resolved;

        foreach (var key in PriorityOrder)
        {
            string path = Path.Combine(unigetuiDir, $"{key}.json");
            if (!File.Exists(path)) continue;

            foreach (var (normalizedId, iconUrl) in ParseFile(path))
            {
                resolved.TryAdd(normalizedId, iconUrl);
            }
        }

        return resolved;
    }

    // Chaves de "metadados" já observadas na raiz do JSON que NUNCA são, elas
    // próprias, um pacote — servem só para não confundir contadores/resumo com
    // uma entrada de ícone de verdade.
    private static readonly string[] NonPackageContainerKeys =
        ["package_count", "packages_with_icon", "packages_with_screenshot"];

    private static IEnumerable<(string NormalizedId, string IconUrl)> ParseFile(string path)
    {
        JsonDocument? doc = null;
        try
        {
            doc = JsonDocument.Parse(File.ReadAllText(path));
        }
        catch
        {
            // Arquivo baixado mas corrompido/vazio (branch removido, curl parcial etc.) —
            // ignora essa fonte pontual, não derruba a pipeline inteira por isso.
        }

        if (doc == null) yield break;

        using (doc)
        {
            var root = doc.RootElement;

            if (root.ValueKind == JsonValueKind.Object)
            {
                foreach (var (normalizedId, iconUrl) in ParseObject(root))
                    yield return (normalizedId, iconUrl);
            }
            else if (root.ValueKind == JsonValueKind.Array)
            {
                foreach (var entry in root.EnumerateArray())
                {
                    string? id = ExtractField(entry, IdFieldCandidates);
                    string? icon = ExtractField(entry, IconFieldCandidates);
                    if (id == null || icon == null) continue;

                    yield return (IconIdNormalizer.Normalize(id), icon);
                }
            }
        }
    }

    /// <summary>
    /// O objeto raiz do "screenshot-database" NUNCA é, ele mesmo, o dicionário
    /// {packageId: {icon, images}} — o dicionário real de pacotes vem sempre
    /// aninhado dentro de uma chave, e essa chave varia por schema:
    ///
    ///   - v1 (screenshot-database.json): raiz tem "package_count" (resumo,
    ///     ignorado) e uma chave por gerenciador de pacote de origem
    ///     ("winget", "scoop", "chocolatey"...), cada uma já sendo o
    ///     dicionário plano de pacotes daquela fonte.
    ///   - v2 (screenshot-database-v2.json): raiz tem "package_count" e uma
    ///     única chave "icons_and_screenshots" com o dicionário plano de
    ///     todos os pacotes já mesclados.
    ///
    /// Tratar a raiz como se já fosse esse dicionário plano (o que o parser
    /// fazia antes) faz cada propriedade de nível raiz ser tratada como um
    /// "PackageIdentifier" (ex.: "winget", "package_count",
    /// "icons_and_screenshots"), nenhuma das quais tem campo "icon" — então
    /// TODA entrada real do arquivo era silenciosamente descartada e Load()
    /// sempre voltava vazio, apesar do arquivo baixar certinho e ter milhares
    /// de pacotes válidos.
    /// </summary>
    private static IEnumerable<(string NormalizedId, string IconUrl)> ParseObject(JsonElement root)
    {
        bool foundNestedDictionary = false;

        foreach (var prop in root.EnumerateObject())
        {
            if (NonPackageContainerKeys.Contains(prop.Name, StringComparer.OrdinalIgnoreCase))
                continue;

            if (prop.Value.ValueKind != JsonValueKind.Object)
                continue;

            // Uma entrada de pacote de verdade é {"icon": "...", "images": [...]}.
            // Um bucket por fonte (v1) ou o "icons_and_screenshots" (v2) é um
            // dicionário cujos VALORES são desse formato. Diferencia os dois
            // checando se o próprio prop.Value já parece uma entrada de ícone
            // ou se é um contêiner que precisa ser descido mais um nível.
            if (LooksLikeIconEntry(prop.Value))
            {
                // Schema plano legado: raiz já é {packageId: {icon, images}}.
                string? icon = ExtractField(prop.Value, IconFieldCandidates);
                if (icon != null)
                    yield return (IconIdNormalizer.Normalize(prop.Name), icon);
                continue;
            }

            // Bucket/contêiner (v1: "winget"/"scoop"/...; v2: "icons_and_screenshots").
            foundNestedDictionary = true;
            foreach (var pkg in prop.Value.EnumerateObject())
            {
                string? icon = ExtractField(pkg.Value, IconFieldCandidates);
                if (icon == null) continue;

                yield return (IconIdNormalizer.Normalize(pkg.Name), icon);
            }
        }

        // Se nada bateu com nenhuma das formas conhecidas, não falha silenciosamente:
        // não há fallback seguro aqui porque a causa raiz (nesse caso) não é um
        // schema novo, e sim um vazio genuíno — resolved simplesmente fica sem
        // essas entradas, como já acontecia antes para arquivos corrompidos.
        _ = foundNestedDictionary;
    }

    private static bool LooksLikeIconEntry(JsonElement element) =>
        element.ValueKind == JsonValueKind.Object &&
        IconFieldCandidates.Any(field => element.TryGetProperty(field, out _));

    private static string? ExtractField(JsonElement element, string[] candidates)
    {
        if (element.ValueKind != JsonValueKind.Object) return null;

        foreach (var field in candidates)
        {
            if (element.TryGetProperty(field, out var value) && value.ValueKind == JsonValueKind.String)
            {
                string? s = value.GetString();
                if (!string.IsNullOrWhiteSpace(s)) return s;
            }
        }

        return null;
    }
}
