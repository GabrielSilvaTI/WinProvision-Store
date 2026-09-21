using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Media;
using System.Windows.Threading;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Wpf.Ui;
using Wpf.Ui.Abstractions;
using Wpf.Ui.Appearance;
using WinProvision.Core.Services;
using WinProvision.Core.Services.Backup;
using WinProvision.Core.Services.Office;
using WinProvision.Core.Services.Profile;
using WinProvision.Core.Services.Provisioning;
using WinProvision.Store.Controls;
using WinProvision.Store.Services;

namespace WinProvision.Store;

public class CustomNavigationViewPageProvider(IServiceProvider serviceProvider) : INavigationViewPageProvider
{
    public object? GetPage(Type pageType) => serviceProvider.GetService(pageType);
}

public partial class App : Application
{
    private const string RelaunchedUnelevatedArgument = "--relaunched-unelevated";
    private const string AllowElevatedArgument = "--allow-elevated";
    private DispatcherTimer? _uiResponsivenessTimer;
    private Stopwatch? _uiResponsivenessStopwatch;
    private static readonly IHost _host = Host.CreateDefaultBuilder()
        .ConfigureServices((context, services) =>
        {
            // Navegação v4
            services.AddSingleton<INavigationViewPageProvider, CustomNavigationViewPageProvider>();
            services.AddSingleton<INavigationService, NavigationService>();

            // Serviços Core
            services.AddSingleton<IconService>();
            services.AddSingleton<StoreService>();
            services.AddSingleton<WingetExecutor>();
            services.AddSingleton<PackageMetricsService>();
            services.AddSingleton<CacheService>();
            services.AddSingleton<WingetBootstrapper>();
            services.AddSingleton<PackageCollectionService>();
            services.AddSingleton<ProfileService>();
            services.AddSingleton<OperationsQueueService>();
            services.AddSingleton<InstalledAppsService>();

            // Adições para Atualizações ->
            services.AddSingleton<ScheduledUpdatesService>();
            services.AddSingleton<IgnoredUpdatesService>();
            services.AddSingleton<ScheduledTempCleanerService>();

            services.AddSingleton<OfficeDeploymentToolService>();
            services.AddSingleton<OfficeUninstallService>();
            services.AddSingleton<OfficeInstalledProductsDetector>();
            services.AddSingleton<WinGetService>();
            services.AddSingleton<InstalledPackagesService>();
            services.AddSingleton<InstalledPackageClassifier>();
            services.AddSingleton<AutoInstallCliService>();
            services.AddSingleton<ProvisioningService>();
            services.AddSingleton<WindowsUpdateService>();
            services.AddSingleton<RestorePointService>();
            services.AddSingleton<CliPresetsService>();
            services.AddSingleton<AppDetailsOverlayService>();
            services.AddSingleton<AppDetailsOverlay>();

            // Backup local + nuvem (GitHub Gist secreto)
            services.AddSingleton<LocalBackupService>();
            services.AddSingleton<GitHubBackupService>();
            services.AddSingleton<CloudBackupService>();
            services.AddSingleton<BackupAutoSyncService>();

            // UI
            services.AddSingleton<MainWindow>();
            services.AddTransient<AutoWindow>();

            services.AddSingleton<HomePage>();
            services.AddTransient<PackagesPage>();
            services.AddSingleton<InstalledPackagesPage>();
            services.AddSingleton<OfficePage>();
            services.AddSingleton<UpdatesPage>();
            services.AddSingleton<AccountSyncPage>();
            services.AddSingleton<SettingsPage>();
            services.AddSingleton<AboutPage>();
            services.AddSingleton<ProvisioningPage>();
        })
        .Build();

    public static IServiceProvider Services => _host.Services;

