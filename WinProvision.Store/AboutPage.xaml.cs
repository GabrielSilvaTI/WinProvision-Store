using System;
using System.Windows;
using System.Windows.Controls;
using Microsoft.Web.WebView2.Core;

namespace WinProvision.Store;

public partial class AboutPage : Page
{
    private const string GitHubPagesUrl = "https://gabrielsilvati.github.io/WinProvision-Store/";

    public AboutPage()
    {
        InitializeComponent();
        Loaded += AboutPage_Loaded;
    }

    private async void AboutPage_Loaded(object sender, RoutedEventArgs e)
    {
        try
        {
            await PagesBrowser.EnsureCoreWebView2Async();
            PagesBrowser.CoreWebView2.NavigationCompleted += CoreWebView2_NavigationCompleted;
            PagesBrowser.Source = new Uri(GitHubPagesUrl);
        }
        catch
        {
            ShowBrowserError("Não foi possível abrir esta página. Verifique sua conexão.");
        }
    }

    private void PagesBrowser_NavigationCompleted(object? sender, CoreWebView2NavigationCompletedEventArgs e)
    {
        if (!e.IsSuccess)
        {
            ShowBrowserError("Não foi possível carregar a página. Verifique sua conexão e tente novamente.");
        }
    }

    private void CoreWebView2_NavigationCompleted(object? sender, CoreWebView2NavigationCompletedEventArgs e)
    {
        if (!e.IsSuccess)
        {
            ShowBrowserError($"A página retornou o erro {e.WebErrorStatus}.");
        }
        else
        {
            BrowserStatusPanel.Visibility = Visibility.Collapsed;
        }
    }

    private void RetryButton_Click(object sender, RoutedEventArgs e)
    {
        BrowserStatusPanel.Visibility = Visibility.Collapsed;
        PagesBrowser.Reload();
    }

    private void ShowBrowserError(string message)
    {
        BrowserStatusText.Text = message;
        BrowserStatusPanel.Visibility = Visibility.Visible;
    }
}
