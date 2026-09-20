using System.Diagnostics;
using System.Text;
using WinProvision.Core.Models.Office;
using WinProvision.Core.Services;

namespace WinProvision.Core.Services.Office;

/// <summary>
/// Obtém o Office Deployment Tool via download direto do R2 (método principal)
/// com fallback para winget (pacote Microsoft.OfficeDeploymentTool) caso o download falhe.
///
/// O configuration.xml não precisa ficar do lado do setup.exe: passamos o caminho
/// completo de cada um como argumento, então tanto faz onde o setup.exe está localizado.
/// </summary>
public class OfficeDeploymentToolService : IDisposable
{
    private const string OdtWingetPackageId = "Microsoft.OfficeDeploymentTool";
    private const string OdtDownloadUrl = "https://pub-166b41912a994dbe86583ba10596d673.r2.dev/Office/Instalador/setup.exe";

    /// <summary>
    /// Mesmo executável e argumento que o botão nativo "Atualizar agora" usa dentro de
    /// qualquer app do Office (Arquivo > Conta > Opções de Atualização > Atualizar
    /// Agora) — não é um mecanismo alternativo/hack, é o oficial da Microsoft.
    /// </summary>
    private const string OfficeC2RClientExePath =
        @"C:\Program Files\Common Files\microsoft shared\ClickToRun\OfficeC2RClient.exe";

    private readonly WingetExecutor _wingetExecutor;
    private readonly string _workRoot;
    private readonly HttpClient _httpClient;
    private readonly OfficeUninstallService _uninstallService;
    private bool _disposed;

    /// <summary>Pasta onde ficam o configuration.xml gerado e os logs desta ferramenta (não do ODT em si).</summary>
    public string WorkRoot => _workRoot;