    private async void OnStartup(object sender, StartupEventArgs e)
    {
        var executablePath = Environment.ProcessPath ?? AppContext.BaseDirectory;
        bool isAuto = HasAutoFlag(e.Args);
        bool isElevated = WinGetDiagnosticLog.IsElevated();
        var buildDate = File.Exists(executablePath)
            ? File.GetLastWriteTime(executablePath)
            : DateTime.MinValue;
        WinGetDiagnosticLog.Write(
            $"STARTUP elevated={isElevated} " +
            $"exe=\"{executablePath}\" buildDate={buildDate:O} " +
            $"auto={isAuto} debugger={Debugger.IsAttached} " +
            $"relaunched={HasArgument(e.Args, RelaunchedUnelevatedArgument)}");
        WingetCliAudit.Sink = WinGetDiagnosticLog.Write;
        WinGetFactoryHelper.ConfigureMode(e.Args);

        if (isElevated &&
            !isAuto &&
            !HasArgument(e.Args, RelaunchedUnelevatedArgument) &&
            !HasArgument(e.Args, AllowElevatedArgument) &&
            !Debugger.IsAttached)
        {
            if (TryRelaunchUnelevated(executablePath, e.Args))
            {
                WinGetDiagnosticLog.Write(
                    "STARTUP decision=relaunch-unelevated result=started; exiting elevated instance");
                Shutdown();
                return;
            }

            WinGetDiagnosticLog.Write(
                "STARTUP decision=relaunch-unelevated result=failed; continuing elevated");
        }
        else if (isAuto)
        {
            WinGetDiagnosticLog.Write("STARTUP decision=auto-exception");
        }
        else if (HasArgument(e.Args, RelaunchedUnelevatedArgument))
        {
            if (isElevated)
            {
                WinGetDiagnosticLog.Write(
                    "STARTUP decision=loop-protection; relaunched process is still elevated");
            }
            else
            {
                WinGetDiagnosticLog.Write("STARTUP decision= relaunched-unelevated accepted");
            }
        }
        else if (HasArgument(e.Args, AllowElevatedArgument))
        {
            WinGetDiagnosticLog.Write("STARTUP decision=allow-elevated-exception");
        }
        else if (Debugger.IsAttached)
        {
            WinGetDiagnosticLog.Write("STARTUP decision=debugger-exception");
        }
        else
        {
            WinGetDiagnosticLog.Write("STARTUP decision=interactive-user-context");
        }

        ApplicationThemeManager.Changed += ApplicationThemeManager_Changed;
        ApplyThemePalette(ApplicationThemeManager.GetAppTheme());

        if (isAuto)
        {
            ShutdownMode = ShutdownMode.OnExplicitShutdown;
            NativeConsole.AttachToParentIfAvailable();

            try
            {
                await _host.StartAsync();
                OperationRunner.ConfigureInstallHandler(
                    _host.Services.GetRequiredService<WinGetService>().InstallAsync);
                WinGetDiagnosticLog.Write(
                    "INSTALL HANDLER CONFIGURED startupPath=auto handler=WinGetService.InstallAsync");
                _ = WinGetFactoryHelper.ProbeAsync();
                _host.Services.GetRequiredService<BackupAutoSyncService>();

                string? autoInstallProfilePath = TryGetAutoInstallProfilePath(e.Args);
                if (autoInstallProfilePath is null)
                {
                    Console.WriteLine("[WinProvision] Uso: WinProvision.Store.exe /auto <caminho-ou-URL-do-perfil.json> [/silent] [/log <caminho.log>] [/cloudlog]");
                    NativeConsole.ReleaseParentPrompt();
                    Shutdown((int)AutoInstallExitCode.InvalidArguments);
                    return;
                }

                bool isSilent = HasSilentFlag(e.Args);

                string logPath = TryGetLogPath(e.Args) ?? DefaultLogPath(autoInstallProfilePath);
                using var logger = new CliFileLogger(logPath);
                logger.Log($"[WinProvision] /auto iniciado. Perfil: {autoInstallProfilePath}. Modo: {(isSilent ? "Silencioso" : "UI Visível")}");

                AutoWindow? autoWindow = null;

                if (!isSilent)
                {
                    try
                    {
                        autoWindow = _host.Services.GetRequiredService<AutoWindow>();
                        SystemThemeWatcher.Watch(autoWindow);
                        autoWindow.Show();
                        logger.Log("[WinProvision] AutoWindow carregada. Executando com UI.");
                    }
                    catch (Exception ex)
                    {
                        autoWindow = null;
                        logger.Log($"[WinProvision] AVISO: AutoWindow indisponível; continuando em modo CLI sem UI. Motivo: {ex.GetType().Name}: {ex.Message}");
                    }
                }
                else
                {
                    logger.Log("[WinProvision] Executando em modo silencioso (sem interface gráfica).");
                }

                AutoInstallExitCode exitCode;
                try
                {
                    // Quando /cloudlog está presente, gera um session ID curto, exibe o QR
                    // Code ASCII no console e envolve o delegate de log para replicar cada
                    // linha para o Worker Cloudflare em fire-and-forget (falha silenciosa).
                    bool useCloudLog = HasCloudLogFlag(e.Args);
                    Action<string> logDelegate = logger.Log;

                    string? cloudSessionId = null;
                    if (useCloudLog)
                    {
                        cloudSessionId = WinProvision.Core.Services.CloudLogSessionId.Generate();
                        string viewUrl = WinProvision.Core.Services.CloudLogSessionId.ViewUrl(cloudSessionId);

                        Console.WriteLine();
                        Console.WriteLine($"[WinProvision] Logs na Nuvem ativados — sessão: {cloudSessionId}");
                        Console.WriteLine($"[WinProvision] Acompanhe em: {viewUrl}");
                        Console.WriteLine();
                        Console.WriteLine(WinProvision.Core.Services.CloudLogSessionId.QrAscii(viewUrl));

                        logDelegate = msg =>
                        {
                            logger.Log(msg);
                            int pct = autoWindow?.CurrentProgress ?? 0;
                            WinProvision.Core.Services.CloudLogService.Send(cloudSessionId, msg, pct);
                        };
                    }

                    var cliService = _host.Services.GetRequiredService<AutoInstallCliService>();
                    exitCode = autoWindow is not null
                        ? await autoWindow.RunAsync(autoInstallProfilePath, logDelegate)
                        : await cliService.RunAsync(autoInstallProfilePath, logDelegate);

                    if (cloudSessionId is not null)
                    {
                        WinProvision.Core.Services.CloudLogService.Send(
                            cloudSessionId,
                            $"[WinProvision] Provisionamento concluído com código {(int)exitCode} ({exitCode}).",
                            100);
                    }
                }
                catch (OperationCanceledException)
                {
                    logger.Log("[WinProvision] Execução cancelada.");
                    exitCode = AutoInstallExitCode.UnexpectedError;
                }
                catch (Exception ex)
                {
                    logger.Log($"[WinProvision] ERRO fatal no modo /auto: {ex}");
                    exitCode = AutoInstallExitCode.UnexpectedError;
                }

                logger.Log($"[WinProvision] Código de saída: {(int)exitCode} ({exitCode}).");

                if (autoWindow is not null && autoWindow.IsVisible)
                    await autoWindow.WaitForCloseAsync();

                NativeConsole.ReleaseParentPrompt();
                NativeConsole.CloseParentConsole();
                Shutdown((int)exitCode);
                return;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[WinProvision] ERRO fatal ao iniciar /auto: {ex}");
                NativeConsole.ReleaseParentPrompt();
                Shutdown((int)AutoInstallExitCode.UnexpectedError);
                return;
            }
        }

        await _host.StartAsync();
        OperationRunner.ConfigureInstallHandler(
            _host.Services.GetRequiredService<WinGetService>().InstallAsync);
        WinGetDiagnosticLog.Write(
            "INSTALL HANDLER CONFIGURED startupPath=interactive handler=WinGetService.InstallAsync");
        _ = WinGetFactoryHelper.ProbeAsync();
        _host.Services.GetRequiredService<BackupAutoSyncService>();

        var mainWindow = _host.Services.GetRequiredService<MainWindow>();
        SystemThemeWatcher.Watch(mainWindow);
        mainWindow.Show();
        StartUiResponsivenessMonitor();
    }

