using System;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net.Http;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace WinProvision.Core.Services;

/// <summary>Resultado de <see cref="WingetBootstrapper.EnsureWingetAsync"/>.</summary>
public enum WingetBootstrapStatus
{
    /// <summary>Winget já estava disponível — nada precisou ser baixado/instalado.</summary>
    AlreadyAvailable,

    /// <summary>Winget não estava disponível, mas o bootstrap baixou e instalou as dependências com sucesso.</summary>
    Bootstrapped,

    /// <summary>Winget não estava disponível e o bootstrap não conseguiu deixá-lo funcional (ver ErrorMessage).</summary>
    Failed,
}

/// <summary>Resultado detalhado de uma tentativa de bootstrap.</summary>
public record WingetBootstrapResult(WingetBootstrapStatus Status, string? ErrorMessage = null)
{
    /// <summary>True se, ao final, dá pra confiar que "winget.exe" funciona (já estava OK ou o bootstrap resolveu).</summary>
    public bool IsUsable => Status != WingetBootstrapStatus.Failed;
}

/// <summary>
/// Garante que o winget (Microsoft.DesktopAppInstaller) está funcional antes do modo CLI
/// /auto tentar instalar qualquer coisa com ele (apps winget e também Office/ODT — ver
/// <see cref="Office.OfficeDeploymentToolService"/>, que também depende do winget pra obter
/// o Office Deployment Tool).
///
/// Cenário-alvo: First Logon Commands numa sessão interativa recém-criada — nesse ponto o
/// Windows pode ainda não ter terminado de provisionar os pacotes APPX de sistema (inclusive
/// o próprio App Installer), então "winget" simplesmente não existe ainda, mesmo em imagens
/// onde ele normalmente estaria presente (ex.: Windows Sandbox, imagens enxutas/customizadas,
/// contas novas). Em vez de deixar o /auto inteiro falhar por causa disso, este serviço
/// detecta a ausência e baixa/instala offline os artefatos oficiais publicados pelo próprio
/// time do winget-cli, direto do GitHub, sempre da MESMA release (via "/latest/download",
/// que redireciona pro asset mais recente sem precisar consultar a API do GitHub antes):
///
///   1. <c>DesktopAppInstaller_Dependencies.zip</c> — contém, por arquitetura (x86/x64/arm64),
///      os .appx de framework (VCLibs, WindowsAppRuntime/UI.Xaml conforme a release) já na
///      versão EXATA que aquele build específico do winget espera. Isso é o que resolve o
///      erro clássico "HRESULT 0x80073CF3 — falha na dependência ou validação de conflito":
///      ele geralmente não é "a dependência está faltando", e sim "a versão instalada não
///      bate com a que o bundle exige" — algo que só acontece quando as dependências são
///      baixadas de um lugar separado/pinado manualmente, fora de sincronia com a versão do
///      winget. A QUANTIDADE e os NOMES desses .appx mudam entre releases sem aviso (ex.: uma
///      release trocou Microsoft.UI.Xaml.2.8 por Microsoft.WindowsAppRuntime.1.8 e passou a
///      trazer o VCLibs em dois arquivos em vez de um) — por isso o bootstrap não fixa nomes
///      específicos, instala TODOS os .appx que existirem na pasta da arquitetura.
///   2. <c>Microsoft.DesktopAppInstaller_8wekyb3d8bbwe.msixbundle</c> — o próprio winget.
///
/// Os dois arquivos (.zip + .msixbundle) são baixados em paralelo (não há dependência entre
/// os downloads em si, só na instalação) pra minimizar o tempo total. Usa "Add-AppxPackage"
/// via PowerShell, instalando o bundle e todas as dependências extraídas numa única transação
/// de deployment (-DependencyPath) — registra os pacotes pro usuário atual, sem precisar da
/// Store nem de um arquivo de licença .xml (diferente de Add-AppxProvisionedPackage/DISM, que
/// é pra imagens offline/todos os usuários) — compatível com o app já rodando elevado
/// (app.manifest = requireAdministrator) dentro da sessão do usuário atual.
/// </summary>
[SupportedOSPlatform("windows")]
public class WingetBootstrapper
{
    // Nomes de asset fixos entre releases do winget-cli — dá pra confiar no redirecionamento
    // "/latest/download" do GitHub pra sempre pegar a build mais recente, e como os dois vêm
    // da MESMA release, as dependências dentro do .zip sempre batem com o que o .msixbundle
    // daquela release espera (é essa sincronia que corrige o 0x80073CF3) — mesmo quando a
    // composição interna do .zip muda entre releases (ver comentário da classe).
    private const string DependenciesZipUrl = "https://github.com/microsoft/winget-cli/releases/latest/download/DesktopAppInstaller_Dependencies.zip";
    private const string DesktopAppInstallerUrl = "https://github.com/microsoft/winget-cli/releases/latest/download/Microsoft.DesktopAppInstaller_8wekyb3d8bbwe.msixbundle";

