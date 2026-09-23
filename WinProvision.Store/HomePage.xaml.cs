using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
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

    public static readonly DependencyProperty FeaturedColumnsProperty =
        DependencyProperty.Register(nameof(FeaturedColumns), typeof(int), typeof(HomePage), new PropertyMetadata(3));

    public static readonly DependencyProperty AppsColumnsProperty =
        DependencyProperty.Register(nameof(AppsColumns), typeof(int), typeof(HomePage), new PropertyMetadata(2));

    public int FeaturedColumns
    {
        get => (int)GetValue(FeaturedColumnsProperty);
        set => SetValue(FeaturedColumnsProperty, value);
    }

    public int AppsColumns
    {
        get => (int)GetValue(AppsColumnsProperty);
        set => SetValue(AppsColumnsProperty, value);
    }

    public HomePage(StoreService storeService, WingetExecutor wingetExecutor, PackageCollectionService collectionService,
        OperationsQueueService queueService, InstalledAppsService installedAppsService,
        AppDetailsOverlayService detailsOverlayService)
    {
        InitializeComponent();

        _storeService = storeService;
        _wingetExecutor = wingetExecutor;
        _collectionService = collectionService;
        _queueService = queueService;
        _installedAppsService = installedAppsService;
        _detailsOverlayService = detailsOverlayService;

        // Define o contexto de dados para o XAML enxergar as listas Apps e FeaturedApps
        DataContext = this;
        SetAppsViewMode(list: false);

        // Debounce: espera 300ms sem digitação antes de refiltrar/buscar
        _debounceTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(300) };
        _debounceTimer.Tick += (_, _) =>
        {
            _debounceTimer.Stop();
            ApplyFilter();
        };

        BuildCategoryChips();

        _installedAppsService.Changed += OnInstalledAppsChanged;
        _storeService.CatalogUpdated += OnCatalogUpdated;
        _storeService.CacheCleared += OnCacheCleared;

        Loaded += HomePage_Loaded;
        SizeChanged += HomePage_SizeChanged;
    }

    private void HomePage_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        double width = e.NewSize.Width;

        // 1. Sidebar responsiva: recolhe a barra lateral quando a largura for menor que 880px
        // para dar 100% do espaço aos cartões de aplicativos sem quebrar layout.
        if (width < 880)
        {
            SidebarColumn.Width = new GridLength(0);
            SidebarGapColumn.Width = new GridLength(0);
            SidebarPanel.Visibility = Visibility.Collapsed;
        }
        else
        {
            SidebarColumn.Width = new GridLength(264);
            SidebarGapColumn.Width = new GridLength(20);
            SidebarPanel.Visibility = Visibility.Visible;
        }

        // 2. Colunas de Destaque responsivas: 3 em tela cheia, 2 em meia tela, 1 se muito estreito
        FeaturedColumns = width < 700 ? 1 : (width < 1120 ? 2 : 3);

        // 3. Colunas da Grade de Apps responsivas: 2 em meia tela/cheia, 1 se muito estreito
        AppsColumns = width < 560 ? 1 : 2;
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

    private void OnCatalogUpdated(IReadOnlyList<AppEntry> catalog)
    {
        if (!Dispatcher.CheckAccess())
        {
            Dispatcher.InvokeAsync(() => OnCatalogUpdated(catalog));
            return;
        }

        _allApps = catalog.ToList();
        SyncInstalledFlags(_allApps);
        UpdateCatalogSyncStatus();
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
        StatusText.Text = "Cache limpo. Catálogo será recarregado ao abrir a Home.";
    }

    private void SyncInstalledFlags(IEnumerable<AppEntry> apps)
    {
        foreach (AppEntry app in apps)
        {
            app.IsInstalled = _installedAppsService.IsInstalled(app.Name);
        }
    }

    private void BuildCategoryChips()
    {
        CategoryList.ItemsSource = new List<CategoryChip>
        {
            new("all", "Todos", "\uE8A9", true),
            new("productivity", "Produtividade", "\uE7C3", false),
            new("development", "Desenvolvimento", "\uE943", false),
            new("utilities", "Utilitários", "\uEC7A", false),
            new("multimedia", "Multimídia", "\uE768", false),
            new("security", "Segurança", "\uE83D", false),
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
            UpdateCatalogSyncStatus();
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

    private void GridViewToggleButton_Click(object sender, RoutedEventArgs e) => SetAppsViewMode(list: false);

    private void ListViewToggleButton_Click(object sender, RoutedEventArgs e) => SetAppsViewMode(list: true);

    private void SetAppsViewMode(bool list)
    {
        AppsGridScrollViewer.Visibility = list ? Visibility.Collapsed : Visibility.Visible;
        AppsListScrollViewer.Visibility = list ? Visibility.Visible : Visibility.Collapsed;
        FeaturedAppsList.Visibility = list ? Visibility.Collapsed : Visibility.Visible;
        FeaturedAppsListView.Visibility = list ? Visibility.Visible : Visibility.Collapsed;
        GridViewToggleButton.IsChecked = !list;
        ListViewToggleButton.IsChecked = list;

        Brush accent = TryFindResource("SystemAccentColorPrimaryBrush") as Brush ?? SystemColors.HighlightBrush;
        Brush primaryText = TryFindResource("TextFillColorPrimaryBrush") as Brush ?? SystemColors.ControlTextBrush;
        GridViewToggleButton.Background = !list ? accent : Brushes.Transparent;
        ListViewToggleButton.Background = list ? accent : Brushes.Transparent;
        GridViewToggleButton.Foreground = !list ? Brushes.White : primaryText;
        ListViewToggleButton.Foreground = list ? Brushes.White : primaryText;
    }

    private static bool HasRealIcon(AppEntry app) =>
        !string.IsNullOrWhiteSpace(app.IconUrl) &&
        !app.IconUrl.Equals(IconService.DefaultIconPackUri, StringComparison.OrdinalIgnoreCase);

    private void ApplyFilter()
    {
        if (!_catalogLoaded)
        {
            return;
        }

        string query = SearchBox.Text?.Trim() ?? string.Empty;

        IEnumerable<AppEntry> source = string.IsNullOrWhiteSpace(query)
            ? _allApps.Where(HasRealIcon).OrderByDescending(a => a.Score).ThenByDescending(a => a.GitHubStars ?? 0)
            : _storeService.Search(query);

        if (_selectedCategoryTag != "all" && CategoryTagMap.TryGetValue(_selectedCategoryTag, out string[]? keywords))
        {
            source = source.Where(app => app.Tags.Any(tag => keywords.Contains(tag, StringComparer.OrdinalIgnoreCase)));
        }

        var results = source.Take(50).ToList();

        FeaturedApps.Clear();
        foreach (AppEntry app in results.Take(3))
        {
            FeaturedApps.Add(app);
        }

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
                : $"{results.Count} resultado(s)";
    }

    private void UpdateCatalogSyncStatus()
    {
        DateTime? syncedAt = _storeService.LastCatalogSyncUtc;
        if (syncedAt is not { } utc)
        {
            LastCatalogSyncText.Text = "Sincronização ainda não realizada";
            SetCatalogStatus(stale: true);
            return;
        }

        LastCatalogSyncText.Text = $"Sincronizado em {utc.ToLocalTime():dd/MM/yyyy HH:mm}";
        SetCatalogStatus(DateTime.UtcNow - utc > TimeSpan.FromHours(24));
    }

    private void SetCatalogStatus(bool stale)
    {
        CatalogStatusIcon.Glyph = stale ? "\uE946" : "\uE930";
        var brush = (System.Windows.Media.Brush)FindResource(
            stale ? "SystemFillColorCautionBrush" : "SystemFillColorSuccessBrush");
        CatalogStatusIcon.Foreground = brush;
        CatalogStatusText.Foreground = brush;
        CatalogStatusText.Text = stale ? "Desatualizado" : "Atualizado";
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
            UpdateCatalogSyncStatus();
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

    private async void InstallButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: AppEntry app })
        {
            return;
        }

        if (app.IsInstalled)
        {
            return;
        }

        if (app.IsInstalling)
        {
            return;
        }

        app.IsInstalling = true;
        StatusText.Text = $"{app.Name} adicionado à fila de instalação.";

        try
        {
            var result = await OperationRunner.RunInstallAsync(
                _queueService,
                _wingetExecutor,
                app.Id,
                app.Name,
                app.IconUrl,
                _installedAppsService,
                source: app.Source);

            // Reset installing flag after operation completes
            app.IsInstalling = false;

            if (result.Success)
            {
                app.IsInstalled = true;
                StatusText.Text = $"{app.Name} instalado com sucesso.";
            }
            else
            {
                // Failure: status text may already be set by OperationRunner; provide a generic message
                StatusText.Text = $"Falha ao instalar {app.Name}.";
            }
        }
        catch (Exception ex)
        {
            // Ensure flag reset and report error
            app.IsInstalling = false;
            StatusText.Text = $"Erro ao instalar {app.Name}: {ex.Message}";
        }
    }

    private void AppCard_Click(object sender, MouseButtonEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: AppEntry app })
        {
            // Alterado de "ShowDetails" para "Show" 
            // Caso o erro continue, verifique no seu arquivo AppDetailsOverlayService.cs 
            // qual o nome correto do método (pode ser Open, ShowAsync, etc).
            _detailsOverlayService.Show(app);
        }
    }
}
