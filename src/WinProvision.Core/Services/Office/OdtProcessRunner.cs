using System.ComponentModel;
using System.Diagnostics;
using System.IO;

namespace WinProvision.Core.Services.Office;

/// <summary>
/// Executa o setup.exe do ODT (Office Deployment Tool) com configuração XML,
/// resolvendo caminhos absolutos e definindo explicitamente o WorkingDirectory
/// para evitar problemas com o diretório de trabalho do processo pai.
/// </summary>
public static class OdtProcessRunner
{
    /// <summary>Executa setup.exe /download e aguarda o ODT concluir a preparação local.</summary>
    public static async Task<int> RunDownloadAsync(
        string setupExePath, string configurationXmlPath, Action<string>? onStatus = null,
        CancellationToken ct = default)
    {
        var absoluteSetupPath = Path.GetFullPath(setupExePath);
        var absoluteXmlPath = Path.GetFullPath(configurationXmlPath);
        if (!File.Exists(absoluteSetupPath))
            throw new FileNotFoundException("setup.exe não encontrado.", absoluteSetupPath);
        if (!File.Exists(absoluteXmlPath))
            throw new FileNotFoundException("configuration.xml não encontrado.", absoluteXmlPath);

        var startInfo = new ProcessStartInfo(absoluteSetupPath, $"/download \"{absoluteXmlPath}\"")
        {
            WorkingDirectory = Path.GetDirectoryName(absoluteSetupPath) ?? string.Empty,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };

        using var process = Process.Start(startInfo)
            ?? throw new InvalidOperationException("Falha ao iniciar o download do Office Deployment Tool.");
        process.OutputDataReceived += (_, e) => { if (!string.IsNullOrWhiteSpace(e.Data)) onStatus?.Invoke(e.Data); };
        process.ErrorDataReceived += (_, e) => { if (!string.IsNullOrWhiteSpace(e.Data)) onStatus?.Invoke(e.Data); };
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();
        try
        {
            await process.WaitForExitAsync(ct).ConfigureAwait(false);
            return process.ExitCode;
        }
        catch (OperationCanceledException)
        {
            try { if (!process.HasExited) process.Kill(entireProcessTree: true); }
            catch { /* O processo pode ter terminado entre a verificação e o encerramento. */ }
            throw;
        }
    }

    /// <summary>
    /// Executa setup.exe /configure com o configuration.xml especificado.
    /// Usa caminhos absolutos e define o WorkingDirectory para a pasta do setup.exe,
    /// eliminando dependência do cwd do processo pai e resolvendo problemas de
    /// elevação que resetam o cwd para System32.
    /// </summary>
    public static async Task<int> RunConfigureAsync(
        string setupExePath, string configurationXmlPath, CancellationToken ct = default)
    {
        var absoluteSetupPath = Path.GetFullPath(setupExePath);
        var absoluteXmlPath = Path.GetFullPath(configurationXmlPath);

        if (!File.Exists(absoluteSetupPath))
            throw new FileNotFoundException("setup.exe não encontrado.", absoluteSetupPath);

        if (!File.Exists(absoluteXmlPath))
            throw new FileNotFoundException("configuration.xml não encontrado.", absoluteXmlPath);

        var startInfo = new ProcessStartInfo(absoluteSetupPath, $"/configure \"{absoluteXmlPath}\"")
        {
            WorkingDirectory = Path.GetDirectoryName(absoluteSetupPath) ?? string.Empty,
            UseShellExecute = false,
            CreateNoWindow = true
        };

        using var process = Process.Start(startInfo)
            ?? throw new InvalidOperationException("Falha ao iniciar o setup.exe.");

        await process.WaitForExitAsync(ct);
        return process.ExitCode;
    }

    /// <summary>
    /// Executa setup.exe /configure com elevação administrativa isolada (UAC prompt).
    /// Usa cmd.exe /c com Verb=runas para solicitar elevação apenas para este comando,
    /// não elevando o processo inteiro da Store. Define explicitamente o WorkingDirectory
    /// e usa caminhos absolutos para evitar problemas com o cwd.
    /// </summary>
    public static async Task<OdtProcessResult> RunConfigureElevatedAsync(
        string setupExePath, string configurationXmlPath, CancellationToken ct = default)
    {
        var absoluteSetupPath = Path.GetFullPath(setupExePath);
        var absoluteXmlPath = Path.GetFullPath(configurationXmlPath);
        var workingDir = Path.GetDirectoryName(absoluteSetupPath) ?? string.Empty;

        if (!File.Exists(absoluteSetupPath))
            return new OdtProcessResult
            {
                Success = false,
                ExitCode = -1,
                Output = "setup.exe não encontrado.",
                ProcessStarted = false,
            };

        if (!File.Exists(absoluteXmlPath))
            return new OdtProcessResult
            {
                Success = false,
                ExitCode = -1,
                Output = "configuration.xml não encontrado.",
                ProcessStarted = false,
            };

        string tempFile = Path.Combine(Path.GetTempPath(), $"winprovision-odt-{Guid.NewGuid():N}.log");

        // Usa cmd.exe /c com Verb=runas para elevação isolada
        // Define o WorkingDirectory via "cd /d" antes de executar o setup.exe
        var startInfo = new ProcessStartInfo
        {
            FileName = "cmd.exe",
            Arguments = $"/c \"chcp 65001>nul & cd /d \"{workingDir}\" & \"{absoluteSetupPath}\" /configure \"{absoluteXmlPath}\" >\"{tempFile}\" 2>&1\"",
            UseShellExecute = true,
            Verb = "runas",
            WindowStyle = ProcessWindowStyle.Hidden,
            CreateNoWindow = true,
        };

        try
        {
            using var process = Process.Start(startInfo);
            if (process is null)
            {
                return new OdtProcessResult
                {
                    Success = false,
                    ExitCode = -1,
                    Output = "Não foi possível iniciar o processo elevado.",
                    ProcessStarted = false,
                };
            }

            await process.WaitForExitAsync(ct);
            string output = File.Exists(tempFile) ? await File.ReadAllTextAsync(tempFile, ct) : string.Empty;

            return new OdtProcessResult
            {
                Success = process.ExitCode == 0,
                ExitCode = process.ExitCode,
                Output = output,
                ProcessStarted = true,
            };
        }
        catch (Win32Exception ex) when (ex.NativeErrorCode == 1223) // ERROR_CANCELLED — usuário clicou "Não" no UAC.
        {
            return new OdtProcessResult
            {
                Success = false,
                ExitCode = -1,
                Output = "Elevação cancelada pelo usuário.",
                ElevationCanceled = true,
                ProcessStarted = false,
            };
        }
        finally
        {
            TryDeleteFile(tempFile);
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
            // Best-effort; arquivo temporário órfão não é crítico.
        }
    }
}

/// <summary>
/// Resultado da execução do setup.exe do ODT.
/// </summary>
public record OdtProcessResult
{
    public bool Success { get; init; }
    public int ExitCode { get; init; }
    public string Output { get; init; } = string.Empty;
    public bool ElevationCanceled { get; init; }
    public bool ProcessStarted { get; init; }
}