    private static readonly TimeSpan VersionCheckTimeout = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan DownloadTimeout = TimeSpan.FromMinutes(3);
    private static readonly TimeSpan AddAppxTimeout = TimeSpan.FromMinutes(2);

    // Sem fallback de URL (as duas fontes já são os assets oficiais do winget-cli, via
    // redirect "/latest/download" do GitHub) — só retry simples, mesma ideia do
    // -MaximumRetryCount/-RetryIntervalSec do Invoke-WebRequest: rede instável logo após o
    // primeiro logon (adaptador ainda subindo, DNS resolvendo) é o caso comum que isso cobre.
    private const int MaxDownloadAttempts = 3;
    private static readonly TimeSpan DownloadRetryDelay = TimeSpan.FromSeconds(2);

    /// <summary>
    /// Roda "winget --version" e só considera disponível se o processo iniciar E retornar
    /// código de saída 0 — cobre tanto "winget.exe não existe no PATH" (Process.Start lança)
    /// quanto "existe o stub da Store mas o App Installer de verdade ainda não terminou de
    /// provisionar por trás dele" (esse stub roda e falha rápido com código != 0, não trava
    /// esperando input, então o timeout aqui é só uma rede de segurança extra).
    /// </summary>
    public async Task<bool> IsWingetAvailableAsync(CancellationToken ct = default)
    {
        Process? process = null;
        try
        {
            var startInfo = new ProcessStartInfo
            {
                FileName = "winget.exe",
                Arguments = "--version",
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };

            process = Process.Start(startInfo);
            if (process is null) return false;

            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeoutCts.CancelAfter(VersionCheckTimeout);

            await process.WaitForExitAsync(timeoutCts.Token);
            return process.ExitCode == 0;
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            // Timeout local (não foi cancelamento do chamador): trata como "não disponível".
            return false;
        }
        catch
        {
            // Caso normal de ausência: Win32Exception "arquivo não encontrado". Qualquer
            // outra falha ao tentar rodar também vira "não disponível" — é exatamente o
            // cenário que EnsureWingetAsync existe pra tentar resolver.
            return false;
        }
        finally
        {
            if (process is { HasExited: false })
            {
                TryKill(process);
            }
            process?.Dispose();
        }
    }