    public OfficeDeploymentToolService(WingetExecutor wingetExecutor, string? workRoot = null)
    {
        _wingetExecutor = wingetExecutor;
        _workRoot = workRoot ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "WinProvision", "Office");
        _httpClient = new HttpClient { Timeout = TimeSpan.FromMinutes(10) };
        _uninstallService = new OfficeUninstallService();
    }

    public void Dispose()
    {
        if (!_disposed)
        {
            _httpClient.Dispose();
            _disposed = true;
        }
        GC.SuppressFinalize(this);
    }

    /// <summary>
    /// Locais conhecidos onde o setup.exe pode estar encontrado:
    /// 1. Pasta local do WinProvision (download do R2)
    /// 2. Program Files (instalação via winget)
    /// 3. Program Files (x86) (instalação via winget em sistemas 64-bit)
    /// </summary>
    private static IEnumerable<string> KnownInstallPaths()
    {
        // Local do download do R2
        yield return Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "WinProvision", "OfficeDeploymentTool", "setup.exe");

        // Locais de instalação via winget
        yield return Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
            "OfficeDeploymentTool", "setup.exe");

        yield return Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86),
            "OfficeDeploymentTool", "setup.exe");
    }

    /// <summary>
    /// Garante que setup.exe existe localmente, baixando do R2 primeiro (método principal)
    /// e usando winget como fallback caso o download falhe.
    /// </summary>
    public async Task<string> EnsureSetupExeAsync(Action<string>? onStatus = null, CancellationToken cancellationToken = default)
    {
        string? existing = KnownInstallPaths().FirstOrDefault(File.Exists);
        if (existing != null)
        {
            return existing;
        }

        // Tenta baixar do R2 primeiro (método principal)
        onStatus?.Invoke("Baixando Office Deployment Tool do R2...");
        try
        {
            string? downloadedPath = await DownloadFromR2Async(onStatus, cancellationToken);
            if (downloadedPath != null)
            {
                return downloadedPath;
            }
        }
        catch (Exception ex)
        {
            onStatus?.Invoke($"Falha ao baixar do R2: {ex.Message}. Tentando via winget...");
        }

        // Fallback: usar winget
        onStatus?.Invoke("Instalando Office Deployment Tool via winget (fallback)...");

        var result = await _wingetExecutor.InstallAppAsync(
            OdtWingetPackageId,
            onLogReceived: line => onStatus?.Invoke(line),
            cancellationToken: cancellationToken);

        if (!result.Success)
        {
            throw new InvalidOperationException($"Falha ao instalar o ODT via winget (código {result.ExitCode}). {result.Output}");
        }

        string? installed = KnownInstallPaths().FirstOrDefault(File.Exists);
        if (installed == null)
        {
            throw new FileNotFoundException(
                "winget reportou sucesso, mas setup.exe não foi encontrado em nenhum dos locais conhecidos.");
        }

        return installed;
    }

    /// <summary>
    /// Baixa o setup.exe do R2 e o coloca em um local conhecido.
    /// </summary>
    private async Task<string?> DownloadFromR2Async(Action<string>? onStatus, CancellationToken cancellationToken)
    {
        try
        {
            // Cria a pasta destino
            string localFolder = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "WinProvision", "OfficeDeploymentTool");
            Directory.CreateDirectory(localFolder);

            string localPath = Path.Combine(localFolder, "setup.exe");

            // Remove arquivo existente se houver
            if (File.Exists(localPath))
            {
                File.Delete(localPath);
            }

            // Baixa o arquivo
            using var response = await _httpClient.GetAsync(OdtDownloadUrl, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
            response.EnsureSuccessStatusCode();

            long totalBytes = response.Content.Headers.ContentLength ?? 0;
            long totalBytesRead = 0;

            using var contentStream = await response.Content.ReadAsStreamAsync(cancellationToken);
            using var fileStream = new FileStream(localPath, FileMode.Create, FileAccess.Write, FileShare.None);

            var buffer = new byte[8192];
            int bytesRead;
            var lastProgressUpdate = DateTime.UtcNow;

            while ((bytesRead = await contentStream.ReadAsync(buffer, 0, buffer.Length, cancellationToken)) > 0)
            {
                await fileStream.WriteAsync(buffer, 0, bytesRead, cancellationToken);
                totalBytesRead += bytesRead;

                // Atualiza progresso a cada 0.5 segundos para não spammar
                if ((DateTime.UtcNow - lastProgressUpdate).TotalSeconds >= 0.5 && totalBytes > 0)
                {
                    double percent = (totalBytesRead * 100.0) / totalBytes;
                    onStatus?.Invoke($"Baixando setup.exe: {percent:F1}%");
                    lastProgressUpdate = DateTime.UtcNow;
                }
            }

            onStatus?.Invoke("Download do setup.exe concluído.");
            return localPath;
        }
        catch (Exception ex)
        {
            onStatus?.Invoke($"Erro ao baixar do R2: {ex.Message}");
            return null;
        }
    }

    /// <summary>
    /// Gera o configuration.xml (na pasta de trabalho da WinProvision, não do ODT) e
    /// dispara setup.exe /configure com o caminho completo do arquivo usando OdtProcessRunner
    /// com elevação administrativa isolada (UAC prompt).
    /// </summary>
    public async Task<bool> RunConfigureAsync(OfficeInstallRequest request, Action<string>? onStatus = null, CancellationToken cancellationToken = default)
    {
        string setupPath = await EnsureSetupExeAsync(onStatus, cancellationToken);
        string configPath = await OfficeConfigXmlBuilder.WriteToFolderAsync(request, _workRoot, cancellationToken);

        onStatus?.Invoke($"Instalando {request.Plan.DisplayName}...");

        try
        {
            var result = await OdtProcessRunner.RunConfigureElevatedAsync(setupPath, configPath, cancellationToken);

            if (result.ElevationCanceled)
            {
                onStatus?.Invoke("Instalação cancelada pelo usuário (UAC).");
                return false;
            }

            bool success = result.Success;

            onStatus?.Invoke(success
                ? "Office instalado com sucesso."
                : $"setup.exe retornou código {result.ExitCode}.");

            if (!success && !string.IsNullOrWhiteSpace(result.Output))
            {
                onStatus?.Invoke($"Saída: {result.Output}");
            }

            return success;
        }
        catch (Exception ex)
        {
            onStatus?.Invoke($"Erro ao executar setup.exe: {ex.Message}");
            return false;
        }
    }

    /// <summary>
    /// Usa os métodos alternativos de desinstalação (OfficeUninstallService) para remover o Office.
    /// Pula o método oficial do ODT (setup.exe /configure) que estava dando erro 0-2048.
    /// Se <see cref="OfficeRemoveRequest.CleanStoreEdition"/> estiver marcado, também remove
    /// a edição da Microsoft Store do Office (pacote AppX).
    /// O OfficeUninstallService usa dois métodos:
    /// 1. Método padrão via OfficeClickToRun.exe (com elevação via ElevatedProcessRunner)
    /// 2. Método agressivo via GetHelpCmd OfficeScrubScenario (com elevação via ElevatedProcessRunner)
    /// </summary>
    public async Task<bool> RunRemoveAsync(OfficeRemoveRequest request, Action<string>? onStatus = null, CancellationToken cancellationToken = default)
    {
        string removeDescription = request.RemoveAll
            ? "todas as instalações do Office"
            : $"{request.ProductIds?.Count ?? 0} produto(s) do Office";

        onStatus?.Invoke($"Removendo {removeDescription}...");

        // Usa o OfficeUninstallService diretamente (pula o método oficial do ODT)
        var result = await _uninstallService.UninstallAsync(onStatus, cancellationToken);

        bool success = result == OfficeUninstallOutcome.RemovedByStandardMethod ||
                      result == OfficeUninstallOutcome.RemovedByAggressiveMethod;

        if (success && request.CleanStoreEdition)
        {
            onStatus?.Invoke("Verificando edição da Microsoft Store do Office...");
            success = await RemoveStoreEditionAsync(onStatus, cancellationToken) && success;
        }

        onStatus?.Invoke(success
            ? "Remoção concluída."
            : "Falha na remoção.");

        return success;
    }

    /// <summary>
    /// Remove a edição UWP/AppX do Office (distribuída pela Microsoft Store), que pode
    /// coexistir e conflitar com uma instalação Click-to-Run. Usa apenas o cmdlet
    /// nativo do PowerShell Remove-AppxPackage — nenhuma interação com licenciamento.
    /// Não falha a operação inteira se o pacote simplesmente não estiver presente.
    /// </summary>
    private static async Task<bool> RemoveStoreEditionAsync(Action<string>? onStatus, CancellationToken cancellationToken)
    {
        const string script = "Get-AppxPackage -Name 'Microsoft.Office.Desktop*' | Remove-AppxPackage -ErrorAction SilentlyContinue";

        var result = await RunProcessAsync(
            "powershell.exe",
            $"-NoProfile -ExecutionPolicy Bypass -Command \"{script}\"",
            Environment.CurrentDirectory,
            onLogReceived: line => onStatus?.Invoke(line),
            cancellationToken);

        // ExitCode diferente de 0 aqui normalmente só significa "nenhum pacote encontrado";
        // não tratamos como falha da remoção do Office em si.
        return true;
    }

    /// <summary>
    /// Aciona uma verificação/instalação de atualizações do Office Click-to-Run
    /// através do OfficeC2RClient.exe /update user — o mesmo comando que o botão
    /// nativo "Atualizar Agora" do Office dispara. Se houver atualização disponível
    /// no canal configurado, ela é baixada e aplicada de verdade (não é só uma
    /// checagem "de mentira").
    /// </summary>
    public async Task<bool> RunUpdateNowAsync(bool silent = true, Action<string>? onStatus = null, CancellationToken cancellationToken = default)
    {
        if (!File.Exists(OfficeC2RClientExePath))
        {
            onStatus?.Invoke("OfficeC2RClient.exe não encontrado — nenhuma instalação Click-to-Run detectada nesta máquina.");
            return false;
        }

        onStatus?.Invoke("Verificando e aplicando atualizações do Office...");

        var result = await RunProcessAsync(
            OfficeC2RClientExePath,
            $"/update user displaylevel={(silent ? "False" : "True")}",
            Path.GetDirectoryName(OfficeC2RClientExePath) ?? _workRoot,
            onLogReceived: line => onStatus?.Invoke(line),
            cancellationToken);

        // OfficeC2RClient.exe dispara o processo de atualização em segundo plano e
        // retorna rápido (não fica bloqueado até a atualização terminar) — por isso
        // um ExitCode 0 aqui significa "solicitação aceita", não "já atualizado".
        onStatus?.Invoke(result.Success
            ? "Atualização solicitada. O Office vai baixar/aplicar em segundo plano se houver algo novo no canal configurado."
            : $"OfficeC2RClient.exe retornou código {result.ExitCode}.");

        return result.Success;
    }

    /// <summary>
    /// Liga/desliga a atualização automática do Office sem reinstalar nada, aplicando
    /// só o elemento &lt;Updates Enabled="TRUE|FALSE"/&gt; via setup.exe /configure —
    /// o mecanismo que o próprio ODT documenta para essa política, usando OdtProcessRunner.
    /// </summary>
    public async Task<bool> RunSetAutoUpdateAsync(bool enabled, Action<string>? onStatus = null, CancellationToken cancellationToken = default)
    {
        string setupPath = await EnsureSetupExeAsync(onStatus, cancellationToken);
        string configPath = await OfficeConfigXmlBuilder.WriteUpdatesOnlyToFolderAsync(enabled, _workRoot, cancellationToken);

        onStatus?.Invoke(enabled ? "Ativando atualizações automáticas..." : "Desativando atualizações automáticas...");

        try
        {
            int exitCode = await OdtProcessRunner.RunConfigureAsync(setupPath, configPath, cancellationToken);
            bool success = exitCode == 0;

            onStatus?.Invoke(success
                ? $"Atualizações automáticas {(enabled ? "ativadas" : "desativadas")}."
                : $"setup.exe retornou código {exitCode}.");

            return success;
        }
        catch (Exception ex)
        {
            onStatus?.Invoke($"Erro ao executar setup.exe: {ex.Message}");
            return false;
        }
    }

    private static async Task<(bool Success, int ExitCode, string Output)> RunProcessAsync(
        string fileName, string arguments, string workingDirectory, Action<string>? onLogReceived, CancellationToken cancellationToken)
    {
        var outputBuilder = new StringBuilder();

        var startInfo = new ProcessStartInfo
        {
            FileName = fileName,
            Arguments = arguments,
            WorkingDirectory = workingDirectory,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
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

        process.Start();
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();

        try
        {
            await process.WaitForExitAsync(cancellationToken);
        }
        catch (OperationCanceledException)
        {
            TryKill(process);
            return (false, -1, outputBuilder.Append("Operação cancelada pelo usuário.").ToString());
        }

        return (process.ExitCode == 0, process.ExitCode, outputBuilder.ToString());
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
