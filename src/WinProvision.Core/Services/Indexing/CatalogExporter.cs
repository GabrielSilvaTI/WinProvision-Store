using System.Text.Json;
using WinProvision.Core.Models;

namespace WinProvision.Core.Services.Indexing;

/// <summary>
/// Exporta o catálogo processado para uso pela publicação externa no R2.
/// Os arquivos gerados são artefatos de pipeline e não devem ser versionados
/// neste repositório.
///
/// Usa as mesmas <see cref="WinProvisionJsonOptions"/> centralizadas que
/// <see cref="StoreService"/> usa para LER o apps.json de volta — gerador e
/// consumidor do mesmo arquivo precisam concordar na mesma política de
/// serialização (antes cada lado definia seu próprio JsonSerializerOptions).
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
