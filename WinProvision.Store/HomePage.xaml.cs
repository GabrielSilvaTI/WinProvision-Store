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
    private ShelfView _shelfView;

    private enum ShelfView { Overview, Popular, Store }

    private static readonly Dictionary<string, string[]> CategoryTagMap = new(StringComparer.OrdinalIgnoreCase)
    {
        ["productivity"] = ["productivity", "produtividade", "office", "business", "negócios"],
        ["development"] = ["development", "developer", "programming", "desenvolvimento", "programação"],
        ["utilities"] = ["utilities", "tools", "system", "utilitários", "ferramentas", "sistema"],
        ["multimedia"] = ["multimedia", "photo", "video", "music", "multimídia", "foto", "vídeo", "música"],
        ["security"] = ["security", "antivirus", "privacy", "segurança", "antivírus", "privacidade"],
    };

    public ObservableCollection<AppEntry> Apps { get; } = [];

    public ObservableCollection<AppEntry> StoreBannerApps { get; } = [];
    public ObservableCollection<AppEntry> SideStoreBannerApps { get; } = [];
    public ObservableCollection<AppEntry> PopularApps { get; } = [];
    public ObservableCollection<AppEntry> StoreHighlights { get; } = [];

    public static readonly DependencyProperty AppsColumnsProperty =
        DependencyProperty.Register(nameof(AppsColumns), typeof(int), typeof(HomePage), new PropertyMetadata(2));

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

        // Define o contexto de dados para a vitrine e os resultados de busca.
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
        UpdateStorefrontLayout(e.NewSize.Width);
    }

    private void UpdateStorefrontLayout(double width)
    {
        bool compact = width < 1050;
        bool showPromotions = !compact && RightStorePromo.DataContext is AppEntry;
        HeroPromotionsColumn.Width = !showPromotions ? new GridLength(0) : new GridLength(1, GridUnitType.Star);
        HeroGapColumn.Width = new GridLength(showPromotions ? 12 : 0);
        HeroPromotions.Visibility = showPromotions ? Visibility.Visible : Visibility.Collapsed;
        StorefrontHeroGrid.Height = width < 700 ? 300 : compact ? 360 : 440;
        StorefrontShelves.Columns = compact || _shelfView != ShelfView.Overview ? 1 : 2;
        AppsColumns = width < 700 ? 1 : width < 1100 ? 2 : 3;
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
        PopularApps.Clear();
        StoreHighlights.Clear();
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
            // Prefer the catalog already loaded by the app; fetch only when it lacks editorial art.
            List<AppEntry> products = _allApps.Where(app => !string.IsNullOrWhiteSpace(app.StoreBannerUrl)).ToList();
            if (products.Count < 4)
            {
                try
                {
                    products.AddRange(await _msStoreCatalogService.FetchAsync(productIds));
                }
                catch (Exception ex)
                {
                    System.Diagnostics.Debug.WriteLine($"[HomePage] Mantendo banners do catálogo local: {ex.Message}");
                }
            }
            var ordered = products.Where(app => !string.IsNullOrWhiteSpace(app.StoreBannerUrl))
                .DistinctBy(app => $"{app.Source}:{app.Id}", StringComparer.OrdinalIgnoreCase)
                .OrderByDescending(app => app.Score).Take(6).ToList();
            SyncInstalledFlags(ordered);

            StoreBannerApps.Clear();
            SideStoreBannerApps.Clear();
            foreach (AppEntry app in ordered.Take(3))
            {
                StoreBannerApps.Add(app);
            }

            RightStorePromo.DataContext = ordered.Skip(1).FirstOrDefault();
            RightStorePromo.Visibility = RightStorePromo.DataContext is AppEntry ? Visibility.Visible : Visibility.Collapsed;
            HeroPromotions.Visibility = ActualWidth >= 1050 && RightStorePromo.DataContext is AppEntry ? Visibility.Visible : Visibility.Collapsed;

            foreach (AppEntry app in ordered.Skip(2).Take(2))
            {
                SideStoreBannerApps.Add(app);
            }

            if (StoreBannerApps.Count > 0)
            {
                SetHeroBanner(0);
            }

            UpdateStorefrontLayout(ActualWidth);
            _storeBannersLoaded = StoreBannerApps.Count > 0;
            UpdatePopularApps();
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
        HeroDetailsButton.IsEnabled = true;
        HeroBanner.Visibility = Visibility.Visible;
        HeroBannerDot0.Visibility = StoreBannerApps.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        HeroBannerDot1.Visibility = StoreBannerApps.Count > 1 ? Visibility.Visible : Visibility.Collapsed;
        HeroBannerDot2.Visibility = StoreBannerApps.Count > 2 ? Visibility.Visible : Visibility.Collapsed;
        HeroBannerPreviousButton.IsEnabled = HeroBannerNextButton.IsEnabled = StoreBannerApps.Count > 1;
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

    private static bool MatchesLocalRegion(AppEntry app) =>
        app.RegionTags.Count == 0 || app.RegionTags.Contains("BR", StringComparer.OrdinalIgnoreCase);

    private static bool HasKnownPtBrSupport(AppEntry app) =>
        string.Equals(app.PackageLocale, "pt-BR", StringComparison.OrdinalIgnoreCase);

    private static bool MatchesCategory(AppEntry app, string[] keywords)
    {
        IEnumerable<string> metadata = app.Tags
            .Concat([app.StoreCategory, app.StoreSubCategory])
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .Select(value => value!);

        return metadata.Any(value => keywords.Any(keyword =>
            value.Contains(keyword, StringComparison.OrdinalIgnoreCase)));
    }

    private void UpdatePopularApps()
    {
        int limit = _shelfView == ShelfView.Popular ? 48 : 6;
        PopularApps.Clear();
        foreach (AppEntry app in _allApps
                     .Where(app => app.Source.Equals("winget", StringComparison.OrdinalIgnoreCase)
                                   && HasRealIcon(app) && HasKnownPtBrSupport(app)
                                   && app.Score >= 25 && MatchesLocalRegion(app))
                     .OrderByDescending(app => app.Score)
                     .ThenByDescending(app => app.GitHubStars ?? 0)
                     .Take(limit))
            PopularApps.Add(app);

        limit = _shelfView == ShelfView.Store ? 48 : 6;
        StoreHighlights.Clear();
        foreach (AppEntry app in _allApps.Concat(StoreBannerApps)
                     .Where(app => app.Source.Equals("msstore", StringComparison.OrdinalIgnoreCase)
                                   && HasRealIcon(app) && HasKnownPtBrSupport(app) && MatchesLocalRegion(app))
                     .DistinctBy(app => app.Id, StringComparer.OrdinalIgnoreCase)
                     .OrderByDescending(GetStoreRatingScore)
                     .ThenByDescending(app => app.StoreRatingCount ?? 0)
                     .ThenByDescending(app => app.Score)
                     .Take(limit))
            StoreHighlights.Add(app);
    }

    private static double GetStoreRatingScore(AppEntry app) =>
        app.StoreRating is > 0 && app.StoreRatingCount is > 0
            ? ((app.StoreRating.Value * app.StoreRatingCount.Value) + 4000.0)
                / (app.StoreRatingCount.Value + 1000.0)
            : 0;

    private void PopularMoreButton_Click(object sender, RoutedEventArgs e) =>
        SetShelfView(_shelfView == ShelfView.Popular ? ShelfView.Overview : ShelfView.Popular);

    private void StoreMoreButton_Click(object sender, RoutedEventArgs e) =>
        SetShelfView(_shelfView == ShelfView.Store ? ShelfView.Overview : ShelfView.Store);

    private void SetShelfView(ShelfView view)
    {
        _shelfView = view;
        PopularShelfCard.Visibility = view == ShelfView.Store ? Visibility.Collapsed : Visibility.Visible;
        StoreShelfCard.Visibility = view == ShelfView.Popular ? Visibility.Collapsed : Visibility.Visible;
        PopularMoreIcon.Symbol = view == ShelfView.Popular ? Wpf.Ui.Controls.SymbolRegular.ChevronLeft24 : Wpf.Ui.Controls.SymbolRegular.ChevronRight24;
        StoreMoreIcon.Symbol = view == ShelfView.Store ? Wpf.Ui.Controls.SymbolRegular.ChevronLeft24 : Wpf.Ui.Controls.SymbolRegular.ChevronRight24;
        string popularAction = view == ShelfView.Popular
            ? "Voltar à página inicial" : "Ver mais aplicativos populares";
        string storeAction = view == ShelfView.Store
            ? "Voltar à página inicial" : "Ver mais aplicativos da Microsoft Store";
        PopularMoreButton.ToolTip = popularAction;
        StoreMoreButton.ToolTip = storeAction;
        System.Windows.Automation.AutomationProperties.SetName(PopularMoreButton, popularAction);
        System.Windows.Automation.AutomationProperties.SetName(StoreMoreButton, storeAction);
        UpdateStorefrontLayout(ActualWidth);
        UpdatePopularApps();
        ApplyFilter();
    }

    private void ApplyFilter()
    {
        if (!_catalogLoaded)
            return;

        string query = SearchBox.Text?.Trim() ?? string.Empty;
        bool browsing = query.Length == 0 && _selectedCategoryTag == "all";
        StorefrontHeroGrid.Visibility = browsing && _shelfView == ShelfView.Overview ? Visibility.Visible : Visibility.Collapsed;
        StorefrontShelves.Visibility = browsing ? Visibility.Visible : Visibility.Collapsed;
        ResultsPanel.Visibility = ResultsStatusBar.Visibility = browsing ? Visibility.Collapsed : Visibility.Visible;
        ResultsTitle.Text = query.Length > 0 ? "Resultados da pesquisa" : "Aplicativos da categoria";

        if (string.IsNullOrWhiteSpace(query))
        {
            _searchCancellation?.Cancel();
            _searchCancellation = null;
            _searchQueryInFlight = null;
            _liveSearchQuery = null;
            _liveSearchResults = [];
        }

        if (browsing)
        {
            Apps.Clear();
            return;
        }

        IEnumerable<AppEntry> source = string.IsNullOrWhiteSpace(query)
            ? _shelfView == ShelfView.Store
                ? _allApps.Concat(StoreBannerApps)
                    .Where(app => app.Source.Equals("msstore", StringComparison.OrdinalIgnoreCase)
                                  && HasRealIcon(app) && HasKnownPtBrSupport(app) && MatchesLocalRegion(app))
                    .DistinctBy(app => app.Id, StringComparer.OrdinalIgnoreCase)
                    .OrderByDescending(GetStoreRatingScore)
                    .ThenByDescending(app => app.StoreRatingCount ?? 0)
                : _allApps.Where(app => app.Source.Equals("winget", StringComparison.OrdinalIgnoreCase)
                                             && HasRealIcon(app) && HasKnownPtBrSupport(app)
                                             && app.Score >= 25 && MatchesLocalRegion(app))
                    .OrderByDescending(app => app.Score)
                    .ThenByDescending(app => app.GitHubStars ?? 0)
            : GetSearchResults(query);

        if (_selectedCategoryTag != "all" && CategoryTagMap.TryGetValue(_selectedCategoryTag, out string[]? keywords))
            source = source.Where(app => MatchesCategory(app, keywords));

        if (_catalogSortByName)
            source = source.OrderBy(app => app.Name, StringComparer.CurrentCultureIgnoreCase);

        List<AppEntry> results = source.Take(48).ToList();
        Apps.Clear();
        foreach (AppEntry app in results)
            Apps.Add(app);

        StatusText.Text = string.IsNullOrWhiteSpace(query)
            ? $"{results.Count} aplicativos nesta categoria."
            : results.Count == 0
                ? _liveSearchQuery == query ? $"Nenhum resultado para \"{query}\"" : $"Pesquisando fontes de pacotes por \"{query}\"..."
                : results.Count == 1 ? "1 resultado." : $"Resultados: {results.Count}";

        if (!string.IsNullOrWhiteSpace(query) &&
            !string.Equals(_liveSearchQuery, query, StringComparison.OrdinalIgnoreCase) &&
            !string.Equals(_searchQueryInFlight, query, StringComparison.OrdinalIgnoreCase))
            _ = SearchLiveCatalogsAsync(query);
    }

    private IEnumerable<AppEntry> GetSearchResults(string query)
    {
        // Search retorna a lista já ranqueada e é lazy; materializar uma vez evita
        // recalcular o score e ordenar o catálogo de novo no Take(30)/Skip(30).
        List<AppEntry> localResults = _storeService.Search(query).ToList();
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
                if (Apps.Count == 0)
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

    private void StorefrontApp_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: AppEntry app })
            _detailsOverlayService.Show(app);
        e.Handled = true;
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
