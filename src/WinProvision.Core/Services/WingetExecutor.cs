using System.Diagnostics;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Threading;
using WinProvision.Core.Models;

namespace WinProvision.Core.Services;

public class WingetExecutionResult
{
    public bool Success { get; set; }
    public int ExitCode { get; set; }
    public string Output { get; set; } = string.Empty;
}

public class WingetExecutor
{
    private readonly WingetBootstrapper? _bootstrapper;
    private readonly object _bootstrapLock = new();
    private Task<WingetBootstrapResult>? _bootstrapTask;

    /// <param name="bootstrapper">
    /// Opcional — quando presente (via injeção de dependência; ver App.xaml.cs), garante o
    /// winget disponível antes da PRIMEIRA instalação da sessão (ver
    /// <see cref="EnsureWingetBootstrappedOnceAsync"/>). Null mantém o comportamento antigo
    /// (usado pelo WinProvision.ConsoleDemo, que instancia sem DI) — sem checagem prévia,
    /// só chama winget.exe direto.
    /// </param>
    public WingetExecutor(WingetBootstrapper? bootstrapper = null)
    {
        _bootstrapper = bootstrapper;
    }

    /// <summary>
    /// Instala um pacote do Winget de forma silenciosa e reporta o progresso em tempo real.
    /// </summary>
    public async Task<WingetExecutionResult> InstallAppAsync(string appId, Action<string>? onLogReceived = null, CancellationToken cancellationToken = default)
    {
        // Mesma garantia do /auto (ver WingetBootstrapper/AutoInstallCliService), só que pro
        // caminho da UI (usuário clicando "Instalar" no executável, sem CLI): a primeira
        // instalação da sessão confirma que o winget está funcional (e baixa/instala o que
        // faltar, se não estiver) ANTES de tentar rodar o comando — evita que o usuário veja
        // "winget.exe não encontrado" logo após o primeiro logon, quando o Windows ainda pode
        // estar terminando de provisionar os pacotes APPX de sistema. Instalações seguintes
        // reaproveitam o resultado já checado (ver EnsureWingetBootstrappedOnceAsync) — não
        // roda de novo a cada clique em "Instalar".
        var bootstrapResult = await EnsureWingetBootstrappedOnceAsync(onLogReceived);
        if (!bootstrapResult.IsUsable)
        {
            return new WingetExecutionResult
            {
                Success = false,
                ExitCode = -1,
                Output = $"Winget não está disponível e não foi possível deixá-lo funcional: {bootstrapResult.ErrorMessage}"
            };
        }

        // --source winget: sem isso, o winget precisa resolver o --id consultando TODAS as
        // sources configuradas (winget + msstore) antes de decidir qual usar. Em ambientes
        // sem acesso íntegro aos serviços da Microsoft Store (ex.: Windows Sandbox, redes
        // restritas), a pesquisa na source "msstore" falha ("Falha na pesquisa da origem:
        // msstore") e derruba o comando inteiro mesmo quando o pacote existe na source
        // "winget". UpdateAppAsync e o script exportado (PackagesPage) já fixam a source por
        // esse mesmo motivo — faltava só aqui. --disable-interactivity evita qualquer prompt
        // de confirmação de source ficar esperando input que nunca chega (stdin não é
        // redirecionado nesse Process).
        string args = $"install --id \"{appId}\" --exact --source winget --silent --disable-interactivity --accept-source-agreements --accept-package-agreements";
        return await ExecuteWingetCommandAsync(args, onLogReceived, cancellationToken);
    }

    /// <summary>
    /// Desinstala um pacote silenciosamente.
    /// </summary>
    public async Task<WingetExecutionResult> UninstallAppAsync(string appId, Action<string>? onLogReceived = null, CancellationToken cancellationToken = default)
    {
        // Faltava --accept-source-agreements aqui (presente no Install e no export):
        // sem ela, winget uninstall precisa consultar a source para resolver o --exact
        // e, se o acordo da source ainda não tiver sido aceito no perfil elevado em que
        // a Store roda (requireAdministrator), fica esperando uma confirmação
        // interativa que nunca chega (stdin não é redirecionado) e a operação sempre
        // termina em falha/timeout.
        //
        // --source winget: mesmo motivo do InstallAppAsync — como o comando ainda consulta
        // uma source pra resolver o --exact (comentário acima), sem fixar a source o winget
        // tentaria também a "msstore", que pode falhar em ambientes sem acesso íntegro aos
        // serviços da Store (ex.: Windows Sandbox) e derrubar a desinstalação por um motivo
        // que não tem nada a ver com o pacote sendo removido.
        string args = $"uninstall --id \"{appId}\" --exact --source winget --silent --disable-interactivity --accept-source-agreements";
        return await ExecuteWingetCommandAsync(args, onLogReceived, cancellationToken);
    }

