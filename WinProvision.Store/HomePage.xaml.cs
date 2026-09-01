using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;
using WinProvision.Core.Models;
using WinProvision.Core.Services;
using WinProvision.Core.Services.Profile;
using WinProvision.Store.Services;

namespace WinProvision.Store;

public partial class HomePage : Page
{
    private readonly StoreService _storeService;
    private readonly WingetExecutor _wingetExecutor;
    private readonly PackageCollectionService _collectionService;
    private readonly OperationsQueueService _queueService;
    private readonly InstalledAppsService _installedAppsService;
    private readonly AppLaunchService _appLaunchService;
    private readonly AppDetailsOverlayService _detailsOverlayService;
    private readonly DispatcherTimer _debounceTimer;
    private List<AppEntry> _allApps = [];
    private bool _catalogLoaded;
    private bool _catalogLoading;
    private bool _isRefreshing;
    private string _selectedCategoryTag = "all";

    // Ajuste esse dicionário para bater com a taxonomia real do catálogo
    // (ex.: as 8 categorias já geradas via Gemini Notebook), caso as tags
    // do apps.json usem termos diferentes dos chips abaixo.
    private static readonly Dictionary<string, string[]> CategoryTagMap = new(StringComparer.OrdinalIgnoreCase)
    {
        ["productivity"] = ["productivity", "office"],
        ["development"] = ["development", "dev", "programming"],
        ["utilities"] = ["utilities", "system"],
        ["multimedia"] = ["multimedia", "media", "video", "audio", "design"],
        ["security"] = ["security", "privacy"],
    };

    public ObservableCollection<AppEntry> Apps { get; } = [];

    // NOVA COLEÇÃO: Para preencher os Banners Superiores de Destaque
    public ObservableCollection<AppEntry> FeaturedApps { get; } = [];

    public HomePage(StoreService storeService, WingetExecutor wingetExecutor, PackageCollectionService collectionService,
        OperationsQueueService queueService, InstalledAppsService installedAppsService, AppLaunchService appLaunchService,
        AppDetailsOverlayService detailsOverlayService)
    {
        InitializeComponent();

        _storeService = storeService;
        _wingetExecutor = wingetExecutor;
        _collectionService = collectionService;
        _queueService = queueService;
        _installedAppsService = installedAppsService;
        _appLaunchService = appLaunchService;
        _detailsOverlayService = detailsOverlayService;

        // Define o contexto de dados para o XAML enxergar as listas Apps e FeaturedApps
        DataContext = this;

        // Debounce: espera 300ms sem digitação antes de refiltrar/buscar
        _debounceTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(300) };
        _debounceTimer.Tick += (_, _) =>
        {
            _debounceTimer.Stop();
            ApplyFilter();
        };

        BuildCategoryChips();

        // Assina o cache compartilhado de instalados para refletir aqui mudanças feitas
        // em outra tela (ex.: desinstalar pelos Detalhes do Pacote). Como a HomePage agora
        // é Singleton (uma instância só, reaproveitada entre navegações — é o que faz a
        // busca/filtro persistirem quando você sai e volta), essa inscrição pode ficar viva
        // pelo tempo de vida do app inteiro, sem o antigo workaround de desinscrever no
        // Unloaded (que era necessário só quando a página era Transient e recriada a cada
        // navegação — sem isso, cada instância antiga ficaria presa na memória).
        _installedAppsService.Changed += OnInstalledAppsChanged;
        _storeService.CatalogUpdated += OnCatalogUpdated;
        _storeService.CacheCleared += OnCacheCleared;