    private static void ApplicationThemeManager_Changed(ApplicationTheme theme, Color systemAccent)
    {
        ApplyThemePalette(theme);
    }

    private static void ApplyThemePalette(ApplicationTheme theme)
    {
        bool isLight = theme == ApplicationTheme.Light;

        SetBrushColor("ApplicationBackgroundBrush", isLight ? "#FFF7F9FC" : "#F0101B2D");
        SetBrushColor("LayerFillColorDefaultBrush", isLight ? "#FFF0F4F8" : "#E8172A40");
        SetBrushColor("ControlFillColorDefaultBrush", isLight ? "#FFFFFFFF" : "#E0263446");
        SetBrushColor("ControlFillColorSecondaryBrush", isLight ? "#FFF3F6FA" : "#F02D4056");
        SetBrushColor("ControlFillColorTertiaryBrush", isLight ? "#FFE9EEF5" : "#E01D3047");
        SetBrushColor("CardBackgroundFillColorDefaultBrush", isLight ? "#FFFFFFFF" : "#E0263446");
        SetBrushColor("CardBackgroundFillColorSecondaryBrush", isLight ? "#FFF3F6FA" : "#E02B3B50");
        SetBrushColor("ControlStrokeColorDefaultBrush", isLight ? "#FFCBD5E1" : "#8050657D");
        SetBrushColor("ControlStrokeColorSecondaryBrush", isLight ? "#FFB8C5D4" : "#70475C75");
        SetBrushColor("SurfaceFillColorDefaultBrush", isLight ? "#FFF0F4F8" : "#E01D3047");
        SetBrushColor("SurfaceFillColorSecondaryBrush", isLight ? "#FFF3F6FA" : "#E02B3B50");
        SetBrushColor("SubtleFillColorSecondaryBrush", isLight ? "#FFF3F6FA" : "#E02B3B50");
        SetBrushColor("SubtleFillColorTertiaryBrush", isLight ? "#FFE9EEF5" : "#E01D3047");
        SetBrushColor("DividerStrokeColorDefaultBrush", isLight ? "#FFCBD5E1" : "#8050657D");
        SetBrushColor("RegionFillColorDefaultBrush", isLight ? "#FFF0F4F8" : "#E8172A40");
        SetBrushColor("RegionFillColorSecondaryBrush", isLight ? "#FFFFFFFF" : "#E0263446");

        SetBrushColor("AppSurfaceBrush", isLight ? "#FFF0F4F8" : "#E01D3047");
        SetBrushColor("AppSurfaceElevatedBrush", isLight ? "#FFFFFFFF" : "#E0263446");
        SetBrushColor("AppSurfaceStrongBrush", isLight ? "#FFF3F6FA" : "#F02D4056");
        SetBrushColor("AppBorderBrush", isLight ? "#FFCBD5E1" : "#8050657D");
        SetBrushColor("AppBorderSubtleBrush", isLight ? "#FFDCE4EC" : "#5050657D");
        SetBrushColor("AppTextPrimaryBrush", isLight ? "#FF182230" : "#FFF5F7FA");
        SetBrushColor("AppTextSecondaryBrush", isLight ? "#FF526274" : "#FFC6D1E0");
        SetBrushColor("AppTextTertiaryBrush", isLight ? "#FF718096" : "#FF95A5B9");
        SetBrushColor("AppIconBrush", isLight ? "#FF334155" : "#FFE0E9F5");
        SetBrushColor("AppIconMutedBrush", isLight ? "#FF64748B" : "#FF9BAAC0");
        SetBrushColor("AppCardBackgroundBrush", isLight ? "#FFFFFFFF" : "#E0263446");
        SetBrushColor("AppCardBorderBrush", isLight ? "#FFCBD5E1" : "#8050657D");
    }

