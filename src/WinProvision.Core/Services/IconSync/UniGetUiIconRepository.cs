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

            // Duas formas observadas entre os branches: objeto raiz é um dicionário
            // {packageId: {...}}, ou uma lista de objetos com o Id embutido. Suporta as duas.
            if (root.ValueKind == JsonValueKind.Object)
            {
                foreach (var prop in root.EnumerateObject())
                {
                    string? icon = ExtractField(prop.Value, IconFieldCandidates);
                    if (icon == null) continue;

                    yield return (IconIdNormalizer.Normalize(prop.Name), icon);
                }
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
