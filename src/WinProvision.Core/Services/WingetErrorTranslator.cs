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
}

/// <summary>
/// Classifica a saída bruta (stdout/stderr) do winget.exe em um <see cref="WingetFailureReason"/>
/// e devolve uma mensagem curta e fixa em pt-BR pra exibir na UI — em vez de repassar o texto
/// cru do winget (que ora sai em inglês, ora em português, dependendo da string específica).
/// </summary>
public static class WingetErrorTranslator
{
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

        WingetFailureReason.ElevationRequired =>
            $"Falha ao {verb} \"{appName}\": requer privilégios de administrador.",

        WingetFailureReason.ElevationCanceled =>
            $"{Capitalize(verb)} de \"{appName}\" cancelada: elevação (UAC) recusada.",

        _ => $"Falha ao {verb} \"{appName}\". Veja o log da operação para detalhes.",
    };

    private static string Capitalize(string s) => string.IsNullOrEmpty(s) ? s : char.ToUpperInvariant(s[0]) + s[1..];

    private static bool Contains(string haystack, string needle) =>
        haystack.IndexOf(needle, StringComparison.OrdinalIgnoreCase) >= 0;
}
