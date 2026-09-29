using System;
using System.Diagnostics;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace WinProvision.Core.Services;

public sealed class ScheduledTempCleanerService
{
    private const string TaskName = @"WinProvisionStore\CleanTempOnLogon";

    public async Task<bool> IsEnabledAsync(CancellationToken cancellationToken = default)
    {
        var result = await RunSchtasksAsync($"/query /tn \"{TaskName}\"", cancellationToken);
        return result.Success;
    }

    public async Task<WingetExecutionResult> EnableAsync(CancellationToken cancellationToken = default)
    {
        string psScript = string.Join("\n", [
            "$ErrorActionPreference = 'SilentlyContinue'",
            "$cleanCmd = 'Remove-Item -Path \"$env:TEMP\\*\", \"$env:SystemRoot\\Temp\\*\" -Recurse -Force -ErrorAction SilentlyContinue'",
            "$action   = New-ScheduledTaskAction -Execute 'powershell.exe' -Argument ('-NoProfile -WindowStyle Hidden -NonInteractive -Command ' + [char]34 + $cleanCmd + [char]34)",
            "$trigger  = New-ScheduledTaskTrigger -AtLogOn",
            "$settings = New-ScheduledTaskSettingsSet -ExecutionTimeLimit (New-TimeSpan -Hours 1) -MultipleInstances IgnoreNew",
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
            "$ErrorActionPreference = 'SilentlyContinue'",
            $"Unregister-ScheduledTask -TaskName '{TaskName}' -Confirm:$false -ErrorAction SilentlyContinue",
        ]);

        string encoded = Convert.ToBase64String(Encoding.Unicode.GetBytes(psScript));
        return await ElevatedProcessRunner.RunElevatedAsync(
            "powershell.exe",
            $"-WindowStyle Hidden -NonInteractive -EncodedCommand {encoded}",
            cancellationToken);
    }

    public async Task<WingetExecutionResult> ExecuteNowAsync(CancellationToken cancellationToken = default)
    {
        string psScript = "Remove-Item -Path \"$env:TEMP\\*\", \"$env:SystemRoot\\Temp\\*\" -Recurse -Force -ErrorAction SilentlyContinue";
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
