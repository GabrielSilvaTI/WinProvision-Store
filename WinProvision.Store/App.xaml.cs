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

            // Serviços Core (Adicionado ProfileService)
            services.AddSingleton<StoreService>();
            services.AddSingleton<WingetExecutor>();
            services.AddSingleton<WingetBootstrapper>(); // Garante o winget disponível antes do /auto usá-lo (ver AutoInstallCliService)
            services.AddSingleton<PackageCollectionService>();
            services.AddSingleton<ProfileService>(); // <-- REGISTRO QUE FALTAVA
            services.AddSingleton<OperationsQueueService>(); // Fila global do painel estilo UnigetUI
            services.AddSingleton<InstalledAppsService>(); // Cache compartilhado de apps já instalados (winget export)
            services.AddSingleton<AppLaunchService>(); // Resolve/abre o executável de um app já instalado
            services.AddSingleton<OfficeDeploymentToolService>(); // Pipeline ODT: winget install + setup.exe /configure
            services.AddSingleton<OfficeInstalledProductsDetector>(); // Leitura (somente leitura) do registro Click-to-Run
            services.AddSingleton<AutoInstallCliService>(); // Modo CLI: "WinProvision.Store.exe /auto perfil.json"
            services.AddSingleton<ProvisioningService>(); // Aplica/importa/exporta ajustes de sistema (tema, barra de tarefas, energia, nome da máquina, atualizações, ponto de restauração)
            services.AddSingleton<WindowsUpdateService>(); // Busca/instala atualizações do Windows (WUAPI) — usado pelo ProvisioningService.ApplyAsync na máquina-alvo
            services.AddSingleton<RestorePointService>(); // Cria ponto de restauração do sistema — usado pelo ProvisioningService.ApplyAsync na máquina-alvo
            services.AddSingleton<ProvisionCliService>(); // Modo CLI: "WinProvision.Store.exe /Provision perfil.json"

            // Backup local + nuvem (login com GitHub via PAT é opcional — ver SettingsPage)
            services.AddSingleton<LocalBackupService>();
            services.AddSingleton<GitHubBackupService>();
            services.AddSingleton<BackupAutoSyncService>(); // resolvido ansiosamente no OnStartup abaixo, pra assinar Changed cedo

            // UI
            services.AddSingleton<MainWindow>();
            // HomePage e OfficePage viram Singleton de propósito: a MESMA instância (com
            // busca/filtro/seleções já feitas) é reaproveitada a cada navegação, em vez de
            // reconstruída do zero — é isso que faz o app "lembrar" o que estava na tela
            // quando você sai e volta (pesquisa feita, plano/apps escolhidos no Office...).
            // PackagesPage continua Transient: as guias/itens já vivem no PackageCollectionService
            // (singleton), então o estado já sobrevive à navegação sem precisar do mesmo truque aqui.
            services.AddSingleton<HomePage>();
            services.AddTransient<PackagesPage>();
            services.AddSingleton<OfficePage>();
            // Singleton pelo mesmo motivo de HomePage/OfficePage: a lista já verificada
            // e as seleções feitas na tela Atualizações sobrevivem à navegação.
            services.AddSingleton<UpdatesPage>();
            services.AddSingleton<SettingsPage>();
            services.AddSingleton<ProvisioningPage>();
        })
        .Build();

    public static IServiceProvider Services => _host.Services;

    private async void OnStartup(object sender, StartupEventArgs e)
    {
        await _host.StartAsync();

        // Resolve ansiosamente para o construtor assinar InstalledAppsService.Changed
        // desde já — sem isso, o backup automático só começaria a reagir depois que
        // alguma tela (ex.: SettingsPage) fosse aberta pela primeira vez, deixando
        // instalações/remoções anteriores fora do backup. Vale tanto pro modo janela
        // quanto pro modo CLI (/auto) logo abaixo.
        _host.Services.GetRequiredService<BackupAutoSyncService>();

        // Modo CLI: "WinProvision.Store.exe /auto caminho\para\perfil.json" (ou uma URL
        // http(s) direta, ex.: link "raw" de Gist) instala tudo que o perfil descreve — apps
        // winget, planos de Office e, se o perfil também trouxer uma seção de
        // provisionamento, os ajustes de sistema (tema, barra de tarefas, energia, nome da
        // máquina, wallpaper) — sem nenhum prompt/janela, pensado pra ser chamado de dentro
        // de scripts de provisionamento automatizado.
        // Sem isso, o app sempre abre a MainWindow normal — o modo CLI é a exceção,
        // detectada aqui antes de qualquer janela ser criada.
        if (HasAutoFlag(e.Args))
        {
            string? autoInstallProfilePath = TryGetAutoInstallProfilePath(e.Args);
            if (autoInstallProfilePath is null)
            {
                // "/auto" sem caminho depois: erro de uso explícito, não cai
                // silenciosamente pra janela normal (script chamador esperaria
                // instalação automática, não uma janela abrindo do nada).
                NativeConsole.AttachToParentIfAvailable();
                Console.WriteLine("[WinProvision] Uso: WinProvision.Store.exe /auto <caminho-ou-URL-do-perfil.json> [/log <caminho.log>]");
                NativeConsole.ReleaseParentPrompt();
                Shutdown((int)AutoInstallExitCode.InvalidArguments);
                return;
            }

            await RunAutoInstallAndExitAsync(autoInstallProfilePath, e.Args);
            return;
        }

        // Modo CLI: "WinProvision.Store.exe /Provision caminho\para\perfil.json" (ou uma
        // URL) aplica só a seção de provisionamento do mesmo perfil (tema, barra de
        // tarefas, energia, nome da máquina, wallpaper) — atalho útil quando não há
        // apps/Office no perfil e não vale a pena rodar o /auto inteiro. Mesmo padrão do
        // modo /auto acima, e o mesmo arquivo .json funciona nos dois.
        if (HasProvisionFlag(e.Args))
        {
            string? provisioningProfilePath = TryGetProvisioningProfilePath(e.Args);
            if (provisioningProfilePath is null)
            {
                NativeConsole.AttachToParentIfAvailable();
                Console.WriteLine("[WinProvision] Uso: WinProvision.Store.exe /Provision <caminho-ou-URL-do-perfil.json> [/log <caminho.log>]");
                NativeConsole.ReleaseParentPrompt();
                Shutdown((int)ProvisioningExitCode.InvalidArguments);
                return;
            }

            await RunProvisionAndExitAsync(provisioningProfilePath, e.Args);
            return;
        }

        var mainWindow = _host.Services.GetRequiredService<MainWindow>();

        // Aplica o tema (claro/escuro) de acordo com o tema atual do Windows e continua
        // observando: se o usuário trocar o tema do sistema com o app aberto, a janela
        // acompanha automaticamente (também atualiza o efeito de fundo Mica).
        SystemThemeWatcher.Watch(mainWindow);

        mainWindow.Show();
    }

    /// <summary>Só checa se "/auto" foi passado, independente de ter um caminho válido depois.</summary>
    private static bool HasAutoFlag(string[] args) =>
        args.Any(a => string.Equals(a, "/auto", StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// Procura "/auto &lt;caminho&gt;" nos argumentos de linha de comando (case-insensitive).
    /// Retorna o caminho do perfil (.json) se encontrado, ou null se "/auto" não foi
    /// passado com um caminho logo depois.
    /// </summary>
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

    /// <summary>
    /// Roda a instalação automática e encerra o processo com um código de saída detalhado
    /// (ver <see cref="AutoInstallExitCode"/>), pra scripts de provisionamento — ou o
    /// WinProvision principal, via Process.Start — poderem checar o resultado com precisão
    /// (ex.: "if %errorlevel% neq 0 ..."). Shutdown() dispara o evento Exit normalmente,
    /// então OnExit já cuida de parar/descartar o host — não duplica isso aqui.
    /// </summary>
    private async Task RunAutoInstallAndExitAsync(string profilePath, string[] args)
    {
        NativeConsole.AttachToParentIfAvailable();

        string logPath = TryGetLogPath(args) ?? DefaultLogPath(profilePath);
        using var logger = new CliFileLogger(logPath);

        if (logger.FilePath is not null)
        {
            logger.Log($"[WinProvision] Log desta execução: {logger.FilePath}");
        }

        var cliService = _host.Services.GetRequiredService<AutoInstallCliService>();
        AutoInstallExitCode exitCode = await cliService.RunAsync(profilePath, logger.Log);

        logger.Log($"[WinProvision] Código de saída: {(int)exitCode} ({exitCode}).");

        NativeConsole.ReleaseParentPrompt();
        Shutdown((int)exitCode);
    }

    /// <summary>Só checa se "/Provision" foi passado, independente de ter um caminho válido depois.</summary>
    private static bool HasProvisionFlag(string[] args) =>
        args.Any(a => string.Equals(a, "/Provision", StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// Procura "/Provision &lt;caminho&gt;" nos argumentos de linha de comando (case-insensitive).
    /// Retorna o caminho do perfil de provisionamento (.json) se encontrado, ou null se
    /// "/Provision" não foi passado com um caminho logo depois.
    /// </summary>
    private static string? TryGetProvisioningProfilePath(string[] args)
    {
        for (int i = 0; i < args.Length; i++)
        {
            if (string.Equals(args[i], "/Provision", StringComparison.OrdinalIgnoreCase) && i + 1 < args.Length)
            {
                return args[i + 1];
            }
        }

        return null;
    }

    /// <summary>
    /// Roda a aplicação do perfil de provisionamento e encerra o processo com um código de
    /// saída detalhado (ver <see cref="ProvisioningExitCode"/>) — mesmo padrão de
    /// <see cref="RunAutoInstallAndExitAsync"/>, adaptado pro serviço de provisionamento.
    /// </summary>
    private async Task RunProvisionAndExitAsync(string profilePath, string[] args)
    {
        NativeConsole.AttachToParentIfAvailable();

        string logPath = TryGetLogPath(args) ?? DefaultLogPath(profilePath, "provision");
        using var logger = new CliFileLogger(logPath);

        if (logger.FilePath is not null)
        {
            logger.Log($"[WinProvision] Log desta execução: {logger.FilePath}");
        }

        var cliService = _host.Services.GetRequiredService<ProvisionCliService>();
        ProvisioningExitCode exitCode = await cliService.RunAsync(profilePath, logger.Log);

        logger.Log($"[WinProvision] Código de saída: {(int)exitCode} ({exitCode}).");

        NativeConsole.ReleaseParentPrompt();
        Shutdown((int)exitCode);
    }

    /// <summary>Procura "/log &lt;caminho&gt;" nos argumentos (opcional — sobrescreve o caminho padrão).</summary>
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

    /// <summary>
    /// Caminho padrão do log quando "/log" não é informado: um arquivo por execução em
    /// %LocalAppData%\WinProvision\Logs, nomeado com o perfil + timestamp — não precisa
    /// de privilégio de admin e não colide entre execuções.
    /// </summary>
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