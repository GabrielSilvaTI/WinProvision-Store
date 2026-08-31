namespace WinProvision.Core.Services;

/// <summary>
/// Códigos de saída do processo pro modo CLI (<c>WinProvision.Store.exe /auto perfil.json</c>).
/// Pensado pra quem chama isso de fora (task sequence, script, ou o WinProvision principal
/// via Process.Start) conseguir diferenciar "tudo certo" de "algo falhou" de "uso errado"
/// sem precisar fazer parsing de texto do console/log.
/// </summary>
public enum AutoInstallExitCode
{
    /// <summary>Todos os itens do perfil instalaram com sucesso (ou o perfil estava vazio).</summary>
    Success = 0,

    /// <summary>Rodou de ponta a ponta, mas 1+ item falhou na instalação. Ver log pra detalhes.</summary>
    CompletedWithFailures = 1,

    /// <summary>"/auto" foi passado sem caminho de perfil (ou outro uso inválido de argumentos).</summary>
    InvalidArguments = 2,

    /// <summary>O caminho de perfil (.json) informado não existe no disco.</summary>
    ProfileNotFound = 3,

    /// <summary>O arquivo existe mas não deu pra ler/desserializar como um perfil válido.</summary>
    ProfileReadError = 4,

    /// <summary>Erro não esperado (exceção) em algum ponto fora dos casos acima.</summary>
    UnexpectedError = 5,

    /// <summary>
    /// O perfil tinha apps/Office pra instalar, o winget não estava disponível (comum logo
    /// após o primeiro logon) e o bootstrap automático (ver <see cref="WingetBootstrapper"/>)
    /// não conseguiu deixá-lo funcional — nenhum item que depende do winget foi tentado.
    /// Se o perfil também tinha uma seção de provisionamento, ela ainda foi aplicada
    /// normalmente (não depende do winget).
    /// </summary>
    WingetUnavailable = 6,
}
