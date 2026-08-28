using System;
using System.Windows;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Wpf.Ui;
using Wpf.Ui.Abstractions;
using Wpf.Ui.Appearance;
using WinProvision.Core.Services;
using WinProvision.Core.Services.Profile;

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
            services.AddSingleton<PackageCollectionService>();
            services.AddSingleton<ProfileService>(); // <-- REGISTRO QUE FALTAVA
            services.AddSingleton<OperationsQueueService>(); // Fila global do painel estilo UnigetUI
            services.AddSingleton<InstalledAppsService>(); // Cache compartilhado de apps já instalados (winget export)
            services.AddSingleton<AppLaunchService>(); // Resolve/abre o executável de um app já instalado

            // UI
            services.AddSingleton<MainWindow>();
            services.AddTransient<HomePage>();
            services.AddTransient<PackagesPage>();
        })
        .Build();

    public static IServiceProvider Services => _host.Services;

    private async void OnStartup(object sender, StartupEventArgs e)
    {
        await _host.StartAsync();

        var mainWindow = _host.Services.GetRequiredService<MainWindow>();

        // Aplica o tema (claro/escuro) de acordo com o tema atual do Windows e continua
        // observando: se o usuário trocar o tema do sistema com o app aberto, a janela
        // acompanha automaticamente (também atualiza o efeito de fundo Mica).
        SystemThemeWatcher.Watch(mainWindow);

        mainWindow.Show();
    }

    private async void OnExit(object sender, ExitEventArgs e)
    {
        await _host.StopAsync();
        _host.Dispose();
    }
}