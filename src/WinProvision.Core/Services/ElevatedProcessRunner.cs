using System;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace WinProvision.Core.Services;

/// <summary>
/// Roda um único comando elevado (UAC), sem precisar que o processo chamador (a Store)
/// esteja elevado. Usado como fallback quando um comando winget falha por falta de
/// privilégio — em vez de exigir que o app inteiro rode como Administrador (o que
/// quebrava a desinstalação de pacotes em escopo user, ver WingetExecutor), só a
/// operação específica que realmente precisa é relançada elevada.
///
/// Necessário passar por "cmd.exe /c ... > arquivo 2>&1" porque um processo lançado com
/// UseShellExecute=true + Verb=runas (exigido pro UAC funcionar) não permite redirecionar
/// stdout/stderr diretamente — é uma limitação do próprio Windows/.NET.
/// </summary>
public static class ElevatedProcessRunner
{
    public static async Task<WingetExecutionResult> RunElevatedAsync(
        string fileName,
        string arguments,
        CancellationToken cancellationToken)
    {
        string tempFile = Path.Combine(Path.GetTempPath(), $"winprovision-elev-{Guid.NewGuid():N}.log");

        // "chcp 65001" evita mojibake em acentos: sem isso, o cmd.exe redireciona a saída
        // usando a codepage OEM do console (ex.: CP850/CP860), não UTF-8.
        var startInfo = new ProcessStartInfo
        {
            FileName = "cmd.exe",
            Arguments = $"/c \"chcp 65001>nul & \"{fileName}\" {arguments} >\"{tempFile}\" 2>&1\"",
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
                return new WingetExecutionResult
                {
                    Success = false,
                    ExitCode = -1,
                    Output = "Não foi possível iniciar o processo elevado.",
                };
            }

            await process.WaitForExitAsync(cancellationToken);
            string output = File.Exists(tempFile) ? await File.ReadAllTextAsync(tempFile, cancellationToken) : string.Empty;

            return new WingetExecutionResult
            {
                Success = process.ExitCode == 0,
                ExitCode = process.ExitCode,
                Output = output,
            };
        }
        catch (Win32Exception ex) when (ex.NativeErrorCode == 1223) // ERROR_CANCELLED — usuário clicou "Não" no UAC.
        {
            return new WingetExecutionResult
            {
                Success = false,
                ExitCode = -1,
                Output = "Elevação cancelada pelo usuário.",
                FailureReason = WingetFailureReason.ElevationCanceled,
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
