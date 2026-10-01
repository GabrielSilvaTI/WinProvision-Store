using System.Diagnostics;
using System.IO;
using WinProvision.Core.Services;

namespace WinProvision.Store.Services;

public sealed class BackgroundUpdateStartupService(ApplicationPreferencesService preferences)
{
    private const string LegacyTaskName = @"\WinProvisionStore\AutoUpdate";
    private const string LegacyScriptPath = @"WinProvisionStore\AutoUpdate.vbs";

    public bool IsEnabled => preferences.AutoUpdateAtStartup;

    public async Task<string?> MigrateLegacyTaskAsync(CancellationToken cancellationToken = default)
    {
        if (!await LegacyTaskExistsAsync(cancellationToken).ConfigureAwait(false))
            return null;

        preferences.SetAutoUpdateAtStartup(true);
        string? error = await RemoveLegacyTaskAsync(cancellationToken).ConfigureAwait(false);
        return error is null
            ? "A configuração antiga foi migrada para execução em segundo plano ao entrar no Windows."
            : $"A inicialização em segundo plano foi ativada, mas a tarefa antiga não pôde ser removida: {error}";
    }

    public async Task<(bool Success, string? Message)> EnableAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            preferences.SetAutoUpdateAtStartup(true);
            string? cleanupError = await RemoveLegacyTaskAsync(cancellationToken).ConfigureAwait(false);
            return (true, cleanupError is null
                ? null
                : $"Ativada. Não foi possível remover a tarefa antiga: {cleanupError}");
        }
        catch (Exception ex)
        {
            return (false, ex.Message);
        }
    }

    public async Task<(bool Success, string? Message)> DisableAsync(CancellationToken cancellationToken = default)
    {
        bool wasEnabled = preferences.AutoUpdateAtStartup;
        try
        {
            preferences.SetAutoUpdateAtStartup(false);
            string? cleanupError = await RemoveLegacyTaskAsync(cancellationToken).ConfigureAwait(false);
            if (cleanupError is not null)
            {
                if (wasEnabled) preferences.SetAutoUpdateAtStartup(true);
                return (false, $"A tarefa antiga não pôde ser removida: {cleanupError}");
            }

            return (true, null);
        }
        catch (Exception ex)
        {
            if (wasEnabled)
            {
                try { preferences.SetAutoUpdateAtStartup(true); } catch { }
            }
            return (false, ex.Message);
        }
    }

    private static async Task<bool> LegacyTaskExistsAsync(CancellationToken cancellationToken)
    {
        var result = await RunSchtasksAsync($"/query /tn \"{LegacyTaskName}\"", cancellationToken).ConfigureAwait(false);
        return result.ExitCode == 0;
    }

    private static async Task<string?> RemoveLegacyTaskAsync(CancellationToken cancellationToken)
    {
        if (!await LegacyTaskExistsAsync(cancellationToken).ConfigureAwait(false))
            return null;

        var result = await RunSchtasksAsync($"/delete /tn \"{LegacyTaskName}\" /f", cancellationToken).ConfigureAwait(false);
        if (result.ExitCode != 0)
        {
            // A tarefa antiga podia ter sido criada com privilégios elevados. Tenta removê-la
            // no contexto atual primeiro; só solicita UAC se o Windows negar essa remoção.
            var elevated = await ElevatedProcessRunner.RunElevatedAsync(
                "schtasks.exe",
                $"/delete /tn {LegacyTaskName} /f",
                cancellationToken).ConfigureAwait(false);
            if (!elevated.Success)
                return string.IsNullOrWhiteSpace(elevated.Output)
                    ? string.IsNullOrWhiteSpace(result.Output) ? $"código {result.ExitCode}" : result.Output.Trim()
                    : elevated.Output.Trim();
        }

        try
        {
            string scriptPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), LegacyScriptPath);
            if (File.Exists(scriptPath)) File.Delete(scriptPath);
        }
        catch { }

        return null;
    }

    private static async Task<(int ExitCode, string Output)> RunSchtasksAsync(string arguments, CancellationToken cancellationToken)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = "schtasks.exe",
            Arguments = arguments,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };

        try
        {
            using var process = Process.Start(startInfo);
            if (process is null) return (-1, "Não foi possível iniciar schtasks.exe.");
            Task<string> stdout = process.StandardOutput.ReadToEndAsync(cancellationToken);
            Task<string> stderr = process.StandardError.ReadToEndAsync(cancellationToken);
            await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
            return (process.ExitCode, (await stdout.ConfigureAwait(false)) + (await stderr.ConfigureAwait(false)));
        }
        catch (Exception ex)
        {
            return (-1, ex.Message);
        }
    }
}
