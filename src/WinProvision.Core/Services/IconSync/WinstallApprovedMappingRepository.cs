using System.Text.Json;

namespace WinProvision.Core.Services.IconSync;

/// <summary>
/// Carrega o mapeamento AppId → nome de arquivo do Winstall aprovado manualmente
/// (config/winstall-approved-mappings.json, versionado no repo e editado só via PR
/// humano — ver docs/ICON_APPROVAL.md).
///
/// Os nomes de arquivo do Winstall NÃO seguem o PackageIdentifier do winget de forma
/// confiável (curadoria própria do projeto Winstall), então, diferente do
/// <see cref="ExternalIconRepository"/>, aqui não existe match automático seguro —
/// só o que já foi aprovado entra na base publicada. Tudo que ainda não tem
/// mapeamento aprovado vira candidato de revisão (ver
/// <see cref="WinstallReviewCandidateGenerator"/>), nunca é aplicado sozinho.
/// </summary>
public class WinstallApprovedMappingRepository
{
    private const string BaseRawUrl = "https://raw.githubusercontent.com/SplashtopInc/winstall/master/public/assets/apps";

    public Dictionary<string, string> Load(string approvedMappingsPath, string winstallDir)
    {
        var resolved = new Dictionary<string, string>();
        var mapping = ReadMappingFile(approvedMappingsPath);

        foreach (var (appId, fileName) in mapping)
        {
            // Confirma que o arquivo aprovado ainda existe na fonte antes de publicar o
            // link — o Winstall pode ter reorganizado/removido o ícone desde a aprovação.
            string localPath = Path.Combine(winstallDir, fileName);
            if (!File.Exists(localPath)) continue;

            resolved[IconIdNormalizer.Normalize(appId)] = $"{BaseRawUrl}/{Uri.EscapeDataString(fileName).Replace("%2F", "/")}";
        }

        return resolved;
    }

    /// <summary>
    /// Nomes de arquivo já aprovados (valores do mapeamento), usados pelo
    /// <see cref="WinstallReviewCandidateGenerator"/> para não sugerir de novo algo
    /// que já foi decidido.
    /// </summary>
    public HashSet<string> GetApprovedFileNames(string approvedMappingsPath)
    {
        var mapping = ReadMappingFile(approvedMappingsPath);
        return new HashSet<string>(mapping.Values, StringComparer.OrdinalIgnoreCase);
    }

    private static Dictionary<string, string> ReadMappingFile(string approvedMappingsPath)
    {
        if (!File.Exists(approvedMappingsPath)) return [];

        try
        {
            return JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(approvedMappingsPath)) ?? [];
        }
        catch (Exception ex)
        {
            // Diferente das outras fontes, aqui não existe fallback seguro: um JSON
            // corrompido nesse arquivo curado é sempre erro humano, não ruído de fonte
            // externa — falha alto e explícito em vez de silenciosamente ignorar o arquivo.
            throw new InvalidOperationException(
                $"O mapeamento aprovado do Winstall em '{approvedMappingsPath}' está com JSON inválido: {ex.Message}. " +
                "Corrija manualmente antes de rodar a sincronização.", ex);
        }
    }
}
