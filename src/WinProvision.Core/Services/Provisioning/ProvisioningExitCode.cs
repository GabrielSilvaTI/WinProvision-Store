namespace WinProvision.Core.Services.Provisioning;

/// <summary>
/// Códigos de saída do processo pro modo CLI (<c>WinProvision.Store.exe /Provision perfil.json</c>).
/// Mesma ideia do <see cref="AutoInstallExitCode"/> (modo /auto): quem chama de fora (task
/// sequence, script, ou o WinProvision principal via Process.Start) consegue diferenciar os
/// cenários sem parsear texto do console/log.
/// </summary>
public enum ProvisioningExitCode
{
    /// <summary>Todos os ajustes do perfil foram aplicados com sucesso (ou o perfil não definia nenhum).</summary>
    Success = 0,

    /// <summary>Rodou de ponta a ponta, mas 1+ ajuste falhou ao aplicar. Ver log para detalhes.</summary>
    CompletedWithFailures = 1,

    /// <summary>"/Provision" foi passado sem caminho de perfil (ou outro uso inválido de argumentos).</summary>
    InvalidArguments = 2,

    /// <summary>O caminho de perfil (.json) informado não existe no disco.</summary>
    ProfileNotFound = 3,

    /// <summary>O arquivo existe mas não deu para ler/desserializar como um perfil de provisionamento válido.</summary>
    ProfileReadError = 4,

    /// <summary>Erro não esperado (exceção) em algum ponto fora dos casos acima.</summary>
    UnexpectedError = 5,
}
