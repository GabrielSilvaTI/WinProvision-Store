using System;
using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using Microsoft.Web.WebView2.Core;

namespace WinProvision.Store;

public partial class UnattendCanvasPage : Page
{
    private const string GeneratorUrl = "https://schneegans.de/windows/unattend-generator/";

    public UnattendCanvasPage()
    {
        InitializeComponent();
        Loaded += UnattendCanvasPage_Loaded;
    }

    private async void UnattendCanvasPage_Loaded(object sender, RoutedEventArgs e)
    {
        Loaded -= UnattendCanvasPage_Loaded;
        try
        {
            var userDataFolder = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "WinProvisionStore",
                "WebView2Canvas");
            var options = new CoreWebView2EnvironmentOptions
            {
                AdditionalBrowserArguments = "--enable-features=msEdgeTranslate,msEdgeTranslateUi"
            };
            var environment = await CoreWebView2Environment.CreateAsync(
                browserExecutableFolder: null,
                userDataFolder,
                options);
            await GeneratorBrowser.EnsureCoreWebView2Async(environment);
            GeneratorBrowser.CoreWebView2.NavigationCompleted += GeneratorBrowser_NavigationCompleted;
            GeneratorBrowser.Source = new Uri(GeneratorUrl);
        }
        catch
        {
            ShowBrowserError("Não foi possível iniciar o Canvas. Verifique se o Microsoft Edge WebView2 Runtime está instalado e tente novamente.");
        }
    }

    private void GeneratorBrowser_NavigationCompleted(object? sender, CoreWebView2NavigationCompletedEventArgs e)
    {
        if (e.IsSuccess)
        {
            BrowserStatusPanel.Visibility = Visibility.Collapsed;
            return;
        }

        ShowBrowserError($"Não foi possível carregar o gerador Schneegans ({e.WebErrorStatus}). Verifique sua conexão e tente novamente.");
    }

    private void OpenInBrowserButton_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            Process.Start(new ProcessStartInfo(GeneratorUrl)
            {
                UseShellExecute = true
            });
        }
        catch
        {
            BrowserStatusText.Text = "Não foi possível abrir o navegador padrão. Copie o endereço do Canvas e abra-o no navegador.";
            RetryButton.Visibility = Visibility.Collapsed;
            BrowserStatusPanel.Visibility = Visibility.Visible;
        }
    }

    private void RetryButton_Click(object sender, RoutedEventArgs e)
    {
        BrowserStatusText.Text = "Carregando o gerador Schneegans…";
        RetryButton.Visibility = Visibility.Collapsed;
        BrowserStatusPanel.Visibility = Visibility.Visible;

        if (GeneratorBrowser.CoreWebView2 is null)
        {
            Loaded += UnattendCanvasPage_Loaded;
            return;
        }

        GeneratorBrowser.Source = new Uri(GeneratorUrl);
    }

    private void ShowBrowserError(string message)
    {
        BrowserStatusText.Text = message;
        RetryButton.Visibility = Visibility.Visible;
        BrowserStatusPanel.Visibility = Visibility.Visible;
    }
}
