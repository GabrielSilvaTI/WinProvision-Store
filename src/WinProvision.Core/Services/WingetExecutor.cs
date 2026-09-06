using System.Diagnostics;
using System.IO;
using System.Linq;
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

    /// <summary>
    /// Motivo canônico da falha (ver <see cref="WingetErrorTranslator"/>), já classificado a
    /// partir de <see cref="Output"/> — permite que a UI mostre sempre a mesma mensagem em
    /// pt-BR em vez de repassar o texto cru do winget, cuja cobertura de tradução é
    /// inconsistente (a mesma falha às vezes sai em inglês, às vezes em português).
    /// Só é preenchido quando <see cref="Success"/> é false.
    /// </summary>
    public WingetFailureReason FailureReason { get; set; } = WingetFailureReason.Unknown;
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
        return await ExecuteWithElevationFallbackAsync(args, onLogReceived, cancellationToken);
    }

    /// <summary>
    /// Desinstala um pacote silenciosamente.
    /// </summary>
    public async Task<WingetExecutionResult> UninstallAppAsync(string appId, Action<string>? onLogReceived = null, CancellationToken cancellationToken = default)
    {
        // Mesma garantia do InstallAppAsync (ver comentário lá) — faltava aqui. Sem essa
        // checagem, se a desinstalação for a primeira operação da sessão (winget ainda não
        // provisionado, ex.: logo após o primeiro logon), o processo "winget.exe" nem existe
        // no PATH e o comando abaixo falha sempre, pra qualquer app — não é um problema de
        // pacote específico, é winget.exe simplesmente não estar disponível ainda.
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

        // SEM --source winget aqui, de propósito (diferente do Install/Update): esse é um
        // bug conhecido do próprio winget-cli (ex.: microsoft/winget-cli#5683, #4875,
        // #3690) — "uninstall --id X --exact --source winget" falha quase sempre com "No
        // installed package found matching input criteria", mesmo com o pacote instalado
        // e visível em "winget list", porque a correlação interna entre pacote instalado e
        // a source de origem não é confiável. Pra uninstall/update, o winget já resolve o
        // --exact contra a lista local de instalados sem precisar de --source; restringir
        // a source só quebra esse casamento. --accept-source-agreements continua aqui por
        // segurança (algumas builds ainda consultam a source pra outros metadados).
        //
        // ESCOPO: a Store não roda mais elevada por padrão (ver app.manifest) — rodar
        // elevada fazia o winget se recusar a desinstalar pacotes em escopo "user" (a
        // maioria, já que é o padrão de instalação), mesmo estando visíveis em "winget
        // list". Sem --scope, deixamos o winget decidir primeiro (cobre o caso comum);
        // falhando, força "user"; falhando ainda, tenta "machine" (cobre quem foi
        // instalado nesse escopo explicitamente, e ExecuteWithElevationFallbackAsync já
        // pede UAC pontual se for isso que falta). Cada tentativa é sequencial e para no
        // primeiro sucesso.
        string baseArgs = $"uninstall --id \"{appId}\" --exact --silent --disable-interactivity --accept-source-agreements";

        var result = await ExecuteWithElevationFallbackAsync(baseArgs, onLogReceived, cancellationToken);
        if (result.Success || cancellationToken.IsCancellationRequested)
        {
            return result;
        }
        if (result.FailureReason == WingetFailureReason.Unknown)
        {
            result.FailureReason = WingetErrorTranslator.Classify(result.Output);
        }

        // UserScopeElevationConflict só deve aparecer se a Store for lançada elevada por
        // fora (ex.: "Executar como administrador" no menu de contexto) — por padrão ela
        // não roda mais assim (ver app.manifest). Quando acontece, é um bloqueio do winget
        // baseado no nível de privilégio do PROCESSO chamador, não no --scope do comando:
        // repetir com --scope user, --scope machine ou pelo Name (abaixo) bate na mesma
        // restrição pra esse pacote, só atrasando a resposta. Sai já
        // com o motivo classificado pra UI mostrar uma mensagem fixa e acionável em pt-BR.
        if (result.FailureReason == WingetFailureReason.UserScopeElevationConflict)
        {
            return result;
        }

        foreach (string scope in new[] { "user", "machine" })
        {
            var scopedResult = await ExecuteWingetCommandAsync($"{baseArgs} --scope {scope}", onLogReceived, cancellationToken);
            if (scopedResult.Success || cancellationToken.IsCancellationRequested)
            {
                return scopedResult;
            }

            scopedResult.FailureReason = WingetErrorTranslator.Classify(scopedResult.Output);
            if (scopedResult.FailureReason == WingetFailureReason.UserScopeElevationConflict)
            {
                return scopedResult;
            }

            result = scopedResult;
        }

        // Último recurso: casar por --id, mesmo com --exact e os 3 escopos, ainda pode
        // falhar com "No installed package found matching input criteria" mesmo com o
        // pacote instalado e visível em "winget list" — bug real do winget-cli (ex.:
        // microsoft/winget-cli#1711, #3093, #4066): a correlação entre o Id do manifesto e
        // a entrada de Apps & Features local às vezes se perde (o Id do manifesto mudou,
        // ou o pacote nunca foi corretamente indexado por Id). Nesses relatos, desinstalar
        // pelo NOME exibido em "winget list" (em vez do Id) segue funcionando, porque o
        // Name vem direto da própria entrada local, sem depender dessa correlação. Por
        // isso, buscamos o Name local via "winget list --id X --exact" e tentamos de novo
        // com ele.
        var listResult = await ExecuteWingetCommandAsync(
            $"list --id \"{appId}\" --exact --disable-interactivity --accept-source-agreements",
            onLogReceived: null,
            cancellationToken);

        if (cancellationToken.IsCancellationRequested)
        {
            return result;
        }

        string? localName = WingetUpgradeListParser.Parse(listResult.Output).FirstOrDefault()?.Name;
        if (string.IsNullOrWhiteSpace(localName) || localName == appId)
        {
            return result;
        }

        string nameArgs = $"uninstall \"{localName}\" --exact --silent --disable-interactivity --accept-source-agreements";
        var byNameResult = await ExecuteWithElevationFallbackAsync(nameArgs, onLogReceived, cancellationToken);
        if (byNameResult.Success)
        {
            return byNameResult;
        }

        if (byNameResult.FailureReason == WingetFailureReason.Unknown)
        {
            byNameResult.FailureReason = WingetErrorTranslator.Classify(byNameResult.Output);
        }

        return byNameResult.FailureReason is WingetFailureReason.UserScopeElevationConflict or WingetFailureReason.ElevationCanceled
            ? byNameResult
            : result;
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
        return await ExecuteWithElevationFallbackAsync(args, onLogReceived, cancellationToken);
    }

    /// <summary>
    /// Roda o comando winget sem elevação (comportamento padrão desde que a Store deixou de
    /// exigir requireAdministrator — ver app.manifest). Se falhar por falta de privilégio
    /// (<see cref="WingetFailureReason.ElevationRequired"/> — típico de pacote em escopo
    /// "machine"), relança SÓ ESSE comando elevado via <see cref="ElevatedProcessRunner"/>
    /// (prompt de UAC pontual), em vez de exigir que a Store inteira rode como Administrador.
    /// </summary>
    private static async Task<WingetExecutionResult> ExecuteWithElevationFallbackAsync(
        string arguments, Action<string>? onLogReceived, CancellationToken cancellationToken)
    {
        var result = await ExecuteWingetCommandAsync(arguments, onLogReceived, cancellationToken);
        if (result.Success || cancellationToken.IsCancellationRequested)
        {
            return result;
        }

        result.FailureReason = WingetErrorTranslator.Classify(result.Output);
        if (result.FailureReason != WingetFailureReason.ElevationRequired)
        {
            return result;
        }

        onLogReceived?.Invoke("Requer privilégios de administrador — solicitando elevação (UAC)...");
        var elevatedResult = await ElevatedProcessRunner.RunElevatedAsync("winget.exe", arguments, cancellationToken);
        if (!elevatedResult.Success && elevatedResult.FailureReason == WingetFailureReason.Unknown)
        {
            elevatedResult.FailureReason = WingetErrorTranslator.Classify(elevatedResult.Output);
        }

        return elevatedResult;
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
    /// Obtém o tamanho do instalador sem baixar o pacote — usado só como fallback ao
    /// vivo para pacotes que não têm InstallerSizeBytes já calculado no catálogo (ver
    /// AppEntry.InstallerSizeBytes, preenchido pelo Indexer na sincronização diária a
    /// partir da própria InstallerUrl do manifesto winget-pkgs — não existe um campo
    /// de tamanho no schema oficial do winget, então essa é a única fonte confiável).
    /// Consulta apenas os headers HTTP (HEAD/Range) das URLs de instalador reportadas
    /// por "winget show", sem baixar o instalador inteiro.
    /// </summary>
    public async Task<long?> GetPackageInstallerSizeAsync(string appId, CancellationToken cancellationToken = default)
    {
        var bootstrapResult = await EnsureWingetBootstrappedOnceAsync(null);
        if (!bootstrapResult.IsUsable)
            return null;

        string args = $"show --id \"{appId}\" --exact --source winget --accept-source-agreements --disable-interactivity --locale en-US";
        var result = await ExecuteWingetCommandAsync(args, null, cancellationToken);
        if (string.IsNullOrWhiteSpace(result.Output))
            return null;

        // Limitado às 2 primeiras URLs (ex.: pacotes com várias arquiteturas/instaladores
        // como o VLC chegam a ter 3-4). Cada URL pode disparar até duas requisições HTTP
        // (HEAD + fallback via Range GET), então testar todas as URLs de um pacote com
        // muitos instaladores podia levar bem mais de um minuto e deixar a tela presa em
        // "Calculando…" por tempo demais — a primeira URL que responder já é suficiente.
        foreach (string url in ParseInstallerUrls(result.Output).Take(2))
        {
            long? remoteSize = await InstallerSizeResolver.TryGetRemoteContentLengthAsync(url, cancellationToken);
            if (remoteSize is > 0)
                return remoteSize;
        }

        return null;
    }

    private static IEnumerable<string> ParseInstallerUrls(string output)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (string rawLine in output.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries))
        {
            int separator = rawLine.IndexOf(':');
            if (separator < 0)
                continue;

            string label = rawLine[..separator].Trim();
            if (!label.Contains("Installer Url", StringComparison.OrdinalIgnoreCase) &&
                !label.Equals("InstallerURL", StringComparison.OrdinalIgnoreCase))
                continue;

            string url = rawLine[(separator + 1)..].Trim().Trim('\"');
            if (Uri.TryCreate(url, UriKind.Absolute, out var uri) &&
                (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps) &&
                seen.Add(uri.AbsoluteUri))
            {
                yield return uri.AbsoluteUri;
            }
        }
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
