namespace WinProvision.Core.Models;

/// <summary>
/// Resultado de <see cref="Services.AppUpdateService.CheckForUpdateAsync"/>. Cobre tanto
/// falha de rede/parsing (<see cref="Success"/> = false, <see cref="Error"/> preenchido)
/// quanto sucesso sem atualização disponível — os dois casos em que não há nada a instalar.
/// </summary>
public sealed record AppUpdateCheckResult(
    bool Success,
    bool UpdateAvailable,
    Version? CurrentVersion = null,
    Version? LatestVersion = null,
    string? DownloadUrl = null,
    string? Sha256 = null,
    string? ReleaseUrl = null,
    string? Error = null)
{
    public static AppUpdateCheckResult Failed(string error) => new(
        Success: false, UpdateAvailable: false, Error: error);
}
