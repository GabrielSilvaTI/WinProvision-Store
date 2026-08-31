using System.Management;
using System.Runtime.Versioning;

namespace WinProvision.Core.Services.Provisioning;

/// <summary>
/// Cria pontos de restauração do sistema via WMI (classe <c>SystemRestore</c>, namespace
/// <c>root\default</c>) — o equivalente scriptável documentado da API nativa
/// <c>SRSetRestorePointW</c> (mesma engine usada pelo cmdlet <c>Checkpoint-Computer</c> do
/// PowerShell). Usar WMI em vez de P/Invoke direto evita ter que inicializar manualmente a
/// segurança COM (CoInitializeSecurity) que a API nativa exige.
/// </summary>
[SupportedOSPlatform("windows")]
public class RestorePointService
{
    private const string Scope = @"root\default";
    private const string ClassName = "SystemRestore";

    /// <summary>RestorePointType (ver CreateRestorePoint method of the SystemRestore class): MODIFY_SETTINGS = 12.</summary>
    private const uint RestorePointTypeModifySettings = 12;

    /// <summary>EventType: BEGIN_SYSTEM_CHANGE = 100 — usado sozinho (sem END_SYSTEM_CHANGE pareado) para um ponto único e imediato, igual ao Checkpoint-Computer.</summary>
    private const uint EventTypeBeginSystemChange = 100;

    /// <summary>
    /// Cria um ponto de restauração imediato com a descrição informada. O Windows por padrão só
    /// cria um a cada 24h (ver SystemRestorePointCreationFrequency) — chamadas mais frequentes
    /// podem retornar sucesso "reaproveitando" o ponto já criado no período, sem gerar um novo.
    /// </summary>
    public Task<(bool Success, string Message)> CreateAsync(string description, CancellationToken ct = default)
    {
        return Task.Run(() =>
        {
            using var restoreClass = new ManagementClass(new ManagementScope(Scope), new ManagementPath(ClassName), null);
            using var inParams = restoreClass.GetMethodParameters("CreateRestorePoint");

            inParams["Description"] = description;
            inParams["RestorePointType"] = RestorePointTypeModifySettings;
            inParams["EventType"] = EventTypeBeginSystemChange;

            ct.ThrowIfCancellationRequested();

            using var outParams = restoreClass.InvokeMethod("CreateRestorePoint", inParams, null);
            uint returnValue = (uint)outParams["ReturnValue"];

            return returnValue == 0
                ? (true, $"Ponto de restauração \"{description}\" criado.")
                : (false, $"CreateRestorePoint retornou o código {returnValue} (ver documentação do WMI SystemRestore).");
        }, ct);
    }
}
