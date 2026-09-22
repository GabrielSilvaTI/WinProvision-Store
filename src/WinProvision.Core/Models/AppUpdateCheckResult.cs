namespace WinProvision.Core.Models;

/// <summary>
/// Resultado de <see cref="Services.AppUpdateService.CheckForUpdateAsync"/>. Cobre tanto
/// falha de rede/parsing (<see cref="Success"/> = false, <see cref="Error"/> preenchido)
/// quanto sucesso sem atualização disponível — os dois casos em que não há nada a instalar.
///
/// O repositório hoje só publica a release de tag fixa "nightly" (rebuild automático a cada
/// commit, sem versão semântica — ver releases/tag/nightly). Quando existir uma tag vX.Y.Z
/// estável, <see cref="LatestVersion"/> vem preenchido e a comparação é por versão; no canal
/// nightly, sem versão para comparar, vem <see cref="ReleaseLabel"/> (nome da release, ex.
/// "1.0.1-nightly.2") e <see cref="PublishedAt"/>, comparado contra a data de build do
/// executável atual.
/// </summary>
public sealed record AppUpdateCheckResult(
    bool Success,
    bool UpdateAvailable,
    Version? CurrentVersion = null,
    Version? LatestVersion = null,
    string? DownloadUrl = null,
    string? Sha256 = null,
    string? ReleaseUrl = null,
    string? ReleaseLabel = null,
    DateTimeOffset? PublishedAt = null,
    string? Error = null)
{
    public static AppUpdateCheckResult Failed(string error) => new(
        Success: false, UpdateAvailable: false, Error: error);
}
