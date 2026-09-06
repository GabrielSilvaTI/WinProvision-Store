using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
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
            services.AddSingleton<OfficeInstalledProductsDetector>();
            services.AddSingleton<AutoInstallCliService>();
            services.AddSingleton<ProvisioningService>();
            services.AddSingleton<WindowsUpdateService>();
            services.AddSingleton<RestorePointService>();
            services.AddSingleton<CliPresetsService>();
            services.AddSingleton<AppDetailsOverlayService>();
            services.AddSingleton<AppDetailsOverlay>();

            // Backup local + nuvem
            services.AddSingleton<LocalBackupService>();
            services.AddSingleton<GitHubBackupService>();
            services.AddSingleton<BackupAutoSyncService>();

            // UI
            services.AddSingleton<MainWindow>();
            services.AddTransient<AutoWindow>();

            services.AddSingleton<HomePage>();
            services.AddTransient<PackagesPage>();
            services.AddSingleton<OfficePage>();
            services.AddSingleton<UpdatesPage>();
            services.AddSingleton<SettingsPage>();
            services.AddSingleton<ProvisioningPage>();
        })
        .Build();

    public static IServiceProvider Services => _host.Services;

    private async void OnStartup(object sender, StartupEventArgs e)
    {
        bool isAuto = HasAutoFlag(e.Args);

        if (isAuto)
        {
            ShutdownMode = ShutdownMode.OnExplicitShutdown;
            NativeConsole.AttachToParentIfAvailable();

            try
            {
                await _host.StartAsync();
                _host.Services.GetRequiredService<BackupAutoSyncService>();

                string? autoInstallProfilePath = TryGetAutoInstallProfilePath(e.Args);
                if (autoInstallProfilePath is null)
                {
                    Console.WriteLine("[WinProvision] Uso: WinProvision.Store.exe /auto <caminho-ou-URL-do-perfil.json> [/silent] [/log <caminho.log>] [/webhook <url>]");
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
                    var cliService = _host.Services.GetRequiredService<AutoInstallCliService>();
                    exitCode = autoWindow is not null
                        ? await autoWindow.RunAsync(autoInstallProfilePath, logger.Log)
                        : await cliService.RunAsync(autoInstallProfilePath, logger.Log);
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

                string? webhookUrl = TryGetWebhookUrl(e.Args);
                if (webhookUrl is not null)
                {
                    string title = Path.GetFileNameWithoutExtension(autoInstallProfilePath);
                    await WebhookLogNotifier.SendAsync(webhookUrl, title, exitCode == AutoInstallExitCode.Success, logger.GetFullText(), logger.Log);
                }

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
        _host.Services.GetRequiredService<BackupAutoSyncService>();

        var mainWindow = _host.Services.GetRequiredService<MainWindow>();
        SystemThemeWatcher.Watch(mainWindow);
        mainWindow.Show();
    }

    private static bool HasAutoFlag(string[] args) =>
        args.Any(a => string.Equals(a, "/auto", StringComparison.OrdinalIgnoreCase));

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

    private static string? TryGetWebhookUrl(string[] args)
    {
        for (int i = 0; i < args.Length; i++)
        {
            if (string.Equals(args[i], "/webhook", StringComparison.OrdinalIgnoreCase) && i + 1 < args.Length)
            {
                return args[i + 1];
            }
        }

        return null;
    }

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
        await _host.StopAsync();
        _host.Dispose();
    }
}