using System;
using System.Diagnostics;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using Microsoft.Web.WebView2.Core;

namespace WinProvision.Store;

public partial class AboutPage : Page
{
    private const string GitHubPagesUrl = "https://gabrielsilvati.github.io/WinProvision-Store/";
    private bool _initializationInProgress;
    private bool _navigationStarted;
    private bool _coreEventsAttached;

    public AboutPage()
    {
        InitializeComponent();
        Loaded += AboutPage_Loaded;
    }

    private async void AboutPage_Loaded(object sender, RoutedEventArgs e)
    {
        await InitializeBrowserAsync(retry: false);
    }

    private async Task InitializeBrowserAsync(bool retry)
    {
        if (_initializationInProgress || (!retry && _navigationStarted))
            return;

        _initializationInProgress = true;
        BrowserStatusPanel.Visibility = Visibility.Visible;
        BrowserStatusText.Text = retry
            ? "Tentando carregar o GitHub Pages…"
            : "Conectando ao GitHub Pages…";

        try
        {
            if (PagesBrowser.CoreWebView2 is null)
                await PagesBrowser.EnsureCoreWebView2Async();

            CoreWebView2 core = PagesBrowser.CoreWebView2
                ?? throw new InvalidOperationException("O WebView2 não disponibilizou o navegador interno.");

            if (!_coreEventsAttached)
            {
                core.NavigationCompleted += CoreWebView2_NavigationCompleted;
                _coreEventsAttached = true;
            }

            if (retry && _navigationStarted)
                core.Reload();
            else
            {
                _navigationStarted = true;
                core.Navigate(GitHubPagesUrl);
            }
        }
        catch (Exception ex)
        {
            _navigationStarted = false;
            Debug.WriteLine($"[AboutPage] Falha ao inicializar/navegar no WebView2: {ex}");
            ShowBrowserError("Não foi possível iniciar o navegador interno. Verifique o Microsoft Edge WebView2 Runtime e tente novamente.");
        }
        finally
        {
            _initializationInProgress = false;
        }
    }

    private void CoreWebView2_NavigationCompleted(object? sender, CoreWebView2NavigationCompletedEventArgs e)
    {
        if (!e.IsSuccess)
        {
            ShowBrowserError($"Não foi possível carregar o GitHub Pages ({e.WebErrorStatus}). Verifique sua conexão e tente novamente.");
            return;
        }

        BrowserStatusPanel.Visibility = Visibility.Collapsed;
    }

    private async void RetryButton_Click(object sender, RoutedEventArgs e)
        => await InitializeBrowserAsync(retry: true);

    private void ShowBrowserError(string message)
    {
        BrowserStatusText.Text = message;
        BrowserStatusPanel.Visibility = Visibility.Visible;
    }
}
