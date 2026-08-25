using System;
using System.Windows;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Wpf.Ui;
using Wpf.Ui.Abstractions;
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

            // UI
            services.AddSingleton<MainWindow>();
            services.AddTransient<StorePage>();
            services.AddTransient<PackagesPage>();
        })
        .Build();

    public static IServiceProvider Services => _host.Services;

    private async void OnStartup(object sender, StartupEventArgs e)
    {
        await _host.StartAsync();

        var mainWindow = _host.Services.GetRequiredService<MainWindow>();
        mainWindow.Show();
    }

    private async void OnExit(object sender, ExitEventArgs e)
    {
        await _host.StopAsync();
        _host.Dispose();
    }
}