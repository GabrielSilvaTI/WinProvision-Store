using System.Text.Json;
using System.Text.Json.Serialization;
using WinProvision.Core.Models;

namespace WinProvision.Core.Services.Indexing;

public class SearchIndexEntry
{
    [JsonPropertyName("id")] public string Id { get; set; } = "";
    [JsonPropertyName("name")] public string Name { get; set; } = "";
    [JsonPropertyName("publisher")] public string Publisher { get; set; } = "";
    [JsonPropertyName("score")] public int Score { get; set; }
    [JsonPropertyName("tags")] public List<string> Tags { get; set; } = [];
}

/// <summary>
/// Exporta o catálogo processado em múltiplos arquivos JSON segmentados, para que
/// o app cliente nunca precise baixar a base inteira só para mostrar a home ou
/// fazer autocomplete de busca:
///
///  - apps.json               catálogo completo higienizado e pontuado
///  - apps-featured.json      top N por score (destaques da home)
///  - apps-regional-br.json   apenas apps com apelo regional Brasil
///  - apps-search-index.json  campos mínimos, para busca/autocomplete rápido
/// </summary>
public class CatalogExporter
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = false,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    public async Task ExportAsync(List<AppEntry> catalog, string outputDir, int featuredCount = 500)
    {
        Directory.CreateDirectory(outputDir);

        var sorted = catalog.OrderByDescending(a => a.Score).ThenBy(a => a.Name).ToList();

        await WriteAsync(Path.Combine(outputDir, "apps.json"), sorted);
        await WriteAsync(Path.Combine(outputDir, "apps-featured.json"), sorted.Take(featuredCount).ToList());
        await WriteAsync(
            Path.Combine(outputDir, "apps-regional-br.json"),
            sorted.Where(a => a.RegionTags.Contains("BR")).ToList());

        var searchIndex = sorted.Select(a => new SearchIndexEntry
        {
            Id = a.Id,
            Name = a.Name,
            Publisher = a.Publisher,
            Score = a.Score,
            Tags = a.Tags
        }).ToList();

        await WriteAsync(Path.Combine(outputDir, "apps-search-index.json"), searchIndex);
    }

    private static async Task WriteAsync<T>(string path, T data)
    {
        await using var stream = File.Create(path);
        await JsonSerializer.SerializeAsync(stream, data, JsonOptions);
    }
}
