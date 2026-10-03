using System.Text.Json;
using WinProvision.Core.Models;

namespace WinProvision.Core.Services.Indexing;

/// <summary>
/// Exporta o catálogo processado para uso pela publicação externa no R2.
/// Os arquivos gerados são artefatos de pipeline e não devem ser versionados
/// neste repositório.
///
/// Usa as opções centralizadas de JSON ao gerar o arquivo intermediário que o
/// publicador Python organiza no catálogo hierárquico consumido pelo cliente.
/// </summary>
public class CatalogExporter
{
    private static readonly JsonSerializerOptions JsonOptions = WinProvisionJsonOptions.Compact;

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