    /// <summary>
    /// Atualiza um pacote específico via "winget update" (alias de "winget upgrade"),
    /// silenciosamente e sem interação — mesmos argumentos usados no InstallAppAsync,
    /// mais --include-unknown (necessário pro winget atualizar apps cuja versão instalada
    /// ele não consegue detectar com certeza) e --force (ignora hash mismatch/instalador
    /// já baixado em cache desatualizado).
    /// </summary>
    public async Task<WingetExecutionResult> UpdateAppAsync(string appId, Action<string>? onLogReceived = null, CancellationToken cancellationToken = default)
    {
        // Mesma garantia do InstallAppAsync (ver comentário lá) — cobre quem chega direto
        // na tela Atualizações antes de qualquer instalação ter disparado o bootstrap.
        var bootstrapResult = await EnsureWingetBootstrappedOnceAsync(onLogReceived);
        if (!bootstrapResult.IsUsable)
        {
            return new WingetExecutionResult
            {
                Success = false,
                ExitCode = -1,
                Output = $"Winget não está disponível e não foi possível deixá-lo funcional: {bootstrapResult.ErrorMessage}"
            };
        }

        string args = $"update --id \"{appId}\" --exact --source winget --accept-source-agreements --disable-interactivity --silent --include-unknown --accept-package-agreements --force";
        return await ExecuteWingetCommandAsync(args, onLogReceived, cancellationToken);
    }

    /// <summary>
    /// Lista os pacotes com atualização pendente ("winget upgrade"). Diferente de
    /// GetInstalledPackageIdsAsync (que usa "winget export", com saída JSON), esse comando
    /// não tem opção de saída estruturada — a resposta é a tabela de texto padrão do
    /// console, parseada por WingetUpgradeListParser. --include-unknown inclui pacotes cuja
    /// versão instalada o winget não consegue confirmar (comuns em apps instalados fora do
    /// winget), pra não esconder atualizações reais só por causa disso.
    /// </summary>
    /// <exception cref="InvalidOperationException">
    /// O winget não está disponível e o bootstrap automático não conseguiu deixá-lo
    /// funcional (ver <see cref="WingetBootstrapResult.ErrorMessage"/>). Lançar aqui (em vez
    /// de devolver lista vazia) evita que "nenhuma atualização" seja confundido com "tudo em
    /// dia" quando na verdade o winget nem rodou — quem chama já trata isso num try/catch
    /// (ver UpdatesPage.CheckUpdatesButton_Click).
    /// </exception>
    public async Task<List<UpgradablePackage>> GetUpgradablePackagesAsync(Action<string>? onLogReceived = null, CancellationToken cancellationToken = default)
    {
        var bootstrapResult = await EnsureWingetBootstrappedOnceAsync(onLogReceived);
        if (!bootstrapResult.IsUsable)
        {
            throw new InvalidOperationException(
                $"Winget não está disponível e não foi possível deixá-lo funcional: {bootstrapResult.ErrorMessage}");
        }

        string args = "upgrade --include-unknown --accept-source-agreements --disable-interactivity";
        // onLogReceived não é passado pra execução do comando em si — a saída aqui é a
        // tabela padrão do console (cabeçalhos, colunas), não faz sentido linha a linha
        // como "log" de progresso; o parsing estruturado é feito por WingetUpgradeListParser
        // logo abaixo. onLogReceived serve só pro bootstrap (chamado acima).
        var result = await ExecuteWingetCommandAsync(args, onLogReceived: null, cancellationToken);

        return WingetUpgradeListParser.Parse(result.Output);
    }

    /// <summary>
    /// Lista os Package Ids atualmente instalados via "winget export" (JSON estruturado
    /// gerado pelo próprio winget), em vez de parsear a saída de texto do console —
    /// evita quebra por locale/versão do winget. Usado pela reconciliação de perfis
    /// e pela contagem de updates.
    /// </summary>
    public async Task<List<string>> GetInstalledPackageIdsAsync(CancellationToken cancellationToken = default)
    {
        string tempFile = Path.Combine(Path.GetTempPath(), $"winprovision-export-{Guid.NewGuid():N}.json");

        try
        {
            string args = $"export -o \"{tempFile}\" --accept-source-agreements --include-versions";
            var result = await ExecuteWingetCommandAsync(args, onLogReceived: null, cancellationToken);

            if (!result.Success || !File.Exists(tempFile))
                return [];

            string json = await File.ReadAllTextAsync(tempFile, cancellationToken);
            return ParseInstalledIdsFromExportJson(json);
        }
        finally
        {
            TryDeleteFile(tempFile);
        }
    }