    private static void SetBrushColor(string key, string hex)
    {
        Current.Resources[key] = new SolidColorBrush(
            (Color)ColorConverter.ConvertFromString(hex));
    }

    private void StartUiResponsivenessMonitor()
    {
        _uiResponsivenessStopwatch = Stopwatch.StartNew();
        var previousTick = _uiResponsivenessStopwatch.Elapsed;
        _uiResponsivenessTimer = new DispatcherTimer(DispatcherPriority.ApplicationIdle)
        {
            Interval = TimeSpan.FromSeconds(1)
        };
        _uiResponsivenessTimer.Tick += (_, _) =>
        {
            var now = _uiResponsivenessStopwatch.Elapsed;
            var delay = now - previousTick;
            previousTick = now;
            if (delay > TimeSpan.FromSeconds(2))
            {
                WinGetDiagnosticLog.Write(
                    $"UI THREAD DELAY delay={delay} sinceLastTick={now}");
            }
        };
        _uiResponsivenessTimer.Start();
        WinGetDiagnosticLog.Write("UI THREAD MONITOR started interval=1s threshold=2s");
    }

    private static bool HasAutoFlag(string[] args) =>
        args.Any(a => string.Equals(a, "/auto", StringComparison.OrdinalIgnoreCase));

    private static bool HasArgument(string[] args, string argument) =>
        args.Any(a => string.Equals(a, argument, StringComparison.OrdinalIgnoreCase));

