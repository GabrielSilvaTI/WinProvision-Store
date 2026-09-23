using System;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Win32;

namespace WinProvision.Core.Services;

/// <summary>
/// Tipos de instalador reconhecidos pela tabela de assinaturas (estilo BCUninstaller).
/// Expandido para incluir Squirrel/Electron, package managers e Windows Store Apps.
/// </summary>
public enum InstallerKind
{
    Unknown,
    Msiexec,
    InnoSetup,
    Nsis,
    InstallShield,
    Wise,
    Squirrel,        // Squirrel/Electron auto-updater (Discord, Slack, GitHub Desktop, etc.)
    Chocolatey,      // Chocolatey package manager
    Steam,           // Steam games/apps
    WindowsStore,    // Windows Store/Appx apps
    Scoop,           // Scoop package manager
    Nullsoft         // Generic Nullsoft installer
}

/// <summary>
/// Gerenciador de locks para prevenção de colisões durante desinstalações em massa
/// (estilo BCUninstaller collision prevention).
/// </summary>
public static class UninstallLockManager
{
    private static readonly ConcurrentDictionary<string, SemaphoreSlim> _installPathLocks = new();
    private static readonly ConcurrentDictionary<string, SemaphoreSlim> _processLocks = new();
    private static readonly SemaphoreSlim _globalUninstallLock = new(3, 3); // Máximo 3 desinstalações simultâneas

    /// <summary>
    /// Adquire um lock para um caminho de instalação específico.
    /// </summary>
    public static async Task<IDisposable> AcquireInstallPathLockAsync(string installPath, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrEmpty(installPath))
            return new NoOpDisposable();

        var normalizedPath = installPath.ToLowerInvariant();
        var semaphore = _installPathLocks.GetOrAdd(normalizedPath, _ => new SemaphoreSlim(1, 1));

        await semaphore.WaitAsync(cancellationToken);
        return new LockReleaser(() => ReleaseInstallPathLock(normalizedPath, semaphore));
    }

    /// <summary>
    /// Adquire um lock para um processo específico (para evitar conflitos de MSI/instaladores).
    /// </summary>
    public static async Task<IDisposable> AcquireProcessLockAsync(string processName, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrEmpty(processName))
            return new NoOpDisposable();

        var normalizedProcess = processName.ToLowerInvariant();
        var semaphore = _processLocks.GetOrAdd(normalizedProcess, _ => new SemaphoreSlim(1, 1));

        await semaphore.WaitAsync(cancellationToken);
        return new LockReleaser(() => ReleaseProcessLock(normalizedProcess, semaphore));
    }

    /// <summary>
    /// Adquire o lock global de desinstalação (limita número de desinstalações simultâneas).
    /// </summary>
    public static async Task<IDisposable> AcquireGlobalUninstallLockAsync(CancellationToken cancellationToken = default)
    {
        await _globalUninstallLock.WaitAsync(cancellationToken);
        return new LockReleaser(() => _globalUninstallLock.Release());
    }

    private static void ReleaseInstallPathLock(string path, SemaphoreSlim semaphore)
    {
        semaphore.Release();
        _installPathLocks.TryRemove(path, out _);
    }

    private static void ReleaseProcessLock(string processName, SemaphoreSlim semaphore)
    {
        semaphore.Release();
        _processLocks.TryRemove(processName, out _);
    }

    private class LockReleaser : IDisposable
    {
        private readonly Action _releaseAction;

        public LockReleaser(Action releaseAction)
        {
            _releaseAction = releaseAction;
        }

        public void Dispose()
        {
            _releaseAction?.Invoke();
        }
    }

    private class NoOpDisposable : IDisposable
    {
        public void Dispose() { }
    }
}