    /// <summary>
    /// Garante que o winget está utilizável: se já estiver, não faz nada e retorna na hora.
    /// Se não estiver, baixa o pacote oficial de dependências + o instalador do winget (em
    /// paralelo), instala tudo na ordem certa e reconfirma no final.
    /// </summary>
    public async Task<WingetBootstrapResult> EnsureWingetAsync(Action<string>? log = null, CancellationToken ct = default)
    {
        void Log(string message) => log?.Invoke(message);

        if (await IsWingetAvailableAsync(ct))
        {
            return new WingetBootstrapResult(WingetBootstrapStatus.AlreadyAvailable);
        }

        Log("[WinProvision] Winget ainda não está disponível (comum logo após o primeiro logon, " +
            "enquanto o Windows termina de provisionar os pacotes APPX). Baixando via GitHub (winget-cli)...");

        string workDir = Path.Combine(Path.GetTempPath(), $"winprovision-winget-bootstrap-{Guid.NewGuid():N}");
        string extractDir = Path.Combine(workDir, "deps");
        Directory.CreateDirectory(workDir);

        try
        {
            string zipPath = Path.Combine(workDir, "DesktopAppInstaller_Dependencies.zip");
            string bundlePath = Path.Combine(workDir, "Microsoft.DesktopAppInstaller_8wekyb3d8bbwe.msixbundle");

            // Download mais rápido: os dois assets não dependem um do outro pra serem
            // baixados (só a instalação tem ordem), então baixa em paralelo em vez de
            // sequencial — normalmente corta quase pela metade o tempo total dessa etapa.
            Log("[WinProvision]   Baixando dependências (.zip) e o instalador do winget (.msixbundle) em paralelo...");
            try
            {
                await Task.WhenAll(
                    DownloadFileAsync(DependenciesZipUrl, zipPath, ct),
                    DownloadFileAsync(DesktopAppInstallerUrl, bundlePath, ct));
            }
            catch (Exception ex)
            {
                string error = $"Falha ao baixar os artefatos do winget-cli no GitHub: {ex.Message}";
                Log($"[WinProvision]   {error}");
                return new WingetBootstrapResult(WingetBootstrapStatus.Failed, error);
            }

            Log("[WinProvision]   Extraindo dependências...");
            try
            {
                ZipFile.ExtractToDirectory(zipPath, extractDir);
            }
            catch (Exception ex)
            {
                string error = $"Falha ao extrair DesktopAppInstaller_Dependencies.zip: {ex.Message}";
                Log($"[WinProvision]   {error}");
                return new WingetBootstrapResult(WingetBootstrapStatus.Failed, error);
            }

            string archFolder = ResolveArchitectureFolderName();
            string archDir = Path.Combine(extractDir, archFolder);

            if (!Directory.Exists(archDir))
            {
                string error = $"O .zip de dependências não tem uma pasta \"{archFolder}\" (arquitetura do processo atual: {RuntimeInformation.ProcessArchitecture}).";
                Log($"[WinProvision]   {error}");
                return new WingetBootstrapResult(WingetBootstrapStatus.Failed, error);
            }

            // Não fixa nomes/keywords específicos (ex.: "VCLibs", "UI.Xaml") de propósito —
            // a composição desse .zip já mudou entre releases do winget-cli sem aviso (na
            // release atual, por exemplo, o UI.Xaml.2.8 foi substituído pelo
            // Microsoft.WindowsAppRuntime.1.8, e o VCLibs passou a vir em dois arquivos
            // separados: o "base" e o ".UWPDesktop"). Em vez de tentar prever a composição,
            // instala TODOS os .appx que existirem na pasta da arquitetura — o que estiver
            // lá é, por definição, o que aquela release específica do winget espera.
            string[] dependencyAppxFiles = Directory.EnumerateFiles(archDir, "*.appx")
                .OrderBy(f => f, StringComparer.OrdinalIgnoreCase)
                .ToArray();

            if (dependencyAppxFiles.Length == 0)
            {
                string error = $"Nenhum arquivo .appx de dependência encontrado em \"{archDir}\".";
                Log($"[WinProvision]   {error}");
                return new WingetBootstrapResult(WingetBootstrapStatus.Failed, error);
            }

            Log($"[WinProvision]   Dependências encontradas ({dependencyAppxFiles.Length}): " +
                string.Join(", ", dependencyAppxFiles.Select(Path.GetFileName)));

            // Instala o bundle do App Installer e TODAS as dependências numa única transação
            // de deployment (-DependencyPath com a lista inteira) — é a forma recomendada pela
            // própria Microsoft pra instalação manual do winget, e evita qualquer problema de
            // ordem entre os framework packages (ex.: VCLibs.UWPDesktop pode depender do VCLibs
            // "base" já estar resolvido na mesma transação; registrar cada um em chamadas
            // separadas, como antes, arrisca instalar fora de ordem).
            Log("[WinProvision]   Instalando Microsoft.DesktopAppInstaller (winget) + dependências...");
            var wingetResult = await AddAppxPackageAsync(bundlePath, dependencyPaths: dependencyAppxFiles, ct);
            if (!wingetResult.Success)
            {
                string error = $"Falha ao instalar Microsoft.DesktopAppInstaller (winget): {wingetResult.Output}";
                Log($"[WinProvision]   {error}");
                return new WingetBootstrapResult(WingetBootstrapStatus.Failed, error);
            }
        }
        finally
        {
            TryDeleteDirectory(workDir);
        }

        // Reconfirma no final — o App Installer registra o alias "winget.exe" dentro de
        // %LocalAppData%\Microsoft\WindowsApps, pasta que já costuma estar no PATH do
        // usuário por padrão mesmo antes de o arquivo existir lá dentro, então o processo
        // atual normalmente já enxerga o winget recém-instalado sem precisar reiniciar nada.
        if (await IsWingetAvailableAsync(ct))
        {
            Log("[WinProvision] Winget instalado e funcional.");
            return new WingetBootstrapResult(WingetBootstrapStatus.Bootstrapped);
        }

        const string finalError = "As dependências foram instaladas, mas \"winget --version\" ainda falha. " +
            "Pode ser necessário reiniciar a sessão para o PATH ser atualizado.";
        Log($"[WinProvision]   {finalError}");
        return new WingetBootstrapResult(WingetBootstrapStatus.Failed, finalError);
    }

    /// <summary>
    /// Nome da subpasta dentro de DesktopAppInstaller_Dependencies.zip correspondente à
    /// arquitetura do processo atual (o app roda como win-x64 self-contained, então na
    /// prática isso sempre resolve pra "x64", mas cobre os outros casos por segurança).
    /// </summary>
    private static string ResolveArchitectureFolderName() => RuntimeInformation.ProcessArchitecture switch
    {
        Architecture.X64 => "x64",
        Architecture.X86 => "x86",
        Architecture.Arm64 => "arm64",
        Architecture.Arm => "arm",
        var other => other.ToString().ToLowerInvariant(),
    };