    private static List<string> ParseInstalledIdsFromExportJson(string json)
    {
        var ids = new List<string>();

        try
        {
            using var document = JsonDocument.Parse(json);

            if (!document.RootElement.TryGetProperty("Sources", out var sources))
                return ids;

            foreach (var source in sources.EnumerateArray())
            {
                if (!source.TryGetProperty("Packages", out var packages))
                    continue;

                foreach (var package in packages.EnumerateArray())
                {
                    if (package.TryGetProperty("PackageIdentifier", out var idProp) &&
                        idProp.GetString() is { Length: > 0 } id)
                    {
                        ids.Add(id);
                    }
                }
            }
        }
        catch (JsonException)
        {
            // Export corrompido/incompleto: melhor devolver lista vazia do que derrubar
            // a reconciliação — quem chama trata "nada instalado detectado" como seguro.
        }

        return ids;
    }

    private static void TryDeleteFile(string path)
    {
        try
        {
            if (File.Exists(path))
                File.Delete(path);
        }
        catch
        {
            // Arquivo temporário órfão não é crítico; melhor esforço de limpeza.
        }
    }

    /// <summary>
    /// Dispara <see cref="WingetBootstrapper.EnsureWingetAsync"/> na primeira chamada e
    /// cacheia a Task resultante pro resto da vida do processo — todas as instalações
    /// seguintes (inclusive concorrentes, graças ao lock) reaproveitam o MESMO resultado em
    /// vez de rodar "winget --version" de novo a cada clique em "Instalar". Isso cobre o
    /// cenário do executável (WinProvision.Store.exe) sendo usado direto pelo usuário, sem
    /// CLI — o /auto já tinha essa garantia via AutoInstallCliService.
    ///
    /// Usa CancellationToken.None pra Task compartilhada de propósito: se a PRIMEIRA
    /// instalação que disparou o bootstrap for cancelada pelo usuário, isso não deve
    /// interromper o download/instalação do winget em si nem invalidar o cache pras
    /// próximas tentativas — só a instalação do app específico é cancelada.
    /// </summary>
    private Task<WingetBootstrapResult> EnsureWingetBootstrappedOnceAsync(Action<string>? onLogReceived)
    {
        if (_bootstrapper is null)
        {
            // Sem WingetBootstrapper injetado (ex.: WinProvision.ConsoleDemo, que instancia
            // sem DI) — mantém o comportamento de sempre: tenta rodar o winget.exe direto.
            return Task.FromResult(new WingetBootstrapResult(WingetBootstrapStatus.AlreadyAvailable));
        }

        lock (_bootstrapLock)
        {
            _bootstrapTask ??= _bootstrapper.EnsureWingetAsync(onLogReceived, CancellationToken.None);
            return _bootstrapTask;
        }
    }

    private static async Task<WingetExecutionResult> ExecuteWingetCommandAsync(string arguments, Action<string>? onLogReceived, CancellationToken cancellationToken)
    {
        var outputBuilder = new StringBuilder();

        var startInfo = new ProcessStartInfo
        {
            FileName = "winget.exe",
            Arguments = arguments,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8
        };

        using var process = new Process { StartInfo = startInfo };

        process.OutputDataReceived += (_, e) =>
        {
            if (e.Data == null) return;
            outputBuilder.AppendLine(e.Data);
            onLogReceived?.Invoke(e.Data);
        };

        process.ErrorDataReceived += (_, e) =>
        {
            if (e.Data == null) return;
            outputBuilder.AppendLine(e.Data);
            onLogReceived?.Invoke(e.Data);
        };

        try
        {
            process.Start();
            process.BeginOutputReadLine();
            process.BeginErrorReadLine();

            await process.WaitForExitAsync(cancellationToken);

            return new WingetExecutionResult
            {
                Success = process.ExitCode == 0,
                ExitCode = process.ExitCode,
                Output = outputBuilder.ToString()
            };
        }
        catch (OperationCanceledException)
        {
            // Cancelamento vindo do orquestrador (WPF/PowerShell): mata o processo
            // em vez de deixar o winget.exe orfão rodando em segundo plano.
            TryKill(process);
            return new WingetExecutionResult
            {
                Success = false,
                ExitCode = -1,
                Output = outputBuilder.Append("Operação cancelada pelo usuário.").ToString()
            };
        }
        catch (Exception ex)
        {
            TryKill(process);
            return new WingetExecutionResult
            {
                Success = false,
                ExitCode = -1,
                Output = $"Falha ao executar o Winget: {ex.Message}"
            };
        }
    }

    private static void TryKill(Process process)
    {
        try
        {
            if (!process.HasExited)
                process.Kill(entireProcessTree: true);
        }
        catch
        {
            // Processo já pode ter saído entre a checagem e o Kill; sem ação necessária.
        }
    }
}
