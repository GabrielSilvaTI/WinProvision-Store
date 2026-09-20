using System;

namespace WinProvision.Core.Services;

/// <summary>
/// Motivo "canônico" de falha do winget, independente do idioma em que o winget.exe
/// decidiu imprimir a mensagem daquela vez (a cobertura de localização do winget-cli é
/// incompleta/inconsistente: algumas strings de erro têm tradução pt-BR, outras não, então
/// a MESMA operação pode falhar em inglês numa tentativa e em português noutra).
/// </summary>
public enum WingetFailureReason
{
    Unknown,

    /// <summary>
    /// "The package installed for user scope cannot be uninstalled when running with
    /// administrator privileges." — só deve aparecer se a Store for lançada elevada por
    /// fora (ex.: "Executar como administrador" no menu de contexto), já que por padrão
    /// (ver app.manifest) ela roda sem elevação.
    /// </summary>
    UserScopeElevationConflict,

    /// <summary>"No installed package found matching input criteria."</summary>
    NoPackageFound,

    /// <summary>Falhou por falta de privilégio (ex.: pacote em escopo machine) — candidato a retry elevado.</summary>
    ElevationRequired,

    /// <summary>Usuário recusou o prompt de UAC no retry elevado.</summary>
    ElevationCanceled,
    BlockedByPolicy,
    NoApplicableInstallers,
    PackageAgreementsNotAccepted,
    DownloadError,
    InstallError,
    CatalogError,
    InternalError,

    /// <summary>
    /// "No package found matching input criteria." — o ID não existe no catálogo consultado
    /// (winget install/update/show). Diferente de <see cref="NoPackageFound"/>, que é o caso
    /// do uninstall/list ("No installed package found...", pacote não está INSTALADO).
    /// Exit code do winget: 0x8A150014 (APPINSTALLER_CLI_ERROR_NO_APPLICATIONS_FOUND).
    /// </summary>
    PackageNotInCatalog,
}

/// <summary>
/// Classifica a saída bruta (stdout/stderr) do winget.exe em um <see cref="WingetFailureReason"/>
/// e devolve uma mensagem curta e fixa em pt-BR pra exibir na UI — em vez de repassar o texto
/// cru do winget (que ora sai em inglês, ora em português, dependendo da string específica).
/// </summary>
public static class WingetErrorTranslator
{
    // HRESULTs do winget-cli (src/AppInstallerSharedLib/Public/AppInstallerErrors.h). O exit
    // code NÃO depende do idioma em que o winget imprime o texto, então é a fonte mais
    // confiável — o texto só serve pra desempatar o que o exit code sozinho não distingue
    // (ex.: 0x8A150014 vale tanto pra "pacote não existe no catálogo" quanto pra "pacote
    // não está instalado").
    private const int NoApplicationsFound = unchecked((int)0x8A150014);
    private const int NoApplicableInstaller = unchecked((int)0x8A150010);
    private const int DownloadFailed = unchecked((int)0x8A150008);
    private const int InstallerHashMismatch = unchecked((int)0x8A150011);
    private const int PackageAgreementsNotAcceptedCode = unchecked((int)0x8A150041);
    private const int InternalErrorCode = unchecked((int)0x8A150001);
    private const int SourceDataMissing = unchecked((int)0x8A15000F);
    private const int SourceOpenFailed = unchecked((int)0x8A150045);

    /// <summary>
    /// Classifica usando o texto E o exit code do winget. Prefira esta sobrecarga: o texto
    /// (<see cref="Classify(string?)"/>) vem no idioma do sistema, o exit code não.
    /// </summary>
    public static WingetFailureReason Classify(int exitCode, string? output)
    {
        // 1) Texto primeiro: preserva o comportamento existente (e distingue, por exemplo,
        //    "não instalado" de "não existe no catálogo", que compartilham o exit code).
        var byText = Classify(output);
        if (byText != WingetFailureReason.Unknown)
        {
            return byText;
        }

        // 2) Fallback pelo exit code — cobre winget em qualquer idioma / wording novo.
        return exitCode switch
        {
            NoApplicationsFound => WingetFailureReason.PackageNotInCatalog,
            NoApplicableInstaller => WingetFailureReason.NoApplicableInstallers,
            DownloadFailed or InstallerHashMismatch => WingetFailureReason.DownloadError,
            PackageAgreementsNotAcceptedCode => WingetFailureReason.PackageAgreementsNotAccepted,
            SourceDataMissing or SourceOpenFailed => WingetFailureReason.CatalogError,
            InternalErrorCode => WingetFailureReason.InternalError,
            _ => WingetFailureReason.Unknown,
        };
    }

