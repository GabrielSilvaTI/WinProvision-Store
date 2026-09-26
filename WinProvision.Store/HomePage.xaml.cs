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
    private readonly WinProvision.Core.Services.Indexing.MsStoreCatalogService _msStoreCatalogService;
    private readonly StoreService _storeService;
    private readonly WinGetService _winGetService;
    private readonly IconService _iconService;
    private readonly WingetExecutor _wingetExecutor;
    private readonly PackageCollectionService _collectionService;
    private readonly OperationsQueueService _queueService;
    private readonly InstalledAppsService _installedAppsService;
    private readonly AppDetailsOverlayService _detailsOverlayService;
    private readonly DispatcherTimer _debounceTimer;
    private CancellationTokenSource? _searchCancellation;
    private string? _searchQueryInFlight;
    private string? _liveSearchQuery;
    private IReadOnlyList<AppEntry> _liveSearchResults = [];
    private List<AppEntry> _allApps = [];
    private bool _catalogLoaded;
    private bool _catalogLoading;
    private bool _isRefreshing;
    private bool _storeBannersLoading;
    private bool _storeBannersLoaded;
    private int _heroBannerIndex;
    private string _selectedCategoryTag = "all";
    private bool _catalogSortByName;

    private static readonly Dictionary<string, string[]> CategoryTagMap = new(StringComparer.OrdinalIgnoreCase)
    {
        ["productivity"] = ["productivity", "office", "business"],
        ["development"] = ["development", "developer tools", "programming"],
        ["utilities"] = ["utilities", "tools", "system"],
        ["multimedia"] = ["multimedia", "photo", "video", "music"],
        ["security"] = ["security", "antivirus", "privacy"],
    };

    public ObservableCollection<AppEntry> Apps { get; } = [];

    // NOVA COLEÇÃO: Para preencher os Banners Superiores de Destaque
    public ObservableCollection<AppEntry> FeaturedApps { get; } = [];

    public ObservableCollection<AppEntry> StoreBannerApps { get; } = [];
    public ObservableCollection<AppEntry> SideStoreBannerApps { get; } = [];
    public ObservableCollection<AppEntry> PopularApps { get; } = [];

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

    public HomePage(WingetExecutor wingetExecutor, PackageCollectionService collectionService,
        OperationsQueueService queueService, InstalledAppsService installedAppsService,
        AppDetailsOverlayService detailsOverlayService,
        WinProvision.Core.Services.Indexing.MsStoreCatalogService msStoreCatalogService,
        IconService iconService, StoreService storeService, WinGetService winGetService)
    {
        InitializeComponent();

        _msStoreCatalogService = msStoreCatalogService;
        _storeService = storeService;
        _winGetService = winGetService;
        _iconService = iconService;
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

        _storeService.CatalogUpdated += OnCatalogUpdated;
        _storeService.CacheCleared += OnCacheCleared;
        _installedAppsService.Changed += OnInstalledAppsChanged;
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
            SidebarColumn.Width = new GridLength(220);
            SidebarGapColumn.Width = new GridLength(16);
            SidebarPanel.Visibility = Visibility.Visible;
        }

        // 2. Colunas de Destaque responsivas: 3 em tela cheia, 2 em meia tela, 1 se muito estreito
        FeaturedColumns = width < 820 ? 4 : 6;

        // 3. Colunas da Grade de Apps responsivas: 2 em meia tela/cheia, 1 se muito estreito
        AppsColumns = width < 680 ? 1 : 3;
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
        UpdatePopularApps();
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
        PopularApps.Clear();
        StatusText.Text = "Cache limpo. O catálogo será atualizado ao abrir esta tela.";
    }

    private void SyncInstalledFlags(IEnumerable<AppEntry> apps)
    {
        foreach (AppEntry app in apps)
        {
            app.IsInstalled = _installedAppsService.IsInstalled(app.Name, app.Id);
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
        if (!_storeBannersLoaded && !_storeBannersLoading)
        {
            _ = LoadStoreBannersAsync();
        }

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
            UpdatePopularApps();

            _catalogLoaded = true;
            UpdateCatalogSyncStatus();
            ApplyFilter();
        }
        catch
        {
            StatusText.Text = "Não foi possível carregar o catálogo. Tente novamente.";
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

    private void IconsViewToggleButton_Click(object sender, RoutedEventArgs e) => SetAppsViewMode(list: false, icons: true);

    private void CatalogSortComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        _catalogSortByName = CatalogSortComboBox.SelectedIndex == 1;
        ApplyFilter();
    }

    private async Task LoadStoreBannersAsync()
    {
        _storeBannersLoading = true;
        try
        {
            string[] productIds = ["9MV0B5HZVK9Z", "9WZDNCRD29V9", "9N9LKV8R9VGM"];
            List<AppEntry> products = await _msStoreCatalogService.FetchAsync(productIds);
            var ordered = productIds
                .Select(id => products.FirstOrDefault(app => app.Id.Equals(id, StringComparison.OrdinalIgnoreCase)))
                .Where(app => app is not null && !string.IsNullOrWhiteSpace(app.StoreBannerUrl))
                .Cast<AppEntry>()
                .ToList();

            StoreBannerApps.Clear();
            SideStoreBannerApps.Clear();
            foreach (AppEntry app in ordered)
            {
                StoreBannerApps.Add(app);
            }

            if (StoreBannerApps.Count > 0)
            {
                RightStorePromo.DataContext = StoreBannerApps[0];
            }

            foreach (AppEntry app in ordered.Skip(1))
            {
                SideStoreBannerApps.Add(app);
            }

            if (StoreBannerApps.Count > 0)
            {
                SetHeroBanner(0);
            }

            _storeBannersLoaded = true;
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[HomePage] Falha ao carregar banners da Microsoft Store: {ex.Message}");
        }
        finally
        {
            _storeBannersLoading = false;
        }
    }

    private void SetHeroBanner(int index)
    {
        if (StoreBannerApps.Count == 0)
        {
            return;
        }

        _heroBannerIndex = (index % StoreBannerApps.Count + StoreBannerApps.Count) % StoreBannerApps.Count;
        HeroBanner.DataContext = StoreBannerApps[_heroBannerIndex];
        HeroBanner.Visibility = Visibility.Visible;
        Brush activeDot = TryFindResource("InstallActionBrush") as Brush ?? Brushes.DeepSkyBlue;
        HeroBannerIndicator0.Fill = _heroBannerIndex == 0 ? activeDot : Brushes.White;
        HeroBannerIndicator1.Fill = _heroBannerIndex == 1 ? activeDot : Brushes.White;
        HeroBannerIndicator2.Fill = _heroBannerIndex == 2 ? activeDot : Brushes.White;
    }

    private void HeroBannerPrevious_Click(object sender, RoutedEventArgs e) => SetHeroBanner(_heroBannerIndex - 1);

    private void HeroBannerNext_Click(object sender, RoutedEventArgs e) => SetHeroBanner(_heroBannerIndex + 1);

    private void HeroBannerDetails_Click(object sender, RoutedEventArgs e)
    {
        if (HeroBanner.DataContext is AppEntry app)
        {
            _detailsOverlayService.Show(app);
        }
    }

    private void HeroBannerDot_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { Tag: string indexText } && int.TryParse(indexText, out int index))
        {
            SetHeroBanner(index);
        }
    }

    private void SetAppsViewMode(bool list, bool icons = false)
    {
        AppsGridScrollViewer.Visibility = !list && !icons ? Visibility.Visible : Visibility.Collapsed;
        AppsListScrollViewer.Visibility = list ? Visibility.Visible : Visibility.Collapsed;
        AppsIconsScrollViewer.Visibility = icons ? Visibility.Visible : Visibility.Collapsed;
        FeaturedAppsList.Visibility = !list && !icons ? Visibility.Visible : Visibility.Collapsed;
        FeaturedAppsListView.Visibility = list ? Visibility.Visible : Visibility.Collapsed;
        FeaturedAppsIconsList.Visibility = icons ? Visibility.Visible : Visibility.Collapsed;
        GridViewToggleButton.IsChecked = !list && !icons;
        ListViewToggleButton.IsChecked = list;
        IconsViewToggleButton.IsChecked = icons;

        Brush accent = TryFindResource("SystemAccentColorPrimaryBrush") as Brush ?? SystemColors.HighlightBrush;
        Brush primaryText = TryFindResource("TextFillColorPrimaryBrush") as Brush ?? SystemColors.ControlTextBrush;
        GridViewToggleButton.Background = !list && !icons ? accent : Brushes.Transparent;
        ListViewToggleButton.Background = list ? accent : Brushes.Transparent;
        GridViewToggleButton.Foreground = !list && !icons ? Brushes.White : primaryText;
        ListViewToggleButton.Foreground = list ? Brushes.White : primaryText;
        IconsViewToggleButton.Background = icons ? accent : Brushes.Transparent;
        IconsViewToggleButton.Foreground = icons ? Brushes.White : primaryText;
    }

    private static bool HasRealIcon(AppEntry app) =>
        !string.IsNullOrWhiteSpace(app.IconUrl) &&
        !app.IconUrl.Equals(IconService.DefaultIconPackUri, StringComparison.OrdinalIgnoreCase);

    private void UpdatePopularApps()
    {
        PopularApps.Clear();
        foreach (AppEntry app in _allApps.Where(HasRealIcon).OrderByDescending(app => app.Score).Take(4))
        {
            PopularApps.Add(app);
        }
    }

    private void ApplyFilter()
    {
        if (!_catalogLoaded)
        {
            return;
        }

        string query = SearchBox.Text?.Trim() ?? string.Empty;
        if (string.IsNullOrWhiteSpace(query))
        {
            _searchCancellation?.Cancel();
            _searchCancellation = null;
            _searchQueryInFlight = null;
            _liveSearchQuery = null;
            _liveSearchResults = [];
        }

        IEnumerable<AppEntry> source = string.IsNullOrWhiteSpace(query)
            ? _allApps.Where(HasRealIcon).OrderByDescending(a => a.Score).ThenByDescending(a => a.GitHubStars ?? 0)
            : GetSearchResults(query);

        if (_selectedCategoryTag != "all" && CategoryTagMap.TryGetValue(_selectedCategoryTag, out string[]? keywords))
        {
            source = source.Where(app => app.Tags.Any(tag => keywords.Contains(tag, StringComparer.OrdinalIgnoreCase)));
        }

        if (_catalogSortByName)
        {
            source = source.OrderBy(app => app.Name, StringComparer.CurrentCultureIgnoreCase);
        }

        var results = source.Take(12).ToList();

        FeaturedApps.Clear();
        foreach (AppEntry app in results.Take(6))
        {
            FeaturedApps.Add(app);
        }

        Apps.Clear();
        foreach (AppEntry app in results.Skip(6).Take(6))
        {
            Apps.Add(app);
        }

        CatalogCountText.Text = results.Count.ToString();

        StatusText.Text = string.IsNullOrWhiteSpace(query)
            ? $"Aplicativos em destaque: {results.Count}"
            : results.Count == 0
                ? _liveSearchQuery == query ? $"Nenhum resultado para \"{query}\"" : $"Pesquisando fontes de pacotes por \"{query}\"..."
                : results.Count == 1 ? "1 resultado." : $"Resultados: {results.Count}";

        if (!string.IsNullOrWhiteSpace(query) &&
            !string.Equals(_liveSearchQuery, query, StringComparison.OrdinalIgnoreCase) &&
            !string.Equals(_searchQueryInFlight, query, StringComparison.OrdinalIgnoreCase))
        {
            _ = SearchLiveCatalogsAsync(query);
        }
    }

    private IEnumerable<AppEntry> GetSearchResults(string query)
    {
        IEnumerable<AppEntry> localResults = _storeService.Search(query);
        IEnumerable<AppEntry> liveResults = string.Equals(_liveSearchQuery, query, StringComparison.OrdinalIgnoreCase)
            ? _liveSearchResults
            : [];

        return localResults.Take(30).Concat(liveResults).Concat(localResults.Skip(30))
            .GroupBy(app => $"{app.Source}\0{app.Id}", StringComparer.OrdinalIgnoreCase)
            .Select(group => group
                .OrderByDescending(app => !string.IsNullOrWhiteSpace(app.StoreIconUrl))
                .ThenByDescending(HasRealIcon)
                .First());
    }

    private async Task SearchLiveCatalogsAsync(string query)
    {
        _searchCancellation?.Cancel();
        _liveSearchResults = [];
        var cancellation = new CancellationTokenSource();
        _searchCancellation = cancellation;
        _searchQueryInFlight = query;
        CancellationToken cancellationToken = cancellation.Token;

        try
        {
            var winGetSearchTask = _winGetService.SearchAsync(query, cancellationToken);
            var storeSearchTask = _msStoreCatalogService.SearchAsync(query, cancellationToken);
            try
            {
                await Task.WhenAll(winGetSearchTask, storeSearchTask);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[HomePage] Uma das fontes de busca falhou: {ex.Message}");
            }

            var matches = winGetSearchTask.IsCompletedSuccessfully
                ? await winGetSearchTask
                : [];
            var directStoreEntries = storeSearchTask.IsCompletedSuccessfully
                ? await storeSearchTask
                : [];
            var storeIds = matches
                .Where(match => string.Equals(match.Source, "msstore", StringComparison.OrdinalIgnoreCase))
                .Select(match => match.PackageId)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray();

            var resolvedStoreEntries = storeIds.Length == 0
                ? []
                : await _msStoreCatalogService.FetchAsync(storeIds, cancellationToken);
            var storeEntries = directStoreEntries.Concat(resolvedStoreEntries).ToArray();
            var storeById = storeEntries
                .GroupBy(app => app.Id, StringComparer.OrdinalIgnoreCase)
                .ToDictionary(group => group.Key, group => group.First(), StringComparer.OrdinalIgnoreCase);

            var liveResults = matches.Select(match =>
            {
                if (match.Source.Equals("msstore", StringComparison.OrdinalIgnoreCase) && storeById.TryGetValue(match.PackageId, out var storeEntry))
                {
                    storeEntry.IconUrl = _iconService.ResolveIconUrl(storeEntry);
                    storeEntry.IsInstalled = _installedAppsService.IsInstalled(storeEntry.Name, storeEntry.Id);
                    return storeEntry;
                }

                var app = new AppEntry
                {
                    Id = match.PackageId,
                    Name = match.Name,
                    Version = string.IsNullOrWhiteSpace(match.Version) ? "-" : match.Version,
                    Publisher = match.Publisher,
                    Source = match.Source,
                };
                app.IconUrl = _iconService.ResolveIconUrl(app);
                app.IsInstalled = _installedAppsService.IsInstalled(app.Name, app.Id);
                return app;
            }).Concat(directStoreEntries.Select(storeEntry =>
            {
                storeEntry.IconUrl = _iconService.ResolveIconUrl(storeEntry);
                storeEntry.IsInstalled = _installedAppsService.IsInstalled(storeEntry.Name, storeEntry.Id);
                return storeEntry;
            })).GroupBy(app => $"{app.Source}\0{app.Id}", StringComparer.OrdinalIgnoreCase)
                .Select(group => group.First())
                .ToArray();

            cancellationToken.ThrowIfCancellationRequested();
            if (!string.Equals(SearchBox.Text?.Trim(), query, StringComparison.OrdinalIgnoreCase))
            {
                return;
            }

            _liveSearchQuery = query;
            _liveSearchResults = liveResults;
            ApplyFilter();
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[HomePage] Falha na busca ao vivo: {ex.Message}");
            if (string.Equals(SearchBox.Text?.Trim(), query, StringComparison.OrdinalIgnoreCase))
            {
                _liveSearchResults = [];
                _liveSearchQuery = query;
                ApplyFilter();
                if (Apps.Count == 0 && FeaturedApps.Count == 0)
                {
                    StatusText.Text = "Não foi possível consultar as fontes do WinGet. Exibindo resultados do catálogo local.";
                }
            }
        }
        finally
        {
            if (ReferenceEquals(_searchCancellation, cancellation))
            {
                _searchCancellation = null;
                _searchQueryInFlight = null;
            }
            cancellation.Dispose();
        }
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
            UpdatePopularApps();
            _catalogLoaded = true;
            UpdateCatalogSyncStatus();
            ApplyFilter();
        }
        catch
        {
            StatusText.Text = "Não foi possível atualizar o catálogo. Tente novamente.";
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
        catch
        {
            // Ensure flag reset and report error
            app.IsInstalling = false;
            StatusText.Text = $"Não foi possível instalar {app.Name}. Tente novamente.";
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
