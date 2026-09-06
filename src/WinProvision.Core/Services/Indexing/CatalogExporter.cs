using System.Text.Json;
using System.Text.Json.Serialization;
using WinProvision.Core.Models;

namespace WinProvision.Core.Services.Indexing;

/// <summary>
/// Exporta o catálogo processado para uso pela publicação externa no R2.
/// Os arquivos gerados são artefatos de pipeline e não devem ser versionados
/// neste repositório.
/// </summary>
public class CatalogExporter
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = false,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    public async Task ExportAsync(List<AppEntry> catalog, string outputDir)
    {
        Directory.CreateDirectory(outputDir);

        var sorted = catalog.OrderByDescending(a => a.Score).ThenBy(a => a.Name).ToList();

        await WriteAsync(Path.Combine(outputDir, "apps.json"), sorted);
    }

    private static async Task WriteAsync<T>(string path, T data)
    {
        await using var stream = File.Create(path);
        await JsonSerializer.SerializeAsync(stream, data, JsonOptions);
    }
}