    private static bool TryRelaunchUnelevated(string executablePath, string[] originalArguments)
    {
        IntPtr shellWindow = GetShellWindow();
        if (shellWindow == IntPtr.Zero)
        {
            WinGetDiagnosticLog.Write(
                "STARTUP relaunch failed stage=get-shell-window win32=0");
            return false;
        }

        GetWindowThreadProcessId(shellWindow, out uint shellProcessId);
        if (shellProcessId == 0)
        {
            WinGetDiagnosticLog.Write(
                "STARTUP relaunch failed stage=get-shell-process win32=0");
            return false;
        }

        IntPtr shellProcess = OpenProcess(
            ProcessQueryLimitedInformation,
            false,
            shellProcessId);
        IntPtr shellToken = IntPtr.Zero;
        IntPtr primaryToken = IntPtr.Zero;
        IntPtr environment = IntPtr.Zero;
        PROCESS_INFORMATION processInfo = default;

        try
        {
            if (shellProcess == IntPtr.Zero ||
                !OpenProcessToken(
                    shellProcess,
                    TokenDuplicate | TokenAssignPrimary | TokenQuery,
                    out shellToken))
            {
                WinGetDiagnosticLog.Write(
                    $"STARTUP relaunch failed stage=open-shell-token win32={Marshal.GetLastWin32Error()}");
                return false;
            }

            if (!DuplicateTokenEx(
                shellToken,
                TokenAllAccess,
                IntPtr.Zero,
                SecurityImpersonation,
                TokenPrimary,
                out primaryToken))
            {
                WinGetDiagnosticLog.Write(
                    $"STARTUP relaunch failed stage=duplicate-token win32={Marshal.GetLastWin32Error()}");
                return false;
            }

            if (!CreateEnvironmentBlock(out environment, primaryToken, false))
            {
                WinGetDiagnosticLog.Write(
                    $"STARTUP relaunch failed stage=create-environment win32={Marshal.GetLastWin32Error()}");
                return false;
            }

            string commandLine = BuildCommandLine(
                executablePath,
                originalArguments.Append(RelaunchedUnelevatedArgument));
            var mutableCommandLine = new StringBuilder(commandLine);
            var startupInfo = new STARTUPINFO
            {
                cb = Marshal.SizeOf<STARTUPINFO>(),
                lpDesktop = @"winsta0\default",
                dwFlags = StartfUseShowWindow,
                wShowWindow = 1
            };

            if (!CreateProcessWithTokenW(
                primaryToken,
                LogonWithProfile,
                executablePath,
                mutableCommandLine,
                CreateUnicodeEnvironment,
                environment,
                Path.GetDirectoryName(executablePath),
                ref startupInfo,
                out processInfo))
            {
                WinGetDiagnosticLog.Write(
                    $"STARTUP relaunch failed stage=create-process win32={Marshal.GetLastWin32Error()}");
                return false;
            }

            WinGetDiagnosticLog.Write(
                $"STARTUP relaunch process-started pid={processInfo.dwProcessId} " +
                $"shellPid={shellProcessId} args={commandLine}");
            return true;
        }
        finally
        {
            if (processInfo.hThread != IntPtr.Zero)
                CloseHandle(processInfo.hThread);
            if (processInfo.hProcess != IntPtr.Zero)
                CloseHandle(processInfo.hProcess);
            if (environment != IntPtr.Zero)
                DestroyEnvironmentBlock(environment);
            if (primaryToken != IntPtr.Zero)
                CloseHandle(primaryToken);
            if (shellToken != IntPtr.Zero)
                CloseHandle(shellToken);
            if (shellProcess != IntPtr.Zero)
                CloseHandle(shellProcess);
        }
    }

    private static string BuildCommandLine(string executablePath, IEnumerable<string> arguments) =>
        string.Join(
            " ",
            new[] { QuoteCommandLineArgument(executablePath) }
                .Concat(arguments.Select(QuoteCommandLineArgument)));

    private static string QuoteCommandLineArgument(string value)
    {
        if (value.Length > 0 &&
            value.All(c => !char.IsWhiteSpace(c) && c != '"'))
        {
            return value;
        }

        var builder = new StringBuilder(value.Length + 2);
        builder.Append('"');
        int backslashes = 0;
        foreach (char character in value)
        {
            if (character == '\\')
            {
                backslashes++;
                continue;
            }

            if (character == '"')
            {
                builder.Append('\\', backslashes * 2 + 1);
                builder.Append('"');
                backslashes = 0;
                continue;
            }

            builder.Append('\\', backslashes);
            builder.Append(character);
            backslashes = 0;
        }

        builder.Append('\\', backslashes * 2);
        builder.Append('"');
        return builder.ToString();
    }