    /// <summary>
    /// Baixa um arquivo com retry simples (sem fallback de URL — ver <see cref="MaxDownloadAttempts"/>).
    /// Cada tentativa usa um HttpClient novo (o anterior pode ter ficado num estado ruim após
    /// falha de conexão) e apaga qualquer arquivo parcial antes de tentar de novo, pra nunca
    /// deixar um .zip/.msixbundle truncado passar pra extração/instalação.
    /// </summary>
    private static async Task DownloadFileAsync(string url, string destPath, CancellationToken ct)
    {
        for (int attempt = 1; attempt <= MaxDownloadAttempts; attempt++)
        {
            bool isLastAttempt = attempt == MaxDownloadAttempts;

            try
            {
                using var http = new HttpClient { Timeout = DownloadTimeout };

                // App Installer/GitHub servem esses assets via redirect ("/latest/download");
                // HttpClient segue redirects por padrão, então não precisa de tratamento especial aqui.
                using var response = await http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct);
                response.EnsureSuccessStatusCode();

                await using var httpStream = await response.Content.ReadAsStreamAsync(ct);
                await using var fileStream = File.Create(destPath);
                await httpStream.CopyToAsync(fileStream, ct);

                return;
            }
            // Só engole a exceção se ainda sobrar tentativa e não foi o chamador que
            // cancelou — na última tentativa o filtro falha e a exceção real
            // (HttpRequestException, TaskCanceledException por timeout, etc.) propaga
            // normal pro Task.WhenAll em EnsureWingetAsync, sem mensagem genérica no meio.
            catch when (!isLastAttempt && !ct.IsCancellationRequested)
            {
                // Descarta o arquivo parcial e espera um pouco antes de tentar de novo —
                // rede instável logo após o primeiro logon costuma ser só isso mesmo.
                TryDeleteFile(destPath);
                await Task.Delay(DownloadRetryDelay, ct);
            }
        }
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
            // Arquivo parcial órfão não é crítico — a próxima tentativa recria com File.Create.
        }
    }

    /// <summary>
    /// Instala um .appx/.msixbundle via "Add-AppxPackage" (PowerShell) — registra o pacote
    /// pro usuário atual, sem precisar de licença .xml nem da Store. Idempotente: rodar de
    /// novo com uma dependência que já está instalada não é um erro, o PowerShell só
    /// reinstala/atualiza no lugar.
    /// </summary>
    /// <param name="dependencyPaths">
    /// Caminhos de .appx de framework a passar via "-DependencyPath", resolvidos NA MESMA
    /// transação de deployment que <paramref name="filePath"/> — usado ao instalar o bundle
    /// do App Installer junto com todas as dependências extraídas de uma vez (ver comentário
    /// em EnsureWingetAsync). Null/vazio omite o parâmetro por completo.
    /// </param>
    private static async Task<(bool Success, string Output)> AddAppxPackageAsync(
        string filePath, string[]? dependencyPaths, CancellationToken ct)
    {
        string escapedPath = filePath.Replace("'", "''");
        string command = $"$ErrorActionPreference = 'Stop'; Add-AppxPackage -Path '{escapedPath}'";

        if (dependencyPaths is { Length: > 0 })
        {
            string depArg = string.Join(",", dependencyPaths.Select(p => $"'{p.Replace("'", "''")}'"));
            command += $" -DependencyPath {depArg}";
        }

        var startInfo = new ProcessStartInfo
        {
            FileName = "powershell.exe",
            Arguments = $"-NoProfile -NonInteractive -ExecutionPolicy Bypass -Command \"{command}\"",
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };

        using var process = new Process { StartInfo = startInfo };
        var output = new StringBuilder();

        process.OutputDataReceived += (_, e) => { if (e.Data is not null) output.AppendLine(e.Data); };
        process.ErrorDataReceived += (_, e) => { if (e.Data is not null) output.AppendLine(e.Data); };

        try
        {
            process.Start();
            process.BeginOutputReadLine();
            process.BeginErrorReadLine();

            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeoutCts.CancelAfter(AddAppxTimeout);

            await process.WaitForExitAsync(timeoutCts.Token);

            return (process.ExitCode == 0, output.ToString().Trim());
        }
        catch (OperationCanceledException)
        {
            TryKill(process);
            return (false, ct.IsCancellationRequested
                ? "Operação cancelada."
                : "Tempo esgotado esperando o Add-AppxPackage.");
        }
        catch (Exception ex)
        {
            TryKill(process);
            return (false, ex.Message);
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
            // Mesmo padrão do WingetExecutor.
        }
    }

    private static void TryDeleteDirectory(string path)
    {
        try
        {
            if (Directory.Exists(path))
                Directory.Delete(path, recursive: true);
        }
        catch
        {
            // Arquivos temporários órfãos não são críticos; melhor esforço de limpeza.
        }
    }
}
