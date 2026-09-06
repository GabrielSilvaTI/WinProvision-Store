using System.Diagnostics;
using System.Text;
using WinProvision.Core.Models.Office;
using WinProvision.Core.Services;

namespace WinProvision.Core.Services.Office;

/// <summary>
/// Obtém o Office Deployment Tool via winget (pacote Microsoft.OfficeDeploymentTool)
/// em vez de baixar direto do Download Center — o fluxo de confirmation.aspx/details.aspx
/// da Microsoft mudou/quebrou algumas vezes ao longo do tempo (relatos de 404 e de
/// redirecionamento pra página genérica), enquanto o manifesto do winget é mantido e
/// versionado pela comunidade em winget-pkgs, apontando sempre pra um InstallerUrl válido.
///
/// O configuration.xml não precisa ficar do lado do setup.exe: passamos o caminho
/// completo de cada um como argumento, então tanto faz onde o winget instalou o ODT.
/// </summary>
public class OfficeDeploymentToolService
{
    private const string OdtWingetPackageId = "Microsoft.OfficeDeploymentTool";

    /// <summary>
    /// Mesmo executável e argumento que o botão nativo "Atualizar agora" usa dentro de
    /// qualquer app do Office (Arquivo > Conta > Opções de Atualização > Atualizar
    /// Agora) — não é um mecanismo alternativo/hack, é o oficial da Microsoft.
    /// </summary>
    private const string OfficeC2RClientExePath =
        @"C:\Program Files\Common Files\microsoft shared\ClickToRun\OfficeC2RClient.exe";

    private readonly WingetExecutor _wingetExecutor;
    private readonly string _workRoot;

    /// <summary>Pasta onde ficam o configuration.xml gerado e os logs desta ferramenta (não do ODT em si).</summary>
    public string WorkRoot => _workRoot;

    public OfficeDeploymentToolService(WingetExecutor wingetExecutor, string? workRoot = null)
    {
        _wingetExecutor = wingetExecutor;
        _workRoot = workRoot ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "WinProvision", "Office");
    }

    /// <summary>
    /// Locais conhecidos onde o pacote winget do ODT termina instalado. Ver
    /// microsoft/winget-pkgs#350119: o auto-extrator do ODT é 32-bit, então em
    /// sistemas 64-bit ele resolve "%ProgramFiles%\OfficeDeploymentTool" para
    /// Program Files (x86) em vez de Program Files — por isso checamos os dois.
    /// </summary>
    private static IEnumerable<string> KnownInstallPaths()
    {
        yield return Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
            "OfficeDeploymentTool", "setup.exe");

        yield return Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86),
            "OfficeDeploymentTool", "setup.exe");
    }

    /// <summary>
    /// Garante que setup.exe existe localmente, instalando o pacote winget do ODT se
    /// necessário.
    /// </summary>
    public async Task<string> EnsureSetupExeAsync(Action<string>? onStatus = null, CancellationToken cancellationToken = default)
    {
        string? existing = KnownInstallPaths().FirstOrDefault(File.Exists);
        if (existing != null)
        {
            return existing;
        }

        onStatus?.Invoke("Instalando Office Deployment Tool via winget...");

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
    /// Gera o configuration.xml (na pasta de trabalho da WinProvision, não do ODT) e
    /// dispara setup.exe /configure com o caminho completo do arquivo.
    /// </summary>
    public async Task<bool> RunConfigureAsync(OfficeInstallRequest request, Action<string>? onStatus = null, CancellationToken cancellationToken = default)
    {
        string setupPath = await EnsureSetupExeAsync(onStatus, cancellationToken);
        string configPath = await OfficeConfigXmlBuilder.WriteToFolderAsync(request, _workRoot, cancellationToken);

        onStatus?.Invoke($"Instalando {request.Plan.DisplayName}...");

        var result = await RunProcessAsync(
            setupPath,
            $"/configure \"{configPath}\"",
            Path.GetDirectoryName(setupPath) ?? _workRoot,
            onLogReceived: line => onStatus?.Invoke(line),
            cancellationToken);

        onStatus?.Invoke(result.Success
            ? "Office instalado com sucesso."
            : $"setup.exe retornou código {result.ExitCode}.");

        return result.Success;
    }

    /// <summary>
    /// Gera o configuration.xml de remoção (elemento &lt;Remove&gt;, com suporte à tag
    /// RemoveAll) e dispara setup.exe /configure. Se <see cref="OfficeRemoveRequest.CleanStoreEdition"/>
    /// estiver marcado, também remove a edição da Microsoft Store do Office (pacote
    /// AppX), que não é tocada pelo ODT porque não é uma instalação Click-to-Run.
    /// </summary>
    public async Task<bool> RunRemoveAsync(OfficeRemoveRequest request, Action<string>? onStatus = null, CancellationToken cancellationToken = default)
    {
        string setupPath = await EnsureSetupExeAsync(onStatus, cancellationToken);
        string configPath = await OfficeConfigXmlBuilder.WriteRemoveToFolderAsync(request, _workRoot, cancellationToken);

        onStatus?.Invoke(request.RemoveAll
            ? "Removendo todas as instalações do Office (RemoveAll)..."
            : $"Removendo {request.ProductIds?.Count ?? 0} produto(s) do Office...");

        var result = await RunProcessAsync(
            setupPath,
            $"/configure \"{configPath}\"",
            Path.GetDirectoryName(setupPath) ?? _workRoot,
            onLogReceived: line => onStatus?.Invoke(line),
            cancellationToken);

        bool success = result.Success;

        if (success && request.CleanStoreEdition)
        {
            onStatus?.Invoke("Verificando edição da Microsoft Store do Office...");
            success = await RemoveStoreEditionAsync(onStatus, cancellationToken) && success;
        }

        onStatus?.Invoke(success
            ? "Remoção concluída."
            : $"setup.exe retornou código {result.ExitCode}.");

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
    /// o mecanismo que o próprio ODT documenta para essa política.
    /// </summary>
    public async Task<bool> RunSetAutoUpdateAsync(bool enabled, Action<string>? onStatus = null, CancellationToken cancellationToken = default)
    {
        string setupPath = await EnsureSetupExeAsync(onStatus, cancellationToken);
        string configPath = await OfficeConfigXmlBuilder.WriteUpdatesOnlyToFolderAsync(enabled, _workRoot, cancellationToken);

        onStatus?.Invoke(enabled ? "Ativando atualizações automáticas..." : "Desativando atualizações automáticas...");

        var result = await RunProcessAsync(
            setupPath,
            $"/configure \"{configPath}\"",
            Path.GetDirectoryName(setupPath) ?? _workRoot,
            onLogReceived: line => onStatus?.Invoke(line),
            cancellationToken);

        onStatus?.Invoke(result.Success
            ? $"Atualizações automáticas {(enabled ? "ativadas" : "desativadas")}."
            : $"setup.exe retornou código {result.ExitCode}.");

        return result.Success;
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
