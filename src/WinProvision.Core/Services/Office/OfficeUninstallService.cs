using System.Diagnostics;
using System.IO.Compression;
using Microsoft.Win32;
using WinProvision.Core.Models.Office;
using WinProvision.Core.Services;

namespace WinProvision.Core.Services.Office;

/// <summary>
/// Serviço especializado para desinstalação completa do Office usando métodos
/// padrão e agressivos (GetHelpCmd) quando necessário, sempre com privilégios
/// administrativos via ElevatedProcessRunner.
/// </summary>
public sealed class OfficeUninstallService
{
    private const string GetHelpCmdZipUrl =
        "https://pub-166b41912a994dbe86583ba10596d673.r2.dev/Office/Uninstall/GetHelpCmd.zip";

    private static readonly string GetHelpCmdRoot = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
        "WinProvision", "GetHelpCmd");

    private static readonly string[] UninstallRoots =
    [
        @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall",
        @"SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall"
    ];

    public IReadOnlyList<OfficeInstallation> DetectInstalledOffice()
    {
        var results = new List<OfficeInstallation>();

        foreach (var root in UninstallRoots)
        {
            using var key = Registry.LocalMachine.OpenSubKey(root);
            if (key is null) continue;

            foreach (var subKeyName in key.GetSubKeyNames())
            {
                using var subKey = key.OpenSubKey(subKeyName);
                if (subKey?.GetValue("UninstallString") is not string uninstallString) continue;
                if (!uninstallString.Contains("OfficeClickToRun.exe", StringComparison.OrdinalIgnoreCase)) continue;

                var displayName = subKey.GetValue("DisplayName") as string ?? "Microsoft Office";
                results.Add(new OfficeInstallation(displayName, uninstallString));
            }
        }

        return results;
    }

    public async Task<OfficeUninstallOutcome> UninstallAsync(Action<string>? onStatus = null, CancellationToken ct = default)
    {
        var installations = DetectInstalledOffice();
        if (installations.Count == 0)
        {
            onStatus?.Invoke("Office não está instalado.");
            return OfficeUninstallOutcome.NotInstalled;
        }

        onStatus?.Invoke($"Detectado {installations.Count} instalação(ões) do Office.");

        if (await RunStandardAsync(installations, onStatus, ct) && DetectInstalledOffice().Count == 0)
        {
            onStatus?.Invoke("Office removido com sucesso (método padrão).");
            return OfficeUninstallOutcome.RemovedByStandardMethod;
        }

        onStatus?.Invoke("Método padrão falhou. Tentando método agressivo (GetHelpCmd)...");

        var getHelpCmdPath = await EnsureGetHelpCmdAsync(onStatus, ct);
        if (getHelpCmdPath is null)
        {
            onStatus?.Invoke("Falha ao baixar GetHelpCmd.");
            return OfficeUninstallOutcome.Failed;
        }

        if (await RunAggressiveAsync(getHelpCmdPath, onStatus, ct) && DetectInstalledOffice().Count == 0)
        {
            onStatus?.Invoke("Office removido com sucesso (método agressivo).");
            return OfficeUninstallOutcome.RemovedByAggressiveMethod;
        }

        onStatus?.Invoke("Falha na desinstalação do Office.");
        return OfficeUninstallOutcome.Failed;
    }

    private static async Task<string?> EnsureGetHelpCmdAsync(Action<string>? onStatus, CancellationToken ct)
    {
        var cached = FindExecutable(GetHelpCmdRoot);
        if (cached is not null)
        {
            onStatus?.Invoke("Usando GetHelpCmd em cache.");
            return cached;
        }

        onStatus?.Invoke("Baixando GetHelpCmd do R2...");
        Directory.CreateDirectory(GetHelpCmdRoot);
        var zipPath = Path.Combine(GetHelpCmdRoot, "GetHelpCmd.zip");

        try
        {
            using var http = new HttpClient { Timeout = TimeSpan.FromMinutes(10) };
            await using (var remoteStream = await http.GetStreamAsync(GetHelpCmdZipUrl, ct))
            await using (var fileStream = File.Create(zipPath))
            {
                await remoteStream.CopyToAsync(fileStream, ct);
            }

            onStatus?.Invoke("Extraindo GetHelpCmd...");
            ZipFile.ExtractToDirectory(zipPath, GetHelpCmdRoot, overwriteFiles: true);
        }
        catch (Exception ex) when (ex is HttpRequestException or IOException or InvalidDataException)
        {
            onStatus?.Invoke($"Erro ao baixar/extrair GetHelpCmd: {ex.Message}");
            return null;
        }
        finally
        {
            if (File.Exists(zipPath)) File.Delete(zipPath);
        }

        return FindExecutable(GetHelpCmdRoot);
    }

    private static string? FindExecutable(string root)
    {
        if (!Directory.Exists(root)) return null;

        return Directory
            .EnumerateFiles(root, "GetHelpCmd.exe", SearchOption.AllDirectories)
            .FirstOrDefault();
    }

    private static async Task<bool> RunStandardAsync(IEnumerable<OfficeInstallation> installations, Action<string>? onStatus, CancellationToken ct)
    {
        foreach (var install in installations)
        {
            onStatus?.Invoke($"Desinstalando {install.DisplayName} (método padrão com elevação)...");

            var (exePath, arguments) = SplitCommandLine(install.UninstallString);
            if (exePath is null || !File.Exists(exePath))
            {
                onStatus?.Invoke($"Caminho de desinstalação inválido: {install.UninstallString}");
                return false;
            }

            // Usa ElevatedProcessRunner para executar com privilégios administrativos
            var result = await ElevatedProcessRunner.RunElevatedAsync(
                exePath,
                $"{arguments} DisplayLevel=False",
                ct);

            if (!result.Success)
            {
                onStatus?.Invoke($"Falha na desinstalação padrão (código {result.ExitCode}): {result.Output}");
                return false;
            }
        }

        return true;
    }

    private static async Task<bool> RunAggressiveAsync(string getHelpCmdPath, Action<string>? onStatus, CancellationToken ct)
    {
        if (!File.Exists(getHelpCmdPath))
        {
            onStatus?.Invoke("GetHelpCmd.exe não encontrado.");
            return false;
        }

        onStatus?.Invoke("Executando desinstalação agressiva (OfficeScrubScenario) com elevação...");

        // Usa ElevatedProcessRunner para executar GetHelpCmd com privilégios administrativos
        var result = await ElevatedProcessRunner.RunElevatedAsync(
            getHelpCmdPath,
            "-S OfficeScrubScenario -AcceptEula -OfficeVersion All",
            ct);

        bool success = result.Success;

        onStatus?.Invoke(success
            ? "Desinstalação agressiva concluída."
            : $"Desinstalação agressiva falhou (código {result.ExitCode}): {result.Output}");

        return success;
    }

    private static (string? ExePath, string Arguments) SplitCommandLine(string commandLine)
    {
        if (!commandLine.StartsWith('"')) return (null, string.Empty);

        var closingQuote = commandLine.IndexOf('"', 1);
        if (closingQuote < 0) return (null, string.Empty);

        var exePath = commandLine[1..closingQuote];
        var arguments = commandLine[(closingQuote + 1)..].Trim();
        return (exePath, arguments);
    }
}
