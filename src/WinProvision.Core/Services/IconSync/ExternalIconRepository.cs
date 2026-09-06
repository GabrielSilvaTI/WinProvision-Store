namespace WinProvision.Core.Services.IconSync;

/// <summary>
/// Ícones do memstechtips/package-icons (branch dev, pasta icons/external) são
/// nomeados com o PackageIdentifier do winget (ex.: "Microsoft.VisualStudioCode.png"),
/// então o match aqui é direto por nome de arquivo normalizado — sem heurística e
/// sem fuzzy match. É a fonte mais barata de resolver, mas a de menor cobertura;
/// por isso entra por último na ordem de prioridade do <see cref="IconSyncPipeline"/>.
/// </summary>
public class ExternalIconRepository
{
    private const string BaseRawUrl = "https://raw.githubusercontent.com/memstechtips/package-icons/dev/icons/external";

    public Dictionary<string, string> Load(string externalDir)
    {
        var resolved = new Dictionary<string, string>();
        if (!Directory.Exists(externalDir)) return resolved;

        foreach (var file in Directory.EnumerateFiles(externalDir))
        {
            string fileName = Path.GetFileName(file);
            string normalizedId = IconIdNormalizer.Normalize(fileName);
            if (normalizedId.Length == 0) continue;

            // A URL aponta pro repositório de origem em vez de reempacotar o binário do
            // ícone — evita publicar/versionar imagens que já são mantidas por outro projeto.
            resolved.TryAdd(normalizedId, $"{BaseRawUrl}/{Uri.EscapeDataString(fileName)}");
        }

        return resolved;
    }
}