    private const uint ProcessQueryLimitedInformation = 0x1000;
    private const uint TokenAssignPrimary = 0x0001;
    private const uint TokenDuplicate = 0x0002;
    private const uint TokenQuery = 0x0008;
    private const uint TokenAllAccess = 0x000F01FF;
    private const int SecurityImpersonation = 2;
    private const int TokenPrimary = 1;
    private const uint LogonWithProfile = 0x00000001;
    private const uint CreateUnicodeEnvironment = 0x00000400;
    private const uint StartfUseShowWindow = 0x00000001;

    [DllImport("user32.dll")]
    private static extern IntPtr GetShellWindow();

    [DllImport("user32.dll", SetLastError = true)]
    private static extern uint GetWindowThreadProcessId(
        IntPtr window,
        out uint processId);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr OpenProcess(
        uint desiredAccess,
        bool inheritHandle,
        uint processId);

    [DllImport("advapi32.dll", SetLastError = true)]
    private static extern bool OpenProcessToken(
        IntPtr processHandle,
        uint desiredAccess,
        out IntPtr tokenHandle);

    [DllImport("advapi32.dll", SetLastError = true)]
    private static extern bool DuplicateTokenEx(
        IntPtr existingToken,
        uint desiredAccess,
        IntPtr tokenAttributes,
        int impersonationLevel,
        int tokenType,
        out IntPtr primaryToken);

    [DllImport("userenv.dll", SetLastError = true)]
    private static extern bool CreateEnvironmentBlock(
        out IntPtr environment,
        IntPtr token,
        bool inherit);

    [DllImport("userenv.dll", SetLastError = true)]
    private static extern bool DestroyEnvironmentBlock(IntPtr environment);

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool CreateProcessWithTokenW(
        IntPtr token,
        uint logonFlags,
        string? applicationName,
        StringBuilder commandLine,
        uint creationFlags,
        IntPtr environment,
        string? currentDirectory,
        ref STARTUPINFO startupInfo,
        out PROCESS_INFORMATION processInformation);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool CloseHandle(IntPtr handle);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct STARTUPINFO
    {
        public int cb;
        public string? lpReserved;
        public string? lpDesktop;
        public string? lpTitle;
        public int dwX;
        public int dwY;
        public int dwXSize;
        public int dwYSize;
        public int dwXCountChars;
        public int dwYCountChars;
        public int dwFillAttribute;
        public uint dwFlags;
        public short wShowWindow;
        public short cbReserved2;
        public IntPtr lpReserved2;
        public IntPtr hStdInput;
        public IntPtr hStdOutput;
        public IntPtr hStdError;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct PROCESS_INFORMATION
    {
        public IntPtr hProcess;
        public IntPtr hThread;
        public uint dwProcessId;
        public uint dwThreadId;
    }

    private static bool HasSilentFlag(string[] args) =>
        args.Any(a => string.Equals(a, "/silent", StringComparison.OrdinalIgnoreCase)
                   || string.Equals(a, "/quiet", StringComparison.OrdinalIgnoreCase)
                   || string.Equals(a, "-s", StringComparison.OrdinalIgnoreCase)
                   || string.Equals(a, "--silent", StringComparison.OrdinalIgnoreCase));

    private static string? TryGetAutoInstallProfilePath(string[] args)
    {
        for (int i = 0; i < args.Length; i++)
        {
            if (string.Equals(args[i], "/auto", StringComparison.OrdinalIgnoreCase) && i + 1 < args.Length)
            {
                return args[i + 1];
            }
        }

        return null;
    }

    private static string? TryGetLogPath(string[] args)
    {
        for (int i = 0; i < args.Length; i++)
        {
            if (string.Equals(args[i], "/log", StringComparison.OrdinalIgnoreCase) && i + 1 < args.Length)
            {
                return args[i + 1];
            }
        }

        return null;
    }

    private static bool HasCloudLogFlag(string[] args) =>
        args.Any(a => string.Equals(a, "/cloudlog", StringComparison.OrdinalIgnoreCase)
                   || string.Equals(a, "--cloudlog", StringComparison.OrdinalIgnoreCase));

    private static string DefaultLogPath(string profilePath, string prefix = "auto")
    {
        string baseDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "WinProvision", "Logs");

        string profileLabel = Path.GetFileNameWithoutExtension(profilePath);
        string fileName = $"{prefix}-{profileLabel}-{DateTime.Now:yyyyMMdd-HHmmss}.log";

        return Path.Combine(baseDir, fileName);
    }

    private async void OnExit(object sender, ExitEventArgs e)
    {
        _uiResponsivenessTimer?.Stop();
        await _host.StopAsync();
        _host.Dispose();
    }
}
