using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Security.Principal;
using System.Text.RegularExpressions;

namespace WinProvision.Store.Services;

/// <summary>
/// Log de diagnóstico do WinGet: grava em WinProvision-Log.log, na pasta temporária.
/// As mensagens que chegam em <see cref="Write"/> costumam vir cheias de campos técnicos
/// (pid, tid, elapsed, dispatcher etc.), úteis pra quem escreveu o código, inúteis pra
/// quem só quer saber "o que aconteceu". <see cref="Humanize"/> traduz os formatos mais
/// comuns pra frases diretas em português; o que não reconhece, só tira o ruído.
/// </summary>
internal static class WinProvisionLog
{
    private static readonly object Gate = new();
    private static readonly string Path =
        System.IO.Path.Combine(
            System.IO.Path.GetTempPath(),
            "WinProvision-Log.log");

    public static string FilePath => Path;

    public static void Write(string message)
    {
        string? clean = Humanize(message);
        if (clean is null)
        {
            // Linha só de instrumentação interna (ex.: criação do PackageManager),
            // sem valor pra quem está lendo o log. Descartada.
            return;
        }

        var line = $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] {clean}";

        Trace.WriteLine(line);
        lock (Gate)
        {
            try
            {
                var directory = System.IO.Path.GetDirectoryName(Path);
                if (!string.IsNullOrEmpty(directory))
                {
                    Directory.CreateDirectory(directory);
                }

                File.AppendAllText(Path, line + Environment.NewLine);
            }
            catch
            {
                // Diagnostic logging must never change installation behavior.
            }
        }
    }

    public static bool IsElevated()
    {
        using var identity = WindowsIdentity.GetCurrent();
        return new WindowsPrincipal(identity).IsInRole(
            WindowsBuiltInRole.Administrator);
    }

    public static void WriteComServerInfo()
    {
        var servers = Process.GetProcessesByName("WindowsPackageManagerServer");
        if (servers.Length == 0)
        {
            Write("COM SERVER name=WindowsPackageManagerServer pid=not-found");
            return;
        }

        foreach (var server in servers)
        {
            using (server)
            {
                var path = string.Empty;
                try { path = server.MainModule?.FileName ?? string.Empty; }
                catch (Exception ex) { path = $"unavailable:{ex.GetType().Name}"; }

                Write(
                    $"COM SERVER name={server.ProcessName}.exe pid={server.Id} " +
                    $"integrity={GetIntegrityLevel(server.Id)} path=\"{path}\"");
            }
        }

        Write(
            $"COM CLIENT pid={Environment.ProcessId} integrity={GetIntegrityLevel(Environment.ProcessId)} " +
            $"elevated={IsElevated()}");
    }

    // ----- Tradução das mensagens -----

    private static readonly (Regex Pattern, Func<Match, string?> Format)[] Rules =
    {
        // Ruído puramente interno, sem informação nova pra quem lê o log.
        (new Regex(@"^(INSTALL|UPDATE) COM stage=(packageManager-created|first-progress)\b"),
            _ => null),

        // Inicialização do app.
        (new Regex(@"^STARTUP elevated=(?<elevated>True|False) exe=""[^""]*"" buildDate=\S+ auto=(?<auto>True|False) debugger=\S+ relaunched=\S+$"),
            m => "Iniciando WinProvision Store" +
                 $" (elevado: {(m.Groups["elevated"].Value == "True" ? "sim" : "não")}" +
                 $", modo automático: {(m.Groups["auto"].Value == "True" ? "sim" : "não")})"),
        (new Regex(@"^STARTUP decision=\s*(?<decision>[\w-]+)(?:\s+result=(?<result>[\w-]+))?.*$"),
            m => DescribeStartupDecision(m.Groups["decision"].Value, m.Groups["result"].Value)),
        (new Regex(@"^(?<kind>INSTALL|UPDATE) HANDLER CONFIGURED startupPath=\S+ handler=\S+$"),
            m => m.Groups["kind"].Value == "INSTALL"
                ? "Pronto para instalar pacotes"
                : "Pronto para atualizar pacotes"),
        (new Regex(@"^WINGET MODE=(?<mode>\S+) \([^)]*\) estratégias-COM=(?<strategies>\S+)$"),
            m => $"Modo de operação: {m.Groups["mode"].Value} (estratégias COM: {m.Groups["strategies"].Value.Replace(">", " > ")})"),

        // Chamadas diretas ao winget.exe (fallback CLI ou verificações pontuais).
        (new Regex(@"^WINGET\.EXE LAUNCH file=(?<file>\S+) args=""(?<args>[^""]*)"".*$"),
            m => $"Executando: {m.Groups["file"].Value} {m.Groups["args"].Value}".TrimEnd()),
        (new Regex(@"^WINGET\.EXE RESULT file=(?<file>\S+) exitCode=(?<code>-?\d+) success=(?<success>True|False)$"),
            m => m.Groups["success"].Value == "True"
                ? $"{m.Groups["file"].Value} concluído"
                : $"{m.Groups["file"].Value} falhou (código {m.Groups["code"].Value})"),

        // Ativação dos componentes COM do WinGet (CLSID/IID/HRESULT). Só interessa
        // quando algo dá errado — quando funciona, é ruído de instrumentação.
        (new Regex(@"^COM ACTIVATION type=\S+ strategy=\S+ CLSID=.*$"),
            _ => null),
        (new Regex(@"^COM ACTIVATION type=(?<type>\S+) strategy=(?<strategy>\S+) CoCreateInstance HRESULT=(?<hresult>0x[0-9A-Fa-f]+)$"),
            m => m.Groups["hresult"].Value == "0x00000000"
                ? null
                : $"Falha ao ativar {m.Groups["type"].Value} via COM (HRESULT={m.Groups["hresult"].Value})"),
        (new Regex(@"^COM ACTIVATION type=(?<type>\S+) strategy=(?<strategy>\S+) FromAbi=(?<status>\S+)$"),
            m => m.Groups["status"].Value == "success"
                ? null
                : $"Falha ao converter {m.Groups["type"].Value} (FromAbi={m.Groups["status"].Value})"),
        (new Regex(@"^COM PROBE status=(?<status>\S+) strategy=(?<strategy>\S+) mode=\S+$"),
            m => m.Groups["status"].Value == "Ok"
                ? null
                : $"Falha ao testar API COM ({m.Groups["strategy"].Value}): {m.Groups["status"].Value}"),

        // Monitor interno de travamento da UI — só instrumentação de arranque.
        (new Regex(@"^UI THREAD MONITOR started.*$"),
            _ => null),

        // Início de uma instalação/atualização, com o id do pacote pedido.
        (new Regex(@"^(?<kind>INSTALL|UPDATE) ENTER packageId=""(?<id>[^""]+)"".*$"),
            m => m.Groups["kind"].Value == "INSTALL"
                ? $"Solicitação de instalação: {m.Groups["id"].Value}"
                : $"Solicitação de atualização: {m.Groups["id"].Value}"),

        // Quem está do outro lado da chamada COM (nosso processo e o serviço do WinGet).
        (new Regex(@"^COM SERVER name=\S+ pid=not-found$"),
            _ => "Serviço do WinGet não está em execução"),
        (new Regex(@"^COM SERVER name=\S+ pid=\d+ integrity=(?<integrity>\S+) path="),
            m => $"Serviço do WinGet ativo (integridade {m.Groups["integrity"].Value})"),
        (new Regex(@"^COM CLIENT pid=\d+ integrity=(?<integrity>\S+) elevated=(?<elevated>True|False)$"),
            m => $"Executando com integridade {m.Groups["integrity"].Value}" +
                 (m.Groups["elevated"].Value == "True" ? ", elevado" : ", não elevado")),

        // Ciclo de vida de uma instalação/atualização via API COM do WinGet.
        (new Regex(@"^(?<tag>INSTALL|UPDATE) COM stage=begin$"),
            m => m.Groups["tag"].Value == "INSTALL" ? "Iniciando instalação via WinGet" : "Iniciando atualização via WinGet"),
        (new Regex(@"^(INSTALL|UPDATE) COM stage=connect-completed catalog=(?<cat>\S+) status=(?<status>\S+)"),
            m => m.Groups["status"].Value == "Ok"
                ? $"Conectado ao catálogo {m.Groups["cat"].Value}"
                : $"Falha ao conectar ao catálogo {m.Groups["cat"].Value}: {m.Groups["status"].Value}"),
        (new Regex(@"^(INSTALL|UPDATE) COM stage=package-lookup-completed catalog=(?<cat>\S+) found=(?<found>True|False)"),
            m => m.Groups["found"].Value == "True"
                ? $"Pacote localizado no catálogo {m.Groups["cat"].Value}"
                : $"Pacote não encontrado no catálogo {m.Groups["cat"].Value}"),
        (new Regex(@"^(INSTALL|UPDATE) COM stage=install-dispatched"),
            _ => "Instalação enviada ao WinGet, aguardando progresso"),
        (new Regex(@"^(INSTALL|UPDATE) COM progress callback=\d+ state=(?<state>\S+) download=(?<dl>[\d.]+) install=(?<ins>[\d.]+)"),
            m => FormatProgress(m.Groups["state"].Value, m.Groups["dl"].Value, m.Groups["ins"].Value)),
        (new Regex(@"^(INSTALL|UPDATE) COM heartbeat state=(?<state>\S+) download=(?<dl>[\d.]+) install=(?<ins>[\d.]+)"),
            m => "Ainda em andamento — " + FormatProgress(m.Groups["state"].Value, m.Groups["dl"].Value, m.Groups["ins"].Value)),
        (new Regex(@"^(INSTALL|UPDATE) COM stage=result status=(?<status>\S+) reboot=(?<reboot>True|False)"),
            m => m.Groups["status"].Value == "Ok"
                ? "Instalação concluída com sucesso" + (m.Groups["reboot"].Value == "True" ? " (reinicialização necessária)" : "")
                : $"Instalação terminou com falha: {m.Groups["status"].Value}"),
        (new Regex(@"^(INSTALL|UPDATE) COM failure status=(?<status>\S+) installerErrorCode=(?<code>0x[0-9A-Fa-f]+ \(\d+\))"),
            m => $"Falha na instalação ({m.Groups["status"].Value}), código do instalador {m.Groups["code"].Value}"),

        // Trocas de estratégia (COM -> API própria -> winget.exe).
        (new Regex(@"^FALLBACK PARA API PRÓPRIA: motivo=(?<why>.+)$"),
            m => $"Usando a API própria da WinProvision (motivo: {m.Groups["why"].Value})"),
        (new Regex(@"^FALLBACK PARA CLI: motivo=(?<why>.+)$"),
            m => $"Usando o winget.exe (motivo: {m.Groups["why"].Value})"),
        (new Regex(@"^WINGET PROVISION status=(?<status>\S+) exe="),
            m => $"Winget provisionado (status: {m.Groups["status"].Value})"),
    };

    /// <summary>
    /// Traduz a razão registrada em "STARTUP decision=..." pra uma frase direta.
    /// Motivos não mapeados ainda caem num texto genérico em vez de sumir do log.
    /// </summary>
    private static string DescribeStartupDecision(string decision, string result) => decision switch
    {
        "debugger-exception" => "Rodando via depurador — reinício sem elevação ignorado",
        "auto-exception" => "Modo automático — reinício sem elevação ignorado",
        "allow-elevated-exception" => "Execução elevada permitida explicitamente",
        "interactive-user-context" => "Contexto de uso normal, sem elevação",
        "loop-protection" => "Processo relançado sem elevação continua elevado — abortando novo reinício",
        "relaunched-unelevated" => "Reinício sem elevação confirmado",
        "relanchado-unelevated" => "Reinício sem elevação confirmado",
        "relaunch-unelevated" when result == "started" => "Reiniciando sem elevação",
        "relaunch-unelevated" when result == "failed" => "Falha ao reiniciar sem elevação — continuando elevado",
        _ => $"Decisão de inicialização: {decision}"
    };

    private static string FormatProgress(string state, string download, string install)
    {
        var stateLabel = state switch
        {
            "Queued" => "Na fila",
            "Downloading" => "Baixando",
            "Installing" => "Instalando",
            "PostInstall" => "Finalizando",
            "Finished" => "Concluído",
            _ => state
        };

        double fraction = state == "Downloading"
            ? double.Parse(download, System.Globalization.CultureInfo.InvariantCulture)
            : double.Parse(install, System.Globalization.CultureInfo.InvariantCulture);
        int percent = (int)Math.Clamp(Math.Round(fraction * 100), 0, 100);

        return state is "Downloading" or "Installing"
            ? $"{stateLabel}: {percent}%"
            : stateLabel;
    }

    /// <summary>
    /// Aplica a primeira regra que casar. Se nenhuma casar, faz uma limpeza genérica
    /// (tira pid=/tid=/elapsed=/dispatcher= e afins) em vez de esconder a mensagem —
    /// melhor um log um pouco mais técnico do que perder um evento sem tradução ainda.
    /// </summary>
    private static string? Humanize(string message)
    {
        foreach (var (pattern, format) in Rules)
        {
            var match = pattern.Match(message);
            if (match.Success)
            {
                return format(match);
            }
        }

        return GenericNoiseFilter.Replace(message, string.Empty).Trim();
    }

    private static readonly Regex GenericNoiseFilter = new(
        @"\s+(pid|tid|elapsed|dispatcher|connect|lookup|sinceLastCallback)=\S+");

    private static string GetIntegrityLevel(int processId)
    {
        var process = OpenProcess(0x1000, false, processId);
        if (!OpenProcessToken(
                process,
                0x0008,
                out var token))
        {
            CloseHandle(process);
            return "unavailable";
        }

        try
        {
            const int TokenIntegrityLevel = 25;
            if (!GetTokenInformation(token, TokenIntegrityLevel, IntPtr.Zero, 0, out var length) &&
                length == 0)
            {
                return "unavailable";
            }

            var buffer = Marshal.AllocHGlobal((int)length);
            try
            {
                if (!GetTokenInformation(
                        token, TokenIntegrityLevel, buffer, length, out _))
                {
                    return "unavailable";
                }

                var label = Marshal.ReadIntPtr(buffer);
                var subAuthorityCount = Marshal.ReadByte(label, 1);
                var rid = Marshal.ReadInt32(
                    IntPtr.Add(label, 8 + ((subAuthorityCount - 1) * 4)));
                return rid switch
                {
                    >= 0x500 => "System",
                    >= 0x400 => "High",
                    >= 0x300 => "Medium",
                    >= 0x200 => "Low",
                    _ => $"0x{rid:X}"
                };
            }
            finally
            {
                Marshal.FreeHGlobal(buffer);
            }
        }
        finally
        {
            CloseHandle(token);
            CloseHandle(process);
        }
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr OpenProcess(uint access, bool inheritHandle, int processId);

    [DllImport("advapi32.dll", SetLastError = true)]
    private static extern bool OpenProcessToken(
        IntPtr processHandle,
        uint desiredAccess,
        out IntPtr tokenHandle);

    [DllImport("advapi32.dll", SetLastError = true)]
    private static extern bool GetTokenInformation(
        IntPtr tokenHandle,
        int tokenInformationClass,
        IntPtr tokenInformation,
        int tokenInformationLength,
        out int returnLength);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool CloseHandle(IntPtr handle);
}