public class UninstallerEngineService
{
    // Uninstaller padrão do Inno Setup: unins000.exe, unins001.exe... sempre na
    // pasta de instalação do próprio programa.
    private static readonly Regex InnoSetupPattern = new(@"unins\d{3}\.exe", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    // Nome de saída padrão do gerador de desinstalador do NSIS (makensis): o script
    // grava "Uninstall.exe" (ou "uninst.exe" em builds mais antigas) no $INSTDIR.
    private static readonly Regex NsisPattern = new(@"(^|\\)uninst(all)?\.exe$", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    // Wise Installation System (ex-Wise InstallMaster): uninstaller UNWISE(32).EXE.
    private static readonly Regex WisePattern = new(@"unwise(32)?\.exe$", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    // Pasta usada pelo InstallShield para projetos InstallScript/InstallScript MSI;
    // o UninstallString aponta pro setup.exe dentro dela.
    private const string InstallShieldFolderMarker = "installshield installation information";

    // Squirrel/Electron auto-updater: Update.exe com --uninstall flag
    private static readonly Regex SquirrelPattern = new(@"Update\.exe", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex SquirrelUninstallPattern = new(@"--uninstall", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    // Chocolatey: choco.exe uninstall command
    private static readonly Regex ChocolateyPattern = new(@"choco\.exe", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    // Steam: steam.exe uninstall or steamapps deletion
    private static readonly Regex SteamPattern = new(@"steam\.exe", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    // Windows Store: Get-AppxPackage or AppxPackage
    private static readonly Regex WindowsStorePattern = new(@"(Get-AppxPackage|AppxPackage|PowerShell)", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    // Scoop: scoop.exe uninstall
    private static readonly Regex ScoopPattern = new(@"scoop\.exe", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    /// <summary>
    /// Executa a desinstalação silenciosa de uma aplicação de forma robusta, detectando
    /// o tipo de instalador pela assinatura do UninstallString para aplicar os
    /// parâmetros silenciosos corretos (em vez de concatenar flags de tecnologias diferentes).
    /// Inclui retry logic, fallback methods e collision prevention (estilo BCUninstaller).
    /// </summary>
    public async Task<bool> RunSilentUninstallAsync(InstalledAppDetail app, CancellationToken cancellationToken = default)
    {
        string rawCommand = !string.IsNullOrWhiteSpace(app.QuietUninstallString)
            ? app.QuietUninstallString
            : app.UninstallString;

        if (string.IsNullOrWhiteSpace(rawCommand))
        {
            Debug.WriteLine($"[UninstallerEngine] Sem UninstallString para {app.DisplayName}");
            return await TryFallbackUninstall(app, string.Empty, string.Empty, cancellationToken);
        }

        if (!TrySplitCommand(rawCommand, out string fileName, out string existingArguments)
            || !IsSafeUninstallTarget(fileName))
        {
            Debug.WriteLine($"[UninstallerEngine] Não foi possível parsear UninstallString para {app.DisplayName}");
            return await TryFallbackUninstall(app, string.Empty, string.Empty, cancellationToken);
        }

        // Valida se o executável existe (exceto para comandos do sistema como msiexec, powershell, etc.)
        if (!IsSystemExecutable(fileName) && !File.Exists(fileName))
        {
            Debug.WriteLine($"[UninstallerEngine] Executável não encontrado: {fileName}");
            return await TryFallbackUninstall(app, fileName, existingArguments, cancellationToken);
        }

        string arguments = existingArguments;

        // Se já veio um QuietUninstallString oficial do fabricante, ele já é
        // silencioso por definição: não anexar nada.
        if (string.IsNullOrWhiteSpace(app.QuietUninstallString))
        {
            InstallerKind kind = DetectInstallerKind(fileName, rawCommand, app.Id);
            arguments = AppendSilentSwitches(kind, existingArguments, rawCommand, app.Id);
        }

        // Collision prevention: adquire locks para evitar conflitos
        using var globalLock = await UninstallLockManager.AcquireGlobalUninstallLockAsync(cancellationToken);
        using var pathLock = await UninstallLockManager.AcquireInstallPathLockAsync(app.InstallLocation, cancellationToken);
        using var processLock = await UninstallLockManager.AcquireProcessLockAsync(Path.GetFileName(fileName), cancellationToken);

        // Retry logic: tenta até 3 vezes se falhar (estilo BCUninstaller)
        const int maxRetries = 3;
        for (int attempt = 1; attempt <= maxRetries; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();

            // Apenas primeira tentativa requer elevação (evita múltiplos prompts UAC)
            bool requireElevation = (attempt == 1);
            var result = await TryRunUninstallProcess(fileName, arguments, cancellationToken, requireElevation);
            if (result.Success)
                return true;

            Debug.WriteLine($"[UninstallerEngine] Tentativa {attempt}/{maxRetries} falhou para {app.DisplayName}: {result.ErrorMessage}");

            // Se não for a última tentativa, espera antes de retry
            if (attempt < maxRetries)
            {
                await Task.Delay(2000 * attempt, cancellationToken); // Backoff progressivo
            }
        }

        // Fallback: tenta com argumentos alternativos se todos os retries falharem
        return await TryFallbackUninstall(app, fileName, existingArguments, cancellationToken);
    }

    /// <summary>
    /// Verifica se o executável é um comando do sistema que não precisa de validação de arquivo.
    /// </summary>
    private bool IsSystemExecutable(string fileName)
    {
        var systemExecutables = new[]
        {
            "msiexec.exe",
            "powershell.exe",
            "pwsh.exe",
            "cmd.exe",
            "schtasks.exe",
            "sc.exe",
            "winget.exe"
        };

        var exeName = Path.GetFileName(fileName).ToLowerInvariant();
        return systemExecutables.Contains(exeName);
    }

    private static bool IsSafeUninstallTarget(string fileName)
    {
        if (string.IsNullOrWhiteSpace(fileName) || fileName.Length > 512)
            return false;
        if (fileName.IndexOfAny(['"', '\'', ';', '|', '&', '<', '>', '`', '\n', '\r']) >= 0)
            return false;
        if (fileName.Contains("..", StringComparison.Ordinal))
            return false;

        string exeName = Path.GetFileName(fileName);
        return exeName.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)
            || exeName.Equals("msiexec", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsSafeWingetId(string value)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length > 256)
            return false;
        return value.IndexOfAny(['"', '\'', ';', '|', '&', '<', '>', '`', '\n', '\r', '$']) < 0;
    }

    private static bool IsSafePackageIdentity(string value)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length is < 2 or > 256)
            return false;
        foreach (char c in value)
        {
            if (char.IsLetterOrDigit(c) || c is '.' or '_' or '-' or '+')
                continue;
            return false;
        }
        return true;
    }

    private static readonly Regex ProductCodePattern =
        new(@"^\{[0-9A-Fa-f]{8}(?:-[0-9A-Fa-f]{4}){3}-[0-9A-Fa-f]{12}\}$", RegexOptions.Compiled);

    /// <summary>
    /// Tenta executar o processo de desinstalação com os parâmetros fornecidos.
    /// </summary>
    private async Task<(bool Success, string ErrorMessage)> TryRunUninstallProcess(
        string fileName, string arguments, CancellationToken cancellationToken, bool requireElevation = true)
    {
        try
        {
            var startInfo = new ProcessStartInfo
            {
                FileName = fileName,
                Arguments = arguments,
                CreateNoWindow = true,
                UseShellExecute = true,
                Verb = requireElevation ? "runas" : "", // Elevação apenas se necessário
                WindowStyle = ProcessWindowStyle.Hidden
            };

            using var process = Process.Start(startInfo);
            if (process == null)
                return (false, "Process.Start retornou null");

            // Timeout de 5 minutos para desinstalação (estilo BCUninstaller)
            var timeoutTask = Task.Delay(TimeSpan.FromMinutes(5), cancellationToken);
            var exitTask = process.WaitForExitAsync(cancellationToken);

            var completedTask = await Task.WhenAny(exitTask, timeoutTask);

            if (completedTask == timeoutTask)
            {
                try
                {
                    process.Kill(entireProcessTree: true);
                }
                catch { }
                return (false, "Timeout: desinstalação excedeu 5 minutos");
            }

            // Verifica códigos de sucesso (0 = sucesso, 3010 = sucesso com reboot pendente)
            // Alguns instaladores retornam códigos não-zero mas ainda são bem-sucedidos
            var exitCode = process.ExitCode;
            return exitCode == 0 || exitCode == 3010 || exitCode == 1605 || exitCode == 1641
                ? (true, string.Empty)
                : (false, $"Exit code: {exitCode}");
        }
        catch (OperationCanceledException)
        {
            return (false, "Cancelado pelo usuário");
        }
        catch (Exception ex)
        {
            return (false, ex.Message);
        }
    }

    /// <summary>
    /// Tenta métodos de fallback quando a desinstalação normal falha (estilo BCUninstaller).
    /// Tenta diferentes combinações de parâmetros silenciosos e métodos alternativos.
    /// </summary>
    private async Task<bool> TryFallbackUninstall(
        InstalledAppDetail app, string fileName, string existingArguments, CancellationToken cancellationToken)
    {
        Debug.WriteLine($"[UninstallerEngine] Tentando fallback para {app.DisplayName}");

        // Se não tiver UninstallString válido, tenta detectar o tipo pelo ID e usar métodos específicos
        if (string.IsNullOrEmpty(fileName) || string.IsNullOrEmpty(existingArguments))
        {
            return await TryIdBasedUninstall(app, cancellationToken);
        }

        // Fallback 1: Tenta sem argumentos adicionais (usando apenas o UninstallString original)
        var result1 = await TryRunUninstallProcess(fileName, existingArguments, cancellationToken, false);
        if (result1.Success)
            return true;

        // Fallback 2: Tenta com parâmetros genéricos mais agressivos
        var fallbackArgs = $"{existingArguments} /S /silent /quiet /VERYSILENT /SUPPRESSMSGBOXES /NORESTART".Trim();
        var result2 = await TryRunUninstallProcess(fileName, fallbackArgs, cancellationToken, false);
        if (result2.Success)
            return true;

        // Fallback 3: Tenta com parâmetros específicos para MSI se for msiexec
        if (fileName.Contains("msiexec", StringComparison.OrdinalIgnoreCase))
        {
            var msiFallback = $"{existingArguments} /x /qn /norestart REBOOT=ReallySuppress".Trim();
            var result3 = await TryRunUninstallProcess(fileName, msiFallback, cancellationToken, false);
            if (result3.Success)
                return true;
        }

        // Fallback 4: Tenta desinstalação baseada no ID se todos os métodos anteriores falharem
        return await TryIdBasedUninstall(app, cancellationToken);
    }

    /// <summary>
    /// Tenta desinstalação baseada no ID do aplicativo quando não há UninstallString válido.
    /// </summary>
    private async Task<bool> TryIdBasedUninstall(InstalledAppDetail app, CancellationToken cancellationToken)
    {
        Debug.WriteLine($"[UninstallerEngine] Tentando desinstalação baseada no ID: {app.Id}");

        // MSIX/Store apps
        if (app.Id.StartsWith("MSIX\\", StringComparison.OrdinalIgnoreCase) ||
            app.Id.StartsWith("9N", StringComparison.OrdinalIgnoreCase))
        {
            var packageFullName = app.Id.StartsWith("MSIX\\", StringComparison.OrdinalIgnoreCase)
                ? app.Id[5..]
                : app.Id;
            if (!IsSafePackageIdentity(packageFullName))
                return false;

            var result = await TryRunUninstallProcess(
                "powershell.exe",
                $"-NoProfile -ExecutionPolicy Bypass -Command \"Get-AppxPackage -Name '{packageFullName}' | Remove-AppxPackage -ErrorAction SilentlyContinue\"",
                cancellationToken,
                false);
            return result.Success;
        }

        // MSI packages (se o ID parece ser um GUID)
        if (ProductCodePattern.IsMatch(app.Id))
        {
            var result = await TryRunUninstallProcess(
                "msiexec.exe",
                $"/x {app.Id} /qn /norestart REBOOT=ReallySuppress",
                cancellationToken,
                false);
            return result.Success;
        }

        // Tenta winget se disponível
        if (IsSafeWingetId(app.Id))
        {
            var result = await TryRunUninstallProcess(
                "winget.exe",
                $"uninstall --id \"{app.Id}\" --silent --accept-source-agreements --disable-interactivity",
                cancellationToken,
                false);
            if (result.Success)
                return true;
        }

        Debug.WriteLine($"[UninstallerEngine] Desinstalação baseada no ID falhou para {app.DisplayName}");
        return false;
    }

    /// <summary>
    /// Identifica a tecnologia do instalador pela assinatura do executável de
    /// desinstalação e/ou do comando registrado, para decidir os parâmetros
    /// silenciosos corretos. Expandido para detectar Squirrel/Electron, package
    /// managers e Windows Store Apps (estilo BCUninstaller).
    /// </summary>
    public static InstallerKind DetectInstallerKind(string fileName, string rawCommand, string? appId = null)
    {
        // MSIX/Store apps detection baseado no ID
        if (!string.IsNullOrEmpty(appId) && (appId.StartsWith("MSIX\\", StringComparison.OrdinalIgnoreCase) ||
            appId.StartsWith("9N", StringComparison.OrdinalIgnoreCase) ||
            appId.StartsWith("8wekyb3d8bbwe", StringComparison.OrdinalIgnoreCase)))
            return InstallerKind.WindowsStore;

        if (fileName.Contains("msiexec", StringComparison.OrdinalIgnoreCase))
            return InstallerKind.Msiexec;

        if (InnoSetupPattern.IsMatch(fileName))
            return InstallerKind.InnoSetup;

        if (rawCommand.Contains(InstallShieldFolderMarker, StringComparison.OrdinalIgnoreCase))
            return InstallerKind.InstallShield;

        if (WisePattern.IsMatch(fileName))
            return InstallerKind.Wise;

        if (NsisPattern.IsMatch(fileName))
            return InstallerKind.Nsis;

        // Squirrel/Electron detection: Update.exe with --uninstall
        if (SquirrelPattern.IsMatch(fileName) && SquirrelUninstallPattern.IsMatch(rawCommand))
            return InstallerKind.Squirrel;

        // Chocolatey detection
        if (ChocolateyPattern.IsMatch(fileName))
            return InstallerKind.Chocolatey;

        // Steam detection
        if (SteamPattern.IsMatch(fileName))
            return InstallerKind.Steam;

        // Windows Store detection via PowerShell commands
        if (WindowsStorePattern.IsMatch(rawCommand))
            return InstallerKind.WindowsStore;

        // Scoop detection
        if (ScoopPattern.IsMatch(fileName))
            return InstallerKind.Scoop;

        return InstallerKind.Unknown;
    }

    /// <summary>
    /// Aplica os parâmetros silenciosos específicos de cada tecnologia. Nunca combina
    /// flags de tecnologias diferentes: instaladores costumam rejeitar (erro) um
    /// parâmetro que não reconhecem. Expandido com parâmetros para Squirrel, Chocolatey,
    /// Steam, Windows Store e Scoop (estilo BCUninstaller).
    /// </summary>
    private static string AppendSilentSwitches(InstallerKind kind, string existingArguments, string? rawCommand = null, string? appId = null)
    {
        switch (kind)
        {
            case InstallerKind.Msiexec:
                // https://learn.microsoft.com/windows/win32/msi/standard-installer-command-line-options
                // Adicionado logging para debug e retry logic
                return existingArguments.Contains("/qn", StringComparison.OrdinalIgnoreCase)
                    ? existingArguments
                    : $"{existingArguments} /qn /norestart REBOOT=ReallySuppress".Trim();

            case InstallerKind.InnoSetup:
                // https://jrsoftware.org/ishelp/topic_uninstcmdline.htm
                // Adicionado /SP- para pular páginas iniciais
                return $"{existingArguments} /VERYSILENT /SUPPRESSMSGBOXES /NORESTART /SP-".Trim();

            case InstallerKind.Nsis:
                // NSIS: /S é capital, adicionado /NCRC para skip CRC checks (melhor compatibilidade)
                return $"{existingArguments} /S /NCRC".Trim();

            case InstallerKind.InstallShield:
                // InstallScript: /s para basic silent, /f1 para response file, /f2 para log
                // Sem response file (.iss) gravado previamente não há uninstall 100%
                // silencioso garantido para projetos InstallScript; "/s" é o melhor
                // esforço documentado pela Revenera para suprimir o máximo de diálogos.
                return $"{existingArguments} /s /sms".Trim();

            case InstallerKind.Wise:
                // Wise: /s para silent, /u para uninstall mode
                return $"{existingArguments} /s /u".Trim();

            case InstallerKind.Squirrel:
                // Squirrel/Electron: --uninstall -s para silent
                return existingArguments.Contains("--uninstall", StringComparison.OrdinalIgnoreCase)
                    ? $"{existingArguments} -s".Trim()
                    : $"{existingArguments} --uninstall -s".Trim();

            case InstallerKind.Chocolatey:
                // Chocolatey: -y para yes, --force para force
                return existingArguments.Contains("uninstall", StringComparison.OrdinalIgnoreCase)
                    ? $"{existingArguments} -y --force".Trim()
                    : $"{existingArguments} uninstall -y --force".Trim();

            case InstallerKind.Steam:
                // Steam: steam.exe uninstall <appid> /silent
                return existingArguments.Contains("uninstall", StringComparison.OrdinalIgnoreCase)
                    ? $"{existingArguments} /silent".Trim()
                    : existingArguments;

            case InstallerKind.WindowsStore:
                return existingArguments;

            case InstallerKind.Scoop:
                // Scoop: scoop uninstall já é silencioso
                return existingArguments;

            case InstallerKind.Nullsoft:
                // Generic Nullsoft: /S é capital
                return $"{existingArguments} /S".Trim();

            case InstallerKind.Unknown:
            default:
                // Combinação tolerada pela maioria dos instaladores não identificados
                // (parâmetros desconhecidos costumam ser ignorados silenciosamente
                // pelos motores mais comuns fora dos casos já cobertos acima).
                return $"{existingArguments} /S /VERYSILENT /SUPPRESSMSGBOXES /NORESTART /NCRC".Trim();
        }
    }

    /// <summary>
    /// Separa o executável dos argumentos de um UninstallString de registro, sem
    /// depender de cmd.exe (cuja regra de parsing de aspas quebra com facilidade).
    /// Segue a mesma convenção usada pelo Registro do Windows: caminho entre aspas
    /// quando contém espaços, ou token único até o primeiro espaço quando não contém.
    /// </summary>
    private static bool TrySplitCommand(string commandLine, out string fileName, out string arguments)
    {
        fileName = string.Empty;
        arguments = string.Empty;

        string trimmed = commandLine.Trim();
        if (trimmed.Length == 0) return false;

        if (trimmed[0] == '"')
        {
            int closingQuote = trimmed.IndexOf('"', 1);
            if (closingQuote < 0) return false;

            fileName = trimmed[1..closingQuote];
            arguments = trimmed[(closingQuote + 1)..].Trim();
        }
        else
        {
            int firstSpace = trimmed.IndexOf(' ');
            if (firstSpace < 0)
            {
                fileName = trimmed;
                arguments = string.Empty;
            }
            else
            {
                fileName = trimmed[..firstSpace];
                arguments = trimmed[(firstSpace + 1)..].Trim();
            }
        }

        return !string.IsNullOrWhiteSpace(fileName);
    }

    /// <summary>
    /// Executa a limpeza agressiva de ficheiros residuais e chaves de registo associadas à aplicação.
    /// Expandido para incluir serviços, tarefas agendadas, context menus e arquivos residuais
    /// (estilo BCUninstaller).
    /// </summary>
    public async Task<bool> PerformAggressiveCleanupAsync(InstalledAppDetail app, CancellationToken cancellationToken = default)
    {
        return await Task.Run(() =>
        {
            try
            {
                bool success = true;

                // 1. Limpeza de diretórios de instalação conhecidos
                if (IsSafeToDeleteDirectory(app.InstallLocation) && Directory.Exists(app.InstallLocation))
                {
                    try
                    {
                        Directory.Delete(app.InstallLocation, recursive: true);
                    }
                    catch (Exception ex)
                    {
                        Debug.WriteLine($"[Cleanup] Não foi possível apagar a pasta de instalação: {ex.Message}");
                        success = false;
                    }
                }

                // 2. Limpeza em pastas de utilizador e sistema (AppData, LocalAppData, ProgramData)
                var cleanPaths = new[]
                {
                    Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), app.DisplayName),
                    Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), app.DisplayName),
                    Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), app.DisplayName)
                };

                foreach (var path in cleanPaths)
                {
                    if (IsSafeAppFolderName(app.DisplayName) && IsSafeToDeleteDirectory(path) && Directory.Exists(path))
                    {
                        try
                        {
                            Directory.Delete(path, recursive: true);
                        }
                        catch (Exception ex)
                        {
                            Debug.WriteLine($"[Cleanup] Erro ao limpar diretório residual {path}: {ex.Message}");
                        }
                    }
                }

                // 3. Limpeza de chaves de registo residuais órfãs (se aplicável pelo DisplayName ou AppKey)
                CleanRegistryKeys(app.DisplayName);

                // 4. Remoção de serviços do Windows relacionados ao aplicativo
                RemoveRelatedServices(app);

                // 5. Remoção de tarefas agendadas relacionadas ao aplicativo
                RemoveScheduledTasks(app.DisplayName);

                // 6. Limpeza de context menus do Windows Explorer
                CleanContextMenuHandlers(app.DisplayName);

                // 7. Limpeza de arquivos temporários e cache
                CleanTempFiles(app.DisplayName);

                return success;
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[AggressiveCleanup] Erro geral: {ex.Message}");
                return false;
            }
        }, cancellationToken);
    }

    /// <summary>
    /// Remove serviços do Windows relacionados ao aplicativo.
    /// </summary>
    private void RemoveRelatedServices(InstalledAppDetail app)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(app.InstallLocation) || app.InstallLocation.Length < 8)
                return;

            string installLocation;
            try { installLocation = Path.GetFullPath(app.InstallLocation); }
            catch { return; }

            using var servicesKey = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Services", writable: false);
            if (servicesKey == null) return;

            var serviceNames = servicesKey.GetSubKeyNames();
            foreach (var serviceName in serviceNames)
            {
                try
                {
                    using var serviceKey = servicesKey.OpenSubKey(serviceName, writable: false);
                    if (serviceKey != null)
                    {
                        var imagePath = serviceKey.GetValue("ImagePath") as string;

                        if (!string.IsNullOrEmpty(imagePath)
                            && imagePath.Contains(installLocation, StringComparison.OrdinalIgnoreCase))
                        {
                            try
                            {
                                // Tenta parar e remover o serviço
                                using var process = Process.Start(new ProcessStartInfo
                                {
                                    FileName = "sc.exe",
                                    Arguments = $"stop \"{serviceName}\"",
                                    CreateNoWindow = true,
                                    UseShellExecute = false,
                                    WindowStyle = ProcessWindowStyle.Hidden
                                });
                                process?.WaitForExit();

                                using var deleteProcess = Process.Start(new ProcessStartInfo
                                {
                                    FileName = "sc.exe",
                                    Arguments = $"delete \"{serviceName}\"",
                                    CreateNoWindow = true,
                                    UseShellExecute = false,
                                    WindowStyle = ProcessWindowStyle.Hidden
                                });
                                deleteProcess?.WaitForExit();

                                Debug.WriteLine($"[Cleanup] Serviço removido: {serviceName}");
                            }
                            catch (Exception ex)
                            {
                                Debug.WriteLine($"[Cleanup] Erro ao remover serviço {serviceName}: {ex.Message}");
                            }
                        }
                    }
                }
                catch (Exception ex)
                {
                    Debug.WriteLine($"[Cleanup] Erro ao verificar serviço {serviceName}: {ex.Message}");
                }
            }
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[Cleanup] Erro ao enumerar serviços: {ex.Message}");
        }
    }

    /// <summary>
    /// Remove tarefas agendadas do Windows relacionadas ao aplicativo.
    /// </summary>
    private void RemoveScheduledTasks(string appName)
    {
        try
        {
            using var process = Process.Start(new ProcessStartInfo
            {
                FileName = "schtasks.exe",
                Arguments = "/query /fo LIST",
                CreateNoWindow = true,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                WindowStyle = ProcessWindowStyle.Hidden
            });

            if (process == null) return;

            var output = process.StandardOutput.ReadToEnd();
            process.WaitForExit();

            var lines = output.Split(new[] { Environment.NewLine }, StringSplitOptions.RemoveEmptyEntries);
            foreach (var line in lines)
            {
                if (line.Contains("TaskName:", StringComparison.OrdinalIgnoreCase))
                {
                    var taskName = line.Split(':')[1].Trim();
                    if (appName.Length >= 6 && taskName.Contains(appName, StringComparison.OrdinalIgnoreCase))
                    {
                        try
                        {
                            using var deleteProcess = Process.Start(new ProcessStartInfo
                            {
                                FileName = "schtasks.exe",
                                Arguments = $"/delete /tn \"{taskName}\" /f",
                                CreateNoWindow = true,
                                UseShellExecute = false,
                                WindowStyle = ProcessWindowStyle.Hidden
                            });
                            deleteProcess?.WaitForExit();
                            Debug.WriteLine($"[Cleanup] Tarefa agendada removida: {taskName}");
                        }
                        catch (Exception ex)
                        {
                            Debug.WriteLine($"[Cleanup] Erro ao remover tarefa {taskName}: {ex.Message}");
                        }
                    }
                }
            }
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[Cleanup] Erro ao enumerar tarefas agendadas: {ex.Message}");
        }
    }

    /// <summary>
    /// Limpa context menu handlers do Windows Explorer relacionados ao aplicativo.
    /// </summary>
    private void CleanContextMenuHandlers(string appName)
    {
        try
        {
            var contextMenuKeys = new[]
            {
                @"Software\Classes\*\shellex\ContextMenuHandlers",
                @"Software\Classes\Directory\shellex\ContextMenuHandlers",
                @"Software\Classes\Folder\shellex\ContextMenuHandlers"
            };

            foreach (var contextMenuKey in contextMenuKeys)
            {
                try
                {
                    using var key = Registry.CurrentUser.OpenSubKey(contextMenuKey, writable: true);
                    if (key == null) continue;

                    var handlerNames = key.GetSubKeyNames();
                    foreach (var handlerName in handlerNames)
                    {
                        try
                        {
                            if (handlerName.Contains(appName, StringComparison.OrdinalIgnoreCase))
                            {
                                key.DeleteSubKeyTree(handlerName, throwOnMissingSubKey: false);
                                Debug.WriteLine($"[Cleanup] Context menu handler removido: {handlerName}");
                            }
                        }
                        catch (Exception ex)
                        {
                            Debug.WriteLine($"[Cleanup] Erro ao remover context handler {handlerName}: {ex.Message}");
                        }
                    }
                }
                catch (Exception ex)
                {
                    Debug.WriteLine($"[Cleanup] Erro ao acessar {contextMenuKey}: {ex.Message}");
                }
            }
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[Cleanup] Erro geral ao limpar context menus: {ex.Message}");
        }
    }

    /// <summary>
    /// Limpa arquivos temporários e cache relacionados ao aplicativo.
    /// </summary>
    private void CleanTempFiles(string appName)
    {
        try
        {
            if (!IsSafeAppFolderName(appName) || appName.Length < 5)
                return;

            var tempPath = Path.GetTempPath();
            var tempFiles = Directory.GetFiles(tempPath, $"*{appName}*", SearchOption.TopDirectoryOnly);

            foreach (var tempFile in tempFiles)
            {
                try
                {
                    File.Delete(tempFile);
                    Debug.WriteLine($"[Cleanup] Arquivo temporário removido: {tempFile}");
                }
                catch (Exception ex)
                {
                    Debug.WriteLine($"[Cleanup] Erro ao remover arquivo temporário {tempFile}: {ex.Message}");
                }
            }

            // Limpa pastas temporárias com o nome do aplicativo
            var tempDirs = Directory.GetDirectories(tempPath, $"*{appName}*", SearchOption.TopDirectoryOnly);
            foreach (var tempDir in tempDirs)
            {
                try
                {
                    Directory.Delete(tempDir, recursive: true);
                    Debug.WriteLine($"[Cleanup] Diretório temporário removido: {tempDir}");
                }
                catch (Exception ex)
                {
                    Debug.WriteLine($"[Cleanup] Erro ao remover diretório temporário {tempDir}: {ex.Message}");
                }
            }
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[Cleanup] Erro ao limpar arquivos temporários: {ex.Message}");
        }
    }

    private void CleanRegistryKeys(string appName)
    {
        if (string.IsNullOrEmpty(appName)) return;

        var registryHives = new[]
        {
            Registry.CurrentUser.OpenSubKey(@"Software", writable: true),
            Registry.LocalMachine.OpenSubKey(@"SOFTWARE", writable: true)
        };

        foreach (var hive in registryHives)
        {
            if (hive == null) continue;
            try
            {
                using var subKey = hive.OpenSubKey(appName, writable: true);
                if (subKey != null)
                {
                    hive.DeleteSubKeyTree(appName, throwOnMissingSubKey: false);
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[RegistryCleanup] Erro ao limpar registo para {appName}: {ex.Message}");
            }
            finally
            {
                hive.Dispose();
            }
        }
    }

    /// <summary>
    /// Detecta e remove diretórios vazios nos locais comuns de instalação
    /// (estilo BCUninstaller - empty directory cleanup).
    /// </summary>
    public async Task CleanEmptyDirectoriesAsync(CancellationToken cancellationToken = default)
    {
        await Task.Run(() =>
        {
            try
            {
                var directoriesToClean = new[]
                {
                    Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
                    Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86),
                    Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
                    Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs")
                };

                foreach (var baseDir in directoriesToClean)
                {
                    if (!Directory.Exists(baseDir)) continue;

                    try
                    {
                        var emptyDirs = FindEmptyDirectories(baseDir);
                        foreach (var emptyDir in emptyDirs)
                        {
                            try
                            {
                                Directory.Delete(emptyDir, recursive: false);
                                Debug.WriteLine($"[EmptyDirCleanup] Diretório vazio removido: {emptyDir}");
                            }
                            catch (Exception ex)
                            {
                                Debug.WriteLine($"[EmptyDirCleanup] Não foi possível remover {emptyDir}: {ex.Message}");
                            }
                        }
                    }
                    catch (Exception ex)
                    {
                        Debug.WriteLine($"[EmptyDirCleanup] Erro ao varrer {baseDir}: {ex.Message}");
                    }
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[EmptyDirCleanup] Erro geral: {ex.Message}");
            }
        }, cancellationToken);
    }

    /// <summary>
    /// Encontra recursivamente diretórios vazios a partir de um diretório base.
    /// </summary>
    private List<string> FindEmptyDirectories(string basePath)
    {
        var emptyDirs = new List<string>();

        try
        {
            var directories = Directory.GetDirectories(basePath, "*", SearchOption.AllDirectories);
            foreach (var dir in directories)
            {
                try
                {
                    // Verifica se o diretório está vazio (sem arquivos e sem subdiretórios)
                    var files = Directory.GetFiles(dir);
                    var subDirs = Directory.GetDirectories(dir);

                    if (files.Length == 0 && subDirs.Length == 0)
                    {
                        emptyDirs.Add(dir);
                    }
                }
                catch (UnauthorizedAccessException)
                {
                    // Ignora diretórios sem permissão
                    continue;
                }
                catch (Exception ex)
                {
                    Debug.WriteLine($"[EmptyDirCleanup] Erro ao verificar {dir}: {ex.Message}");
                }
            }
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[EmptyDirCleanup] Erro ao enumerar diretórios: {ex.Message}");
        }

        return emptyDirs;
    }

    /// <summary>
    /// Detecta arquivos residuais (leftovers) após desinstalação usando heurísticas
    /// avançadas (estilo BCUninstaller junk detection).
    /// </summary>
    public async Task<List<string>> DetectLeftoversAsync(InstalledAppDetail app, CancellationToken cancellationToken = default)
    {
        return await Task.Run(() =>
        {
            var leftovers = new List<string>();

            try
            {
                // 1. Procura por arquivos com nome similar em pastas comuns
                var searchPaths = new[]
                {
                    Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                    Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
                    Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
                    Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86)
                };

                foreach (var basePath in searchPaths)
                {
                    if (!Directory.Exists(basePath)) continue;

                    try
                    {
                        // Procura diretórios que contenham o nome do aplicativo
                        var matchingDirs = Directory.GetDirectories(basePath, $"*{app.DisplayName}*", SearchOption.TopDirectoryOnly);
                        leftovers.AddRange(matchingDirs);

                        // Procura arquivos com nome similar
                        var matchingFiles = Directory.GetFiles(basePath, $"*{app.DisplayName}*", SearchOption.AllDirectories);
                        leftovers.AddRange(matchingFiles);
                    }
                    catch (UnauthorizedAccessException)
                    {
                        continue;
                    }
                    catch (Exception ex)
                    {
                        Debug.WriteLine($"[LeftoverDetection] Erro ao varrer {basePath}: {ex.Message}");
                    }
                }

                // 2. Procura por arquivos temporários e logs
                var tempPath = Path.GetTempPath();
                if (Directory.Exists(tempPath))
                {
                    try
                    {
                        var tempFiles = Directory.GetFiles(tempPath, $"*{app.DisplayName}*", SearchOption.AllDirectories);
                        leftovers.AddRange(tempFiles);
                    }
                    catch (Exception ex)
                    {
                        Debug.WriteLine($"[LeftoverDetection] Erro ao varrer temp: {ex.Message}");
                    }
                }

                // 3. Remove duplicatas e filtros de diretórios protegidos
                leftovers = leftovers.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
                leftovers = leftovers.Where(f => !IsProtectedPath(f)).ToList();
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[LeftoverDetection] Erro geral: {ex.Message}");
            }

            return leftovers;
        }, cancellationToken);
    }

    /// <summary>
    /// Verifica se um caminho é protegido e não deve ser removido automaticamente.
    /// </summary>
    private bool IsProtectedPath(string path)
    {
        var protectedPaths = new[]
        {
            Environment.GetFolderPath(Environment.SpecialFolder.Windows),
            Environment.GetFolderPath(Environment.SpecialFolder.System),
            Environment.GetFolderPath(Environment.SpecialFolder.SystemX86),
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles) + "\\Common Files",
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86) + "\\Common Files",
            Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData) + "\\Microsoft",
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData) + "\\Microsoft",
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData) + "\\Microsoft"
        };

        return protectedPaths.Any(protectedPath =>
            !string.IsNullOrWhiteSpace(protectedPath)
            && path.StartsWith(protectedPath, StringComparison.OrdinalIgnoreCase));
    }

    private static bool IsSafeAppFolderName(string name)
    {
        if (string.IsNullOrWhiteSpace(name) || name.Trim().Length < 3)
            return false;
        string trimmed = name.Trim();
        if (trimmed.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
            return false;
        string[] blocked = ["Microsoft", "Windows", "Temp", "Common Files", "WinProvision", "WinProvisionStore", "Program Files", "System32", "SysWOW64"];
        return !blocked.Any(item => trimmed.Equals(item, StringComparison.OrdinalIgnoreCase));
    }

    private static bool IsSafeToDeleteDirectory(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
            return false;

        string full;
        try { full = Path.GetFullPath(Environment.ExpandEnvironmentVariables(path)); }
        catch { return false; }

        if (!Path.IsPathRooted(full) || full.Length < 8)
            return false;

        string? driveRoot = Path.GetPathRoot(full);
        if (!string.IsNullOrEmpty(driveRoot)
            && string.Equals(full.TrimEnd('\\'), driveRoot.TrimEnd('\\'), StringComparison.OrdinalIgnoreCase))
            return false;

        string[] allowedParents =
        [
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs"),
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData)
        ];

        return allowedParents.Any(parent =>
            !string.IsNullOrWhiteSpace(parent)
            && full.StartsWith(parent.TrimEnd('\\') + "\\", StringComparison.OrdinalIgnoreCase)
            && !string.Equals(full.TrimEnd('\\'), parent.TrimEnd('\\'), StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// Executa a desinstalação silenciosa seguida da limpeza agressiva de resíduos.
    /// Modo agressivo é sempre aplicado (sem alternativa "padrão" à parte, igual ao
    /// fluxo do Office): a limpeza só roda depois de uma desinstalação bem-sucedida
    /// e nunca mascara uma falha real de desinstalação.
    /// </summary>
    public async Task<bool> UninstallAggressivelyAsync(InstalledAppDetail app, CancellationToken cancellationToken = default)
    {
        var uninstalled = await RunSilentUninstallAsync(app, cancellationToken);
        if (!uninstalled) return false;

        await PerformAggressiveCleanupAsync(app, cancellationToken);
        return true;
    }

    /// <summary>
    /// Executa a desinstalação forçada quando o desinstalador padrão falha.
    /// Usa detecção de processos, janelas e atalhos para identificar e remover o aplicativo
    /// (estilo BCUninstaller Force Uninstall).
    /// </summary>
    public async Task<bool> ForceUninstallAsync(InstalledAppDetail app, CancellationToken cancellationToken = default)
    {
        return await Task.Run(() =>
        {
            try
            {
                // 1. Primeiro tenta desinstalação normal
                var normalResult = RunSilentUninstallAsync(app, cancellationToken).GetAwaiter().GetResult();
                if (normalResult)
                {
                    PerformAggressiveCleanupAsync(app, cancellationToken).GetAwaiter().GetResult();
                    return true;
                }

                Debug.WriteLine($"[ForceUninstall] Desinstalação normal falhou, tentando force uninstall para {app.DisplayName}");

                // 2. Tenta matar processos relacionados ao aplicativo
                KillRelatedProcesses(app.DisplayName);

                // 3. Remove atalhos do desktop e menu iniciar
                RemoveShortcuts(app.DisplayName);

                // 4. Remove pasta de instalação se existir
                if (IsSafeToDeleteDirectory(app.InstallLocation) && Directory.Exists(app.InstallLocation))
                {
                    try
                    {
                        Directory.Delete(app.InstallLocation, recursive: true);
                        Debug.WriteLine($"[ForceUninstall] Pasta de instalação removida: {app.InstallLocation}");
                    }
                    catch (Exception ex)
                    {
                        Debug.WriteLine($"[ForceUninstall] Não foi possível remover pasta de instalação: {ex.Message}");
                    }
                }

                // 5. Limpeza agressiva de resíduos
                PerformAggressiveCleanupAsync(app, cancellationToken).GetAwaiter().GetResult();

                // 6. Remove chaves de registro do desinstalador
                RemoveUninstallerRegistryKeys(app);

                return true;
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[ForceUninstall] Erro ao executar force uninstall: {ex.Message}");
                return false;
            }
        }, cancellationToken);
    }

    /// <summary>
    /// Mata processos relacionados ao aplicativo pelo nome do executável.
    /// </summary>
    private static readonly HashSet<string> ProtectedProcesses = new(StringComparer.OrdinalIgnoreCase)
    {
        "explorer", "dwm", "svchost", "csrss", "winlogon", "lsass", "services", "smss",
        "system", "idle", "wininit", "fontdrvhost", "conhost", "sihost", "taskhostw",
        "searchhost", "runtimebroker", "dllhost", "ctfmon", "winprovision", "msedge",
        "chrome", "firefox", "powershell", "pwsh", "cmd", "msiexec"
    };

    private void KillRelatedProcesses(string appName)
    {
        if (string.IsNullOrWhiteSpace(appName) || appName.Trim().Length < 6)
            return;

        string token = new string(appName.Trim().TakeWhile(c => char.IsLetterOrDigit(c)).ToArray());
        if (token.Length < 6 || ProtectedProcesses.Contains(token))
            return;

        try
        {
            var processes = Process.GetProcesses();
            foreach (var process in processes)
            {
                try
                {
                    if (ProtectedProcesses.Contains(process.ProcessName))
                        continue;

                    if (process.ProcessName.Equals(token, StringComparison.OrdinalIgnoreCase)
                        || process.ProcessName.StartsWith(token, StringComparison.OrdinalIgnoreCase))
                    {
                        process.Kill(entireProcessTree: true);
                        Debug.WriteLine($"[ForceUninstall] Processo morto: {process.ProcessName}");
                    }
                }
                catch (Exception ex)
                {
                    Debug.WriteLine($"[ForceUninstall] Erro ao matar processo {process.ProcessName}: {ex.Message}");
                }
            }
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[ForceUninstall] Erro ao enumerar processos: {ex.Message}");
        }
    }

    /// <summary>
    /// Remove atalhos do desktop e menu iniciar relacionados ao aplicativo.
    /// </summary>
    private void RemoveShortcuts(string appName)
    {
        try
        {
            var desktopPath = Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);
            var startMenuPath = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.StartMenu),
                "Programs");

            var pathsToCheck = new[] { desktopPath, startMenuPath };

            foreach (var basePath in pathsToCheck)
            {
                if (!Directory.Exists(basePath)) continue;

                var shortcuts = Directory.GetFiles(basePath, "*.lnk", SearchOption.AllDirectories);
                foreach (var shortcut in shortcuts)
                {
                    try
                    {
                        var fileName = Path.GetFileNameWithoutExtension(shortcut);
                        if (IsSafeAppFolderName(appName)
                            && appName.Length >= 5
                            && fileName.Equals(appName, StringComparison.OrdinalIgnoreCase))
                        {
                            File.Delete(shortcut);
                            Debug.WriteLine($"[ForceUninstall] Atalho removido: {shortcut}");
                        }
                    }
                    catch (Exception ex)
                    {
                        Debug.WriteLine($"[ForceUninstall] Erro ao remover atalho {shortcut}: {ex.Message}");
                    }
                }
            }
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[ForceUninstall] Erro ao remover atalhos: {ex.Message}");
        }
    }

    /// <summary>
    /// Remove chaves de registro do desinstalador (HKLM\Software\Microsoft\Windows\CurrentVersion\Uninstall
    /// e HKCU equivalentes).
    /// </summary>
    private void RemoveUninstallerRegistryKeys(InstalledAppDetail app)
    {
        try
        {
            var uninstallKeys = new[]
            {
                @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall",
                @"SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall"
            };

            foreach (var uninstallKey in uninstallKeys)
            {
                // HKLM
                try
                {
                    using var hklmKey = Registry.LocalMachine.OpenSubKey(uninstallKey, writable: true);
                    if (hklmKey != null)
                    {
                        var subKeyNames = hklmKey.GetSubKeyNames();
                        foreach (var subKeyName in subKeyNames)
                        {
                            try
                            {
                                using var subKey = hklmKey.OpenSubKey(subKeyName, writable: false);
                                if (subKey != null)
                                {
                                    var displayName = subKey.GetValue("DisplayName") as string;
                                    if (!string.IsNullOrEmpty(displayName) &&
                                        displayName.Equals(app.DisplayName, StringComparison.OrdinalIgnoreCase))
                                    {
                                        hklmKey.DeleteSubKeyTree(subKeyName, throwOnMissingSubKey: false);
                                        Debug.WriteLine($"[ForceUninstall] Chave de registro removida (HKLM): {subKeyName}");
                                    }
                                }
                            }
                            catch (Exception ex)
                            {
                                Debug.WriteLine($"[ForceUninstall] Erro ao verificar subchave {subKeyName}: {ex.Message}");
                            }
                        }
                    }
                }
                catch (Exception ex)
                {
                    Debug.WriteLine($"[ForceUninstall] Erro ao acessar HKLM {uninstallKey}: {ex.Message}");
                }

                // HKCU
                try
                {
                    using var hkcuKey = Registry.CurrentUser.OpenSubKey(uninstallKey, writable: true);
                    if (hkcuKey != null)
                    {
                        var subKeyNames = hkcuKey.GetSubKeyNames();
                        foreach (var subKeyName in subKeyNames)
                        {
                            try
                            {
                                using var subKey = hkcuKey.OpenSubKey(subKeyName, writable: false);
                                if (subKey != null)
                                {
                                    var displayName = subKey.GetValue("DisplayName") as string;
                                    if (!string.IsNullOrEmpty(displayName) &&
                                        displayName.Equals(app.DisplayName, StringComparison.OrdinalIgnoreCase))
                                    {
                                        hkcuKey.DeleteSubKeyTree(subKeyName, throwOnMissingSubKey: false);
                                        Debug.WriteLine($"[ForceUninstall] Chave de registro removida (HKCU): {subKeyName}");
                                    }
                                }
                            }
                            catch (Exception ex)
                            {
                                Debug.WriteLine($"[ForceUninstall] Erro ao verificar subchave {subKeyName}: {ex.Message}");
                            }
                        }
                    }
                }
                catch (Exception ex)
                {
                    Debug.WriteLine($"[ForceUninstall] Erro ao acessar HKCU {uninstallKey}: {ex.Message}");
                }
            }
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[ForceUninstall] Erro geral ao remover chaves de registro: {ex.Message}");
        }
    }
}
