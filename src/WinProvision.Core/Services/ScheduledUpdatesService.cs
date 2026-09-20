using System.Diagnostics;
using System.Text;

namespace WinProvision.Core.Services;

public sealed class ScheduledUpdatesService
{
    private const string TaskName = "AutoUpdate";
    private const string TaskPath = @"\WinProvisionStore\";
    private const string TaskFullName = @"\WinProvisionStore\AutoUpdate";
    private const string WingetArguments = "upgrade --all --accept-source-agreements --accept-package-agreements --silent --disable-interactivity";

    public async Task<bool> IsEnabledAsync(CancellationToken cancellationToken = default)
    {
        var result = await RunSchtasksAsync($"/query /tn \"{TaskFullName}\"", cancellationToken);
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
        // A tarefa usa wscript.exe em vez de iniciar powershell.exe diretamente no logon.
        // Isso evita que o host do PowerShell pisque ou abra uma janela de console; o
        // wrapper WScript executa o winget com janela 0 (oculta) e aguarda em silêncio.
        string psScript = string.Join("\n", [
            "$ErrorActionPreference = 'Stop'",
            "$ProgressPreference = 'SilentlyContinue'",
            "$WarningPreference = 'SilentlyContinue'",
            "$taskDirectory = Join-Path $env:ProgramData 'WinProvisionStore'",
            "$scriptPath = Join-Path $taskDirectory 'AutoUpdate.vbs'",
            "New-Item -ItemType Directory -Path $taskDirectory -Force | Out-Null",
            $"$vbs = @'\r\nSet shell = CreateObject(\"WScript.Shell\")\r\nshell.Run \"winget {WingetArguments}\", 0, True\r\n'@",
            "[IO.File]::WriteAllText($scriptPath, $vbs, [Text.Encoding]::ASCII)",
            "$action   = New-ScheduledTaskAction -Execute 'wscript.exe' -Argument ('//B //Nologo \"' + $scriptPath + '\"')",
            "$trigger   = New-ScheduledTaskTrigger -AtLogOn",
            "$settings  = New-ScheduledTaskSettingsSet -ExecutionTimeLimit (New-TimeSpan -Hours 2) -MultipleInstances IgnoreNew",
            "$principal = New-ScheduledTaskPrincipal -UserId $env:USERNAME -RunLevel Highest",
            "$service = New-Object -ComObject Schedule.Service",
            "$service.Connect()",
            "$root = $service.GetFolder('\\')",
            "try { $root.GetFolder('\\WinProvisionStore') | Out-Null } catch { $root.CreateFolder('WinProvisionStore', $null) | Out-Null }",
            $"Register-ScheduledTask -TaskName '{TaskName}' -TaskPath '{TaskPath}' -Action $action -Trigger $trigger -Settings $settings -Principal $principal -Force | Out-Null",
        ]);

        string encoded = Convert.ToBase64String(Encoding.Unicode.GetBytes(psScript));
        return await ElevatedProcessRunner.RunElevatedAsync(
            "powershell.exe",
            $"-NoProfile -WindowStyle Hidden -NonInteractive -EncodedCommand {encoded}",
            cancellationToken);
    }

    public async Task<WingetExecutionResult> DisableAsync(CancellationToken cancellationToken = default)
    {
        string psScript = string.Join("\n", [
            "$ErrorActionPreference = 'Stop'",
            "$ProgressPreference = 'SilentlyContinue'",
            "$WarningPreference = 'SilentlyContinue'",
            $"$task = Get-ScheduledTask -TaskName '{TaskName}' -TaskPath '{TaskPath}' -ErrorAction SilentlyContinue",
            "if ($null -ne $task) { Unregister-ScheduledTask -InputObject $task -Confirm:$false -ErrorAction Stop | Out-Null }",
            "$scriptPath = Join-Path $env:ProgramData 'WinProvisionStore\\AutoUpdate.vbs'",
            "Remove-Item -LiteralPath $scriptPath -Force -ErrorAction SilentlyContinue | Out-Null",
        ]);

        string encoded = Convert.ToBase64String(Encoding.Unicode.GetBytes(psScript));
        return await ElevatedProcessRunner.RunElevatedAsync(
            "powershell.exe",
            $"-NoProfile -WindowStyle Hidden -NonInteractive -EncodedCommand {encoded}",
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