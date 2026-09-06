using YamlDotNet.Serialization;

namespace WinProvision.Core.Services.Indexing;

/// <summary>
/// Parseia manifestos YAML do winget-pkgs para dicionários genéricos em vez de
/// classes fortemente tipadas. O schema do winget varia bastante entre
/// versões de ManifestVersion (1.0 a 1.9+) e entre singleton/multi-file,
/// então um parser tolerante evita quebrar a pipeline inteira por causa
/// de um campo novo/removido em um manifesto específico.
/// </summary>
public static class ManifestParser
{
    private static readonly IDeserializer Deserializer = new DeserializerBuilder()
        .IgnoreUnmatchedProperties()
        .Build();

    /// <summary>Retorna null se o arquivo não puder ser parseado (contabilizado como erro pelo scanner).</summary>
    public static Dictionary<string, object?>? TryParse(string filePath)
    {
        try
        {
            string content = File.ReadAllText(filePath);
            var raw = Deserializer.Deserialize<Dictionary<object, object>>(content);
            return Normalize(raw);
        }
        catch
        {
            return null;
        }
    }

    private static Dictionary<string, object?> Normalize(Dictionary<object, object>? raw)
    {
        var result = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
        if (raw == null) return result;

        foreach (var kv in raw)
        {
            result[kv.Key.ToString() ?? string.Empty] = kv.Value;
        }

        return result;
    }
}

/// <summary>Extensões de conveniência para ler campos de um manifesto normalizado.</summary>
public static class ManifestDictionaryExtensions
{
    public static string? GetString(this Dictionary<string, object?> manifest, string key)
        => manifest.TryGetValue(key, out var value) ? value?.ToString() : null;

    public static List<string> GetStringList(this Dictionary<string, object?> manifest, string key)
    {
        if (!manifest.TryGetValue(key, out var value) || value is null)
            return [];

        if (value is List<object> list)
        {
            return list
                .Select(x => x?.ToString() ?? string.Empty)
                .Where(x => x.Length > 0)
                .ToList();
        }

        if (value is string single)
            return [single];

        return [];
    }

    public static List<Dictionary<string, object?>> GetObjectList(this Dictionary<string, object?> manifest, string key)
    {
        if (!manifest.TryGetValue(key, out var value) || value is not List<object> list)
            return [];

        var result = new List<Dictionary<string, object?>>();

        foreach (var item in list)
        {
            if (item is not Dictionary<object, object> dict) continue;

            var normalized = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
            foreach (var kv in dict)
                normalized[kv.Key.ToString() ?? string.Empty] = kv.Value;

            result.Add(normalized);
        }

        return result;
    }
}
