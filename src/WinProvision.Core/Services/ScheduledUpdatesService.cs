using System.Diagnostics;
using System.Text;

namespace WinProvision.Core.Services;

public sealed class ScheduledUpdatesService
{
    private const string TaskName = @"WinProvisionStore\AutoUpdate";
    private const string WingetArguments = "upgrade --all --accept-source-agreements --accept-package-agreements --silent --disable-interactivity";

    public async Task<bool> IsEnabledAsync(CancellationToken cancellationToken = default)
    {
        var result = await RunSchtasksAsync($"/query /tn \"{TaskName}\"", cancellationToken);
        return result.Success;
    }

    public async Task<WingetExecutionResult> EnableAsync(CancellationToken cancellationToken = default)
    {
        // Usa Register-ScheduledTask (PowerShell) em vez de schtasks.exe.
        // Motivo: o ElevatedProcessRunner executa via cmd.exe /c "...", e schtasks.exe exige
        // aspas aninhadas no valor de /tr, o que quebra o parser do cmd.
        // Com -EncodedCommand (Base64 UTF-16LE), o argumento passado ao ElevatedProcessRunner
        // não contém nenhuma aspa — o script PS completo vai codificado.
        //
        // O script registra a tarefa para rodar powershell.exe em segundo plano ao logon,
        // executando winget silenciosamente sem abrir nenhuma janela.
        string psScript = string.Join("\n", [
            "$ErrorActionPreference = 'Stop'",
            $"$action   = New-ScheduledTaskAction -Execute 'powershell.exe' -Argument '-WindowStyle Hidden -NonInteractive -Command \"winget {WingetArguments}\"'",
            "$trigger   = New-ScheduledTaskTrigger -AtLogOn",
            "$settings  = New-ScheduledTaskSettingsSet -ExecutionTimeLimit (New-TimeSpan -Hours 2) -MultipleInstances IgnoreNew",
            "$principal = New-ScheduledTaskPrincipal -UserId $env:USERNAME -RunLevel Highest",
            $"Register-ScheduledTask -TaskName '{TaskName}' -Action $action -Trigger $trigger -Settings $settings -Principal $principal -Force | Out-Null",
        ]);

        string encoded = Convert.ToBase64String(Encoding.Unicode.GetBytes(psScript));
        return await ElevatedProcessRunner.RunElevatedAsync(
            "powershell.exe",
            $"-WindowStyle Hidden -NonInteractive -EncodedCommand {encoded}",
            cancellationToken);
    }

    public async Task<WingetExecutionResult> DisableAsync(CancellationToken cancellationToken = default)
    {
        string psScript = string.Join("\n", [
            "$ErrorActionPreference = 'Stop'",
            $"Unregister-ScheduledTask -TaskName '{TaskName}' -Confirm:$false",
        ]);

        string encoded = Convert.ToBase64String(Encoding.Unicode.GetBytes(psScript));
        return await ElevatedProcessRunner.RunElevatedAsync(
            "powershell.exe",
            $"-WindowStyle Hidden -NonInteractive -EncodedCommand {encoded}",
            cancellationToken);
    }

    private static async Task<WingetExecutionResult> RunSchtasksAsync(string arguments, CancellationToken cancellationToken)
    {
        var outputBuilder = new StringBuilder();

        var startInfo = new ProcessStartInfo
        {
            FileName = "schtasks.exe",
            Arguments = arguments,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8
        };

        using var process = new Process { StartInfo = startInfo };

        try
        {
            process.Start();
            outputBuilder.Append(await process.StandardOutput.ReadToEndAsync(cancellationToken));
            outputBuilder.Append(await process.StandardError.ReadToEndAsync(cancellationToken));
            await process.WaitForExitAsync(cancellationToken);

            return new WingetExecutionResult
            {
                Success = process.ExitCode == 0,
                ExitCode = process.ExitCode,
                Output = outputBuilder.ToString()
            };
        }
        catch (Exception ex)
        {
            return new WingetExecutionResult
            {
                Success = false,
                ExitCode = -1,
                Output = $"Falha ao consultar a tarefa agendada: {ex.Message}"
            };
        }
    }
}