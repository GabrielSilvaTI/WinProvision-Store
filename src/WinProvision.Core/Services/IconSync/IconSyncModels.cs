namespace WinProvision.Core.Services.IconSync;

/// <summary>
/// Origem de onde um ícone foi resolvido. A ordem dos valores aqui não importa
/// para a lógica (a prioridade real está em <see cref="IconSyncPipeline"/>) — serve
/// só para os contadores de estatística no log da pipeline.
/// </summary>
public enum IconSourceKind
{
    WinGetOfficialManifest,
    Cloudflare,
    WinstallApproved,
    UniGetUi,
    External
}

public record ResolvedIcon(string NormalizedAppId, string IconUrl, IconSourceKind Source);

/// <summary>
/// Sugestão de correspondência entre um arquivo de ícone do Winstall e um app do
/// catálogo, calculada por similaridade de nome. Nunca é aplicada automaticamente —
/// ver <see cref="WinstallReviewCandidateGenerator"/> e docs/ICON_APPROVAL.md.
/// </summary>
public record WinstallReviewCandidate(
    string WinstallFileName,
    string SuggestedAppId,
    string SuggestedAppName,
    double Confidence);

/// <param name="WinGetIndexDbPath">
/// Caminho local do index.db oficial do WinGet (baixado à parte, ver
/// docs/ICON_APPROVAL.md). Opcional: se nulo/vazio ou o arquivo não existir, o
/// Tier 1 (manifesto oficial via CDN) é pulado silenciosamente e a cascata
/// segue normalmente a partir das fontes comunitárias de fallback.
/// </param>
public record IconSyncOptions(
    string CatalogPath,
    string WinstallDir,
    string ExternalDir,
    string UniGetUiDir,
    string ApprovedMappingsPath,
    string OutputDir,
    string? WinGetIndexDbPath = null);

public record IconSyncStats(
    int CatalogSize,
    int ResolvedFromWinGetManifest,
    int ResolvedFromCloudflare,
    int ResolvedFromWinstallApproved,
    int ResolvedFromUniGetUi,
    int ResolvedFromExternal,
    int Unresolved,
    int WinstallReviewCandidatesGenerated);
