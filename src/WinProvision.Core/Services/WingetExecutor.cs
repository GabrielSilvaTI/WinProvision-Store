using System.Diagnostics;
using System.Text;
using System.Threading;

namespace WinProvision.Core.Services;

public class WingetExecutionResult
{
    public bool Success { get; set; }
    public int ExitCode { get; set; }
    public string Output { get; set; } = string.Empty;
}

public class WingetExecutor
{
    /// <summary>
    /// Instala um pacote do Winget de forma silenciosa e reporta o progresso em tempo real.
    /// </summary>
    public async Task<WingetExecutionResult> InstallAppAsync(string appId, Action<string>? onLogReceived = null, CancellationToken cancellationToken = default)
    {
        // Argumentos otimizados para instalação não interativa e aceitação automática de termos
        string args = $"install --id \"{appId}\" --exact --silent --accept-source-agreements --accept-package-agreements";
        return await ExecuteWingetCommandAsync(args, onLogReceived, cancellationToken);
    }

    /// <summary>
    /// Desinstala um pacote silenciosamente.
    /// </summary>
    public async Task<WingetExecutionResult> UninstallAppAsync(string appId, Action<string>? onLogReceived = null, CancellationToken cancellationToken = default)
    {
        string args = $"uninstall --id \"{appId}\" --exact --silent";
        return await ExecuteWingetCommandAsync(args, onLogReceived, cancellationToken);
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
