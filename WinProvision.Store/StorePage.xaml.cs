using System;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;
using WinProvision.Core.Models;
using WinProvision.Core.Services;
using WinProvision.Core.Services.Profile;

namespace WinProvision.Store;

public partial class StorePage : Page
{
    private readonly StoreService _storeService;
    private readonly WingetExecutor _wingetExecutor;
    private readonly PackageCollectionService _collectionService;
    private readonly DispatcherTimer _debounceTimer;
    private bool _catalogLoaded;

    // Construtor com Injeção de Dependências direta
    public StorePage(
        StoreService storeService,
        WingetExecutor wingetExecutor,
        PackageCollectionService collectionService)
    {
        InitializeComponent();

        _storeService = storeService;
        _wingetExecutor = wingetExecutor;
        _collectionService = collectionService;

        // Debounce: espera 300ms sem digitação antes de buscar
        _debounceTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(300) };
        _debounceTimer.Tick += (_, _) =>
        {
            _debounceTimer.Stop();
            RunSearch(SearchTextBox.Text);
        };

        Loaded += StorePage_Loaded;
    }

    private async void StorePage_Loaded(object sender, RoutedEventArgs e)
    {
        if (_catalogLoaded)
        {
            return;
        }

        StatusText.Text = "Carregando catálogo...";
        await _storeService.LoadCatalogAsync();
        _catalogLoaded = true;

        RunSearch(SearchTextBox.Text);
    }

    private void SearchTextBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        _debounceTimer.Stop();
        _debounceTimer.Start();
    }

    private void SearchTextBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter)
        {
            return;
        }

        _debounceTimer.Stop();
        RunSearch(SearchTextBox.Text);
    }

    private void RunSearch(string? query)
    {
        if (!_catalogLoaded)
        {
            return;
        }

        if (string.IsNullOrWhiteSpace(query))
        {
            ResultsItemsControl.ItemsSource = null;
            StatusText.Text = "Pronto para pesquisar";
            return;
        }

        StatusText.Text = $"Buscando por \"{query}\"...";

        var results = _storeService.Search(query).Take(50).ToList();

        ResultsItemsControl.ItemsSource = results;
        StatusText.Text = results.Count == 0
            ? $"Nenhum resultado para \"{query}\""
            : $"{results.Count} resultado(s) para \"{query}\"";
    }

    private async void RefreshButton_Click(object sender, RoutedEventArgs e)
    {
        StatusText.Text = "Atualizando catálogo...";
        await _storeService.LoadCatalogAsync(forceRefresh: true);
        RunSearch(SearchTextBox.Text);
    }

    private async void InstallButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: AppEntry app } button)
        {
            return;
        }

        var originalContent = button.Content;
        button.IsEnabled = false;
        button.Content = "Instalando...";
        StatusText.Text = $"Instalando {app.Name}...";

        var success = false;

        try
        {
            var result = await _wingetExecutor.InstallAppAsync(app.Id, onLogReceived: line =>
            {
                Dispatcher.Invoke(() => StatusText.Text = $"Instalando {app.Name}: {line}");
            });

            success = result.Success;

            StatusText.Text = success
                ? $"{app.Name} instalado com sucesso."
                : $"Falha ao instalar {app.Name} (código {result.ExitCode}).";

            button.Content = success ? "Instalado" : "Tentar novamente";
        }
        catch (Exception ex)
        {
            StatusText.Text = $"Erro ao instalar {app.Name}: {ex.Message}";
            button.Content = originalContent;
        }
        finally
        {
            button.IsEnabled = !success;
        }
    }

    // -------------------------------------------------------------
    // SELEÇÃO -> GUIA ATIVA DA COLEÇÃO
    // -------------------------------------------------------------

    private void AddSelectionButton_Click(object sender, RoutedEventArgs e)
    {
        var selected = _storeService.GetAll().Where(a => a.IsSelected).ToList();

        if (selected.Count == 0)
        {
            StatusText.Text = "Nenhum app marcado para adicionar.";
            return;
        }

        // Envia a seleção para a guia ativa no PackageCollectionService
        int added = _collectionService.AddRangeToActive(selected);

        // Desmarca os checkboxes da UI
        foreach (var app in selected)
        {
            app.IsSelected = false;
        }

        var tabTitle = _collectionService.ActiveTab?.Title ?? "Perfil Padrão";
        StatusText.Text = added == selected.Count
            ? $"{added} pacote(s) adicionado(s) à guia '{tabTitle}'."
            : $"{added} pacote(s) adicionado(s) à guia '{tabTitle}' ({selected.Count - added} já estavam lá).";
    }
}