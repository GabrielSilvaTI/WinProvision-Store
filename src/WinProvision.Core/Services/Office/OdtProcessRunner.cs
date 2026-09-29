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
                Output = "setup.exe não encontrado."
            };

        if (!File.Exists(absoluteXmlPath))
            return new OdtProcessResult
            {
                Success = false,
                ExitCode = -1,
                Output = "configuration.xml não encontrado."
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
                    Output = "Não foi possível iniciar o processo elevado."
                };
            }

            await process.WaitForExitAsync(ct);
            string output = File.Exists(tempFile) ? await File.ReadAllTextAsync(tempFile, ct) : string.Empty;

            return new OdtProcessResult
            {
                Success = process.ExitCode == 0,
                ExitCode = process.ExitCode,
                Output = output
            };
        }
        catch (Win32Exception ex) when (ex.NativeErrorCode == 1223) // ERROR_CANCELLED — usuário clicou "Não" no UAC.
        {
            return new OdtProcessResult
            {
                Success = false,
                ExitCode = -1,
                Output = "Elevação cancelada pelo usuário.",
                ElevationCanceled = true
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
}