    public static WingetFailureReason Classify(string? output)
    {
        if (string.IsNullOrWhiteSpace(output))
        {
            return WingetFailureReason.Unknown;
        }

        // Casa tanto a string original em inglês quanto a tradução pt-BR (quando existe),
        // porque não dá pra confiar em qual das duas o winget vai imprimir dessa vez.
        if (Contains(output, "cannot be uninstalled when running with administrator privileges") ||
            Contains(output, "não pode ser desinstalado quando executado com privilégios de administrador"))
        {
            return WingetFailureReason.UserScopeElevationConflict;
        }

        if (Contains(output, "No installed package found matching input criteria") ||
            Contains(output, "Nenhum pacote instalado encontrado correspondendo aos critérios de entrada"))
        {
            return WingetFailureReason.NoPackageFound;
        }

        // Install/update de um ID que não existe no catálogo. Note a diferença pra frase acima:
        // aqui NÃO tem a palavra "installed" ("No package found matching input criteria.").
        if (Contains(output, "No package found matching input criteria") ||
            Contains(output, "Nenhum pacote encontrado correspondendo aos critérios de entrada"))
        {
            return WingetFailureReason.PackageNotInCatalog;
        }

        if (Contains(output, "Access is denied") ||
            Contains(output, "Acesso negado") ||
            Contains(output, "0x80070005") ||
            Contains(output, "requires administrator") ||
            Contains(output, "requer privilégios de administrador") ||
            Contains(output, "elevated permissions are required"))
        {
            return WingetFailureReason.ElevationRequired;
        }

        return WingetFailureReason.Unknown;
    }

    /// <summary>
    /// Mensagem final em pt-BR pra mostrar ao usuário — curta e direta, sempre a mesma
    /// independente de como o winget formatou a própria mensagem dessa vez.
    /// </summary>
    public static string ToMessage(WingetFailureReason reason, string verb, string appName) => reason switch
    {
        WingetFailureReason.UserScopeElevationConflict =>
            $"Não foi possível {verb} \"{appName}\": feche a Store e rode sem \"Executar como administrador\".",

        WingetFailureReason.NoPackageFound =>
            $"\"{appName}\" não foi encontrado como instalado (já removido, ou o ID mudou).",

        WingetFailureReason.PackageNotInCatalog =>
            $"Falha ao {verb} \"{appName}\": pacote não encontrado no catálogo do WinGet (o ID pode ter mudado ou sido removido).",

        WingetFailureReason.ElevationRequired =>
            $"Falha ao {verb} \"{appName}\": requer privilégios de administrador.",

        WingetFailureReason.ElevationCanceled =>
            $"{Capitalize(verb)} de \"{appName}\" cancelada: elevação (UAC) recusada.",

        WingetFailureReason.BlockedByPolicy =>
            $"Falha ao {verb} \"{appName}\": operação bloqueada por política do sistema.",

        WingetFailureReason.NoApplicableInstallers =>
            $"Falha ao {verb} \"{appName}\": não há instalador compatível para este dispositivo.",

        WingetFailureReason.PackageAgreementsNotAccepted =>
            $"Falha ao {verb} \"{appName}\": os acordos do pacote não foram aceitos.",

        WingetFailureReason.DownloadError =>
            $"Falha ao {verb} \"{appName}\": erro ao baixar o instalador.",

        WingetFailureReason.InstallError =>
            $"Falha ao {verb} \"{appName}\": o instalador retornou um erro.",

        WingetFailureReason.CatalogError =>
            $"Falha ao {verb} \"{appName}\": erro no catálogo do WinGet.",

        WingetFailureReason.InternalError =>
            $"Falha ao {verb} \"{appName}\": erro interno do WinGet.",

        _ => $"Falha ao {verb} \"{appName}\". Veja o log da operação para detalhes.",
    };

    private static string Capitalize(string s) => string.IsNullOrEmpty(s) ? s : char.ToUpperInvariant(s[0]) + s[1..];

    private static bool Contains(string haystack, string needle) =>
        haystack.IndexOf(needle, StringComparison.OrdinalIgnoreCase) >= 0;
}