        Loaded += HomePage_Loaded;
    }

    private void OnInstalledAppsChanged()
    {
        if (!Dispatcher.CheckAccess())
        {
            Dispatcher.InvokeAsync(OnInstalledAppsChanged);
            return;
        }

        SyncInstalledFlags(_allApps);
        ApplyFilter();
    }

    // Chamado quando o StoreService troca _cachedCatalog por uma lista nova
    // (refresh em background). Sem isso, _allApps ficava congelado na
    // instância antiga e a Visão Geral nunca refletia dados atualizados
    // do catálogo remoto até um refresh manual.
    private void OnCatalogUpdated(IReadOnlyList<AppEntry> catalog)
    {
        if (!Dispatcher.CheckAccess())
        {
            Dispatcher.InvokeAsync(() => OnCatalogUpdated(catalog));
            return;
        }

        _allApps = catalog.ToList();
        SyncInstalledFlags(_allApps);
        ApplyFilter();
    }

    private void OnCacheCleared()
    {
        if (!Dispatcher.CheckAccess())
        {
            Dispatcher.InvokeAsync(OnCacheCleared);
            return;
        }

        _catalogLoaded = false;
        _allApps = [];
        Apps.Clear();
        FeaturedApps.Clear();
        StatusText.Text = "Cache limpo. O catálogo será recarregado na próxima abertura da Home.";
    }

    private void SyncInstalledFlags(IEnumerable<AppEntry> apps)
    {
        foreach (AppEntry app in apps)
        {
            app.IsInstalled = _installedAppsService.IsInstalled(app.Id);
        }
    }

    private void BuildCategoryChips()
    {
        CategoryList.ItemsSource = new List<CategoryChip>
        {
            new("all", "Todos", Wpf.Ui.Controls.SymbolRegular.Grid24, true),
            new("productivity", "Produtividade", Wpf.Ui.Controls.SymbolRegular.Document24, false),
            new("development", "Desenvolvimento", Wpf.Ui.Controls.SymbolRegular.Code24, false),
            new("utilities", "Utilitários", Wpf.Ui.Controls.SymbolRegular.Wrench24, false),
            new("multimedia", "Multimídia", Wpf.Ui.Controls.SymbolRegular.Play24, false),
            new("security", "Segurança", Wpf.Ui.Controls.SymbolRegular.Shield24, false),
        };
    }

    private async void HomePage_Loaded(object sender, RoutedEventArgs e)
    {
        if (_catalogLoaded)
        {
            ApplyFilter();
            return;
        }

        if (_catalogLoading)
        {
            return;
        }

        _catalogLoading = true;
        StatusText.Text = "Carregando catálogo...";

        try
        {
            _allApps = await _storeService.LoadCatalogAsync();
            await _installedAppsService.EnsureLoadedAsync();
            SyncInstalledFlags(_allApps);

            _catalogLoaded = true;
            ApplyFilter();
        }
        catch (Exception ex)
        {
            StatusText.Text = $"Não foi possível carregar o catálogo: {ex.Message}";
        }
        finally
        {
            _catalogLoading = false;
        }
    }

    private void CategoryChip_Checked(object sender, RoutedEventArgs e)
    {
        if (sender is not RadioButton { Tag: string tag })
        {
            return;
        }

        _selectedCategoryTag = tag;
        ApplyFilter();
    }

    private void SearchBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        _debounceTimer.Stop();
        _debounceTimer.Start();
    }

    private void SearchBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter)
        {
            return;
        }

        _debounceTimer.Stop();
        ApplyFilter();
    }

    private void ApplyFilter()
    {
        if (!_catalogLoaded)
        {
            return;
        }

        string query = SearchBox.Text?.Trim() ?? string.Empty;

        // Com texto de busca, usa o ranking de relevância do catálogo completo
        // (o mesmo motor de busca que a antiga StorePage usava). Sem busca,
        // mostra os apps em destaque ordenados por score.
        IEnumerable<AppEntry> source = string.IsNullOrWhiteSpace(query)
            ? _allApps.OrderByDescending(a => a.Score).ThenByDescending(a => a.GitHubStars ?? 0)
            : _storeService.Search(query);

        if (_selectedCategoryTag != "all" && CategoryTagMap.TryGetValue(_selectedCategoryTag, out string[]? keywords))
        {
            source = source.Where(app => app.Tags.Any(tag => keywords.Contains(tag, StringComparer.OrdinalIgnoreCase)));
        }

        var results = source.Take(50).ToList();

        // 1. Preenche os banners de Destaque Superiores (Pega os 3 primeiros)
        FeaturedApps.Clear();
        foreach (AppEntry app in results.Take(3))
        {
            FeaturedApps.Add(app);
        }

        // 2. Preenche a lista de apoio sem repetir os três destaques do topo.
        Apps.Clear();
        foreach (AppEntry app in results.Skip(3))
        {
            Apps.Add(app);
        }

        CatalogCountText.Text = results.Count.ToString();

        StatusText.Text = string.IsNullOrWhiteSpace(query)
            ? $"{results.Count} app(s) em destaque"
            : results.Count == 0
                ? $"Nenhum resultado para \"{query}\""
                : $"{results.Count} resultado(s) para \"{query}\"";
    }

    private async void RefreshButton_Click(object sender, RoutedEventArgs e)
    {
        if (_isRefreshing)
        {
            return;
        }

        _isRefreshing = true;
        RefreshCatalogButton.IsEnabled = false;
        StatusText.Text = "Atualizando catálogo...";

        try
        {
            _allApps = await _storeService.LoadCatalogAsync(forceRefresh: true);
            await _installedAppsService.RefreshAsync();
            SyncInstalledFlags(_allApps);
            _catalogLoaded = true;
            ApplyFilter();
        }
        catch (Exception ex)
        {
            StatusText.Text = $"Falha ao atualizar o catálogo: {ex.Message}";
        }
        finally
        {
            _isRefreshing = false;
            RefreshCatalogButton.IsEnabled = true;
        }
    }

    private async void InstallOrOpenButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: AppEntry app })
        {
            return;
        }

        if (app.IsInstalled)
        {
            await OpenInstalledAppAsync(app);
            return;
        }

        // Já em andamento (ex.: outro clique escapou antes do IsHitTestVisible
        // atualizar) - ignora silenciosamente em vez de enfileirar de novo.
        if (app.IsInstalling)
        {
            return;
        }

        // O texto/ícone do botão (Instalar/Instalando/Abrir) ficam com binding em
        // IsInstalled + IsInstalling - por isso, em vez de sobrescrever button.Content
        // ou desabilitar o botão (o que mudaria a cor via o estado "disabled" padrão),
        // o "em andamento" liga IsInstalling: troca o rótulo para "Instalando" e
        // bloqueia cliques via IsHitTestVisible, mantendo a cor azul fixa do botão. O
        // progresso de verdade aparece no painel flutuante da fila, estilo UnigetUI.
        app.IsInstalling = true;
        StatusText.Text = $"{app.Name} adicionado à fila de instalação.";

        try
        {
            var result = await OperationRunner.RunInstallAsync(
                _queueService, _wingetExecutor, app.Id, app.Name, app.IconUrl, _installedAppsService);

            if (result.Success)
            {
                app.IsInstalled = true;
                StatusText.Text = $"{app.Name} instalado.";
            }
            else
            {
                StatusText.Text = $"Falha ao instalar {app.Name}.";
            }
        }
        catch (Exception ex)
        {
            StatusText.Text = $"Erro ao instalar {app.Name}: {ex.Message}";
        }
        finally
        {
            app.IsInstalling = false;
        }
    }

    private async Task OpenInstalledAppAsync(AppEntry app)
    {
        StatusText.Text = $"Abrindo {app.Name}...";

        string? executablePath = await _appLaunchService.TryResolveExecutableAsync(app);

        if (executablePath is null || !_appLaunchService.TryLaunch(executablePath))
        {
            StatusText.Text = $"Não foi possível localizar o executável de {app.Name}.";
            return;
        }

        StatusText.Text = $"{app.Name} aberto.";
    }

    // -------------------------------------------------------------
    // DETALHES DO APP (overlay dentro da própria MainWindow — ver
    // AppDetailsOverlay/AppDetailsOverlayService; substitui a antiga janela
    // separada AppDetailsWindow)
    // -------------------------------------------------------------

    private void AppCard_Click(object sender, MouseButtonEventArgs e)
    {
        // Cliques nos ui:Button dentro do cartão já chegam aqui como "Handled"
        // (ButtonBase marca isso ao processar o clique), então este handler só
        // dispara para cliques no corpo do cartão.
        if (sender is not FrameworkElement { DataContext: AppEntry app })
        {
            return;
        }

        _detailsOverlayService.Show(app);
    }

    private void LoadMoreButton_Click(object sender, RoutedEventArgs e)
    {
        StatusText.Text = "Carregando mais aplicativos...";
        // Coloque aqui a sua lógica de paginação no futuro
    }
}
