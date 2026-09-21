using System.Threading;

namespace WinProvision.Core.Services;

/// <summary>
/// Flag de sessão (processo) que registra quando uma reelevação (UAC) pontual já falhou
/// (usuário clicou "Não", ou o próprio UAC não pôde ser exibido). Espelha o padrão já usado
/// por WinGetFactoryHelper.DisableComForSession, só que pro lado da elevação: uma vez que o
/// UAC falhou nesta sessão, instalações seguintes não tentam a API COM nem pedem UAC de
/// novo — vão direto pra API própria da WinProvision Store (ver WinGetService.InstallAsync).
/// </summary>
public static class WinProvisionElevationState
{
    private static int _failed;

    /// <summary>True assim que a primeira reelevação desta sessão tiver falhado.</summary>
    public static bool HasFailedThisSession => Volatile.Read(ref _failed) != 0;

    /// <summary>
    /// Chamado por ElevatedProcessRunner quando o UAC é cancelado/recusado (ERROR_CANCELLED).
    /// Idempotente — seguro de chamar em toda falha de reelevação subsequente.
    /// </summary>
    public static void MarkFailed() => Interlocked.Exchange(ref _failed, 1);

    /// <summary>Reseta o estado — exposto só pra testes; não é chamado em produção.</summary>
    internal static void ResetForTests() => Interlocked.Exchange(ref _failed, 0);
}
