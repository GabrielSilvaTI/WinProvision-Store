using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Globalization;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
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
    private readonly DispatcherTimer _heroBannerTimer;
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
    private DateOnly? _storeBannerWeek;
    private DateOnly? _storeBannerSearchWeek;
    private List<AppEntry> _searchedStoreBannerApps = [];
    private int _heroBannerIndex;
    private string _selectedCategoryTag = "all";
    private CatalogSortMode _catalogSortMode;
    private ShelfView _shelfView;

    private enum ShelfView { Overview, Popular, Store }
    private enum CatalogSortMode { Popularity, Name, Id, MicrosoftStore, WinGet }

    private static readonly Dictionary<string, string[]> CategoryTagMap = new(StringComparer.OrdinalIgnoreCase)
    {
        ["productivity"] = ["productivity", "produtividade", "office", "business", "negócios"],
        ["development"] = ["development", "developer", "programming", "desenvolvimento", "programação"],
        ["utilities"] = ["utilities", "tools", "system", "utilitários", "ferramentas", "sistema"],
        ["multimedia"] = ["multimedia", "photo", "video", "music", "multimídia", "foto", "vídeo", "música"],
        ["security"] = ["security", "antivirus", "privacy", "segurança", "antivírus", "privacidade"],
    };

    // Apenas marcas conhecidas entram nos banners; a lista não limita o catálogo.
    private static readonly string[] FamiliarStoreAppNames =
    [
        "WhatsApp", "Spotify", "Disney+", "Netflix", "TikTok", "CapCut", "Instagram",
        "Facebook", "Messenger", "Telegram", "Prime Video", "Amazon Prime Video", "Canva",
        "Zoom", "VLC", "Microsoft 365", "OneDrive", "Xbox", "PowerToys", "Lively Wallpaper",
        "TranslucentTB", "Dolby Access", "Microsoft To Do", "Phone Link", "Clipchamp",
        "Microsoft Photos", "Windows Terminal", "Paint", "Word", "Excel", "PowerPoint", "Outlook",
        "Adobe", "Pinterest", "Discord", "Brave", "ChatGPT"
    ];

    private static readonly string[] FeaturedStoreSearchTerms =
    [
        "Disney+", "Spotify", "WhatsApp", "Adobe", "TikTok", "Netflix", "Instagram",
        "Canva", "Pinterest", "Discord", "Brave", "ChatGPT", "Telegram"
    ];

    private const double StoreFeaturedMinimumRating = 4.0;
    private const int StoreFeaturedMinimumRatingCount = 3000;
    private const int StoreFeaturedCandidatePoolSize = 30;

    public ObservableCollection<AppEntry> Apps { get; } = [];

    public ObservableCollection<AppEntry> StoreBannerApps { get; } = [];
    public ObservableCollection<AppEntry> SideStoreBannerApps { get; } = [];
    public ObservableCollection<AppEntry> PopularApps { get; } = [];
    public ObservableCollection<AppEntry> StoreHighlights { get; } = [];

    public static readonly DependencyProperty AppsColumnsProperty =
        DependencyProperty.Register(nameof(AppsColumns), typeof(int), typeof(HomePage), new PropertyMetadata(2));

    public static readonly DependencyProperty ShelfColumnsProperty =
        DependencyProperty.Register(nameof(ShelfColumns), typeof(int), typeof(HomePage), new PropertyMetadata(2));

    public int AppsColumns
    {
        get => (int)GetValue(AppsColumnsProperty);
        set => SetValue(AppsColumnsProperty, value);
    }

    public int ShelfColumns
    {
        get => (int)GetValue(ShelfColumnsProperty);
        set => SetValue(ShelfColumnsProperty, value);
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

        _heroBannerTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(8) };
        _heroBannerTimer.Tick += (_, _) =>
        {
            if (StoreBannerApps.Count > 1)
                SetHeroBanner(_heroBannerIndex + 1);
            else
                _heroBannerTimer.Stop();
        };

        BuildCategoryChips();

        _storeService.CatalogUpdated += OnCatalogUpdated;
        _storeService.CacheCleared += OnCacheCleared;
        _installedAppsService.Changed += OnInstalledAppsChanged;
        Loaded += HomePage_Loaded;
        Unloaded += (_, _) => _heroBannerTimer.Stop();
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
        ShelfColumns = width < 760 ? 1 : 2;
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
        UpdateCatalogSyncText();
        SyncInstalledFlags(_allApps);
        _ = LoadStoreBannersAsync(force: true);
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
        CatalogSyncText.Text = "Catálogo ainda não sincronizado nesta sessão.";
        _allApps = [];
        _storeBannersLoaded = false;
        _storeBannerWeek = null;
        _storeBannerSearchWeek = null;
        _searchedStoreBannerApps = [];
        StoreBannerApps.Clear();
        SideStoreBannerApps.Clear();
        RightStorePromo.DataContext = null;
        HeroBanner.Visibility = Visibility.Collapsed;
        HeroPromotions.Visibility = Visibility.Collapsed;
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
        if (_catalogLoaded)
        {
            await LoadStoreBannersAsync();
            UpdateBannerAutoAdvance();
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
            UpdateCatalogSyncText();
            await _installedAppsService.EnsureLoadedAsync();
            SyncInstalledFlags(_allApps);
            await LoadStoreBannersAsync();
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
        _catalogSortMode = CatalogSortComboBox.SelectedIndex switch
        {
            1 => CatalogSortMode.Name,
            2 => CatalogSortMode.Id,
            3 => CatalogSortMode.MicrosoftStore,
            4 => CatalogSortMode.WinGet,
            _ => CatalogSortMode.Popularity,
        };
        ApplyFilter();
    }

    private static DateOnly GetCurrentBannerWeek()
    {
        DateOnly today = DateOnly.FromDateTime(DateTime.Now);
        int daysSinceMonday = ((int)today.DayOfWeek + 6) % 7;
        return today.AddDays(-daysSinceMonday);
    }

    private static string GetWeeklyBannerOrderKey(AppEntry app, DateOnly week)
    {
        string key = $"{week:yyyy-MM-dd}:{app.Source}:{app.Id}";
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(key)));
    }

    private async Task LoadStoreBannersAsync(bool force = false)
    {
        DateOnly week = GetCurrentBannerWeek();
        if (_storeBannersLoading || (!force && _storeBannersLoaded && _storeBannerWeek == week))
        {
            return;
        }

        _storeBannersLoading = true;
        try
        {
            List<AppEntry> availableStoreApps = _allApps
                .Where(IsEligibleFeaturedStoreApp)
                .ToList();

            // A busca ampla do catálogo pode não conter todos os produtos editoriais.
            // Se ainda faltarem banners, consulta diretamente os títulos conhecidos.
            if (availableStoreApps.Select(GetProductIdentityKey).Distinct(StringComparer.OrdinalIgnoreCase).Count() < 6)
            {
                if (_storeBannerSearchWeek != week)
                {
                    var discovered = new System.Collections.Concurrent.ConcurrentBag<AppEntry>();
                    await Parallel.ForEachAsync(
                        FeaturedStoreSearchTerms,
                        new ParallelOptions { MaxDegreeOfParallelism = 3 },
                        async (term, cancellationToken) =>
                        {
                            foreach (AppEntry app in await _msStoreCatalogService.SearchAsync(term, cancellationToken))
                            {
                                if (IsEligibleFeaturedStoreApp(app))
                                {
                                    app.IconUrl = _iconService.ResolveIconUrl(app);
                                    app.IsInstalled = _installedAppsService.IsInstalled(app.Name, app.Id);
                                    discovered.Add(app);
                                }
                            }
                        });
                    _searchedStoreBannerApps = discovered.ToList();
                    _storeBannerSearchWeek = week;
                }

                availableStoreApps.AddRange(_searchedStoreBannerApps);
            }

            // A Store não fornece downloads no catálogo; o volume de avaliações serve
            // como sinal de popularidade. Primeiro limitamos aos produtos com nota alta,
            // volume mínimo de avaliações e banner. A rotação semanal escolhe entre os
            // 30 mais populares, sem completar a vitrine com apps fora dos critérios.
            List<AppEntry> candidatePool = availableStoreApps
                .DistinctBy(GetProductIdentityKey, StringComparer.OrdinalIgnoreCase)
                .OrderByDescending(app => app.StoreRatingCount ?? 0)
                .ThenByDescending(app => app.StoreRating ?? 0)
                .ThenByDescending(app => app.Score)
                .Take(StoreFeaturedCandidatePoolSize)
                .ToList();

            List<AppEntry> featured = candidatePool
                .OrderBy(app => GetWeeklyBannerOrderKey(app, week), StringComparer.Ordinal)
                .Take(6)
                .ToList();

            SyncInstalledFlags(featured);
            StoreBannerApps.Clear();
            SideStoreBannerApps.Clear();
            foreach (AppEntry app in featured.Take(3))
            {
                StoreBannerApps.Add(app);
            }

            // Distribui os destaques sem repetir o mesmo produto em mais de um espaço.
            RightStorePromo.DataContext = featured.Skip(3).FirstOrDefault();
            RightStorePromo.Visibility = RightStorePromo.DataContext is AppEntry ? Visibility.Visible : Visibility.Collapsed;
            HeroPromotions.Visibility = ActualWidth >= 1050 && RightStorePromo.DataContext is AppEntry ? Visibility.Visible : Visibility.Collapsed;

            foreach (AppEntry app in featured.Skip(4).Take(2))
            {
                SideStoreBannerApps.Add(app);
            }

            if (StoreBannerApps.Count > 0)
            {
                SetHeroBanner(0);
            }
            else
            {
                HeroBanner.Visibility = Visibility.Collapsed;
            }

            UpdateBannerAutoAdvance();
            UpdateStorefrontLayout(ActualWidth);
            _storeBannerWeek = week;
            _storeBannersLoaded = true;
            UpdatePopularApps();
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[HomePage] Falha ao carregar banners do catálogo: {ex.Message}");
        }
        finally
        {
            _storeBannersLoading = false;
        }
    }

    private static bool IsEligibleFeaturedStoreApp(AppEntry app) =>
        app.Source.Equals("msstore", StringComparison.OrdinalIgnoreCase)
        && !string.IsNullOrWhiteSpace(app.StoreBannerUrl)
        && IsFamiliarStoreApp(app)
        && app.StoreRating is > StoreFeaturedMinimumRating and <= 5
        && app.StoreRatingCount >= StoreFeaturedMinimumRatingCount;

    private void UpdateBannerAutoAdvance()
    {
        if (StoreBannerApps.Count > 1 && IsLoaded)
            _heroBannerTimer.Start();
        else
            _heroBannerTimer.Stop();
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

    private static bool IsFamiliarStoreApp(AppEntry app) =>
        FamiliarStoreAppNames.Any(knownName =>
            app.Name.Equals(knownName, StringComparison.OrdinalIgnoreCase)
            || app.Name.StartsWith(knownName + " - ", StringComparison.OrdinalIgnoreCase)
            || app.Name.StartsWith(knownName + " ", StringComparison.OrdinalIgnoreCase));

    private static string NormalizeSearchText(string? value) => string.Concat((value ?? string.Empty)
        .Normalize(NormalizationForm.FormD)
        .Where(character => CharUnicodeInfo.GetUnicodeCategory(character) != UnicodeCategory.NonSpacingMark)
        .Where(char.IsLetterOrDigit)
        .Select(char.ToLowerInvariant));

    private static string? GetFamiliarProductKey(string? name)
    {
        foreach (string knownName in FamiliarStoreAppNames)
        {
            if (string.Equals(name, knownName, StringComparison.OrdinalIgnoreCase)
                || name?.StartsWith(knownName + " - ", StringComparison.OrdinalIgnoreCase) == true
                || name?.StartsWith(knownName + ": ", StringComparison.OrdinalIgnoreCase) == true
                || name?.StartsWith(knownName + " — ", StringComparison.OrdinalIgnoreCase) == true)
            {
                return $"known\0{NormalizeSearchText(knownName)}";
            }
        }

        return null;
    }

    private static string GetProductIdentityKey(AppEntry app)
    {
        // Títulos editoriais da Store, como "Spotify - Música e podcasts", não
        // precisam repetir o mesmo produto já disponível pelo WinGet. Para marcas
        // conhecidas, a identidade usa o nome canônico; nos demais casos, mantém a
        // combinação nome + publicador, que evita juntar aplicativos diferentes.
        if (GetFamiliarProductKey(app.Name) is { } familiarKey)
            return familiarKey;

        string name = NormalizeSearchText(app.Name);
        string publisher = NormalizeSearchText(app.Publisher);
        return name.Length == 0 || publisher.Length == 0
            ? $"{app.Source}\0{app.Id}"
            : $"{name}\0{publisher}";
    }

    private static AppEntry MergeProductSources(IEnumerable<AppEntry> entries)
    {
        List<AppEntry> group = entries.ToList();
        AppEntry primary = group.FirstOrDefault(app => app.Source.Equals("winget", StringComparison.OrdinalIgnoreCase))
                           ?? group[0];
        AppEntry? store = group.FirstOrDefault(app => app.Source.Equals("msstore", StringComparison.OrdinalIgnoreCase));
        if (store is null || ReferenceEquals(primary, store))
            return primary;

        // Mantém o ID e a origem de instalação WinGet, mas usa as mídias oficiais da Store.
        if (!string.IsNullOrWhiteSpace(store.StoreBannerUrl))
            primary.StoreBannerUrl = store.StoreBannerUrl;
        if (store.StoreScreenshotUrls is { Count: > 0 })
            primary.StoreScreenshotUrls = store.StoreScreenshotUrls;
        if (!string.IsNullOrWhiteSpace(store.StoreIconUrl))
        {
            primary.StoreIconUrl = store.StoreIconUrl;
            primary.IconUrl = store.StoreIconUrl;
        }
        else if (!string.IsNullOrWhiteSpace(store.IconUrl))
        {
            // Resultados ao vivo podem já vir com o ícone da Store resolvido em
            // IconUrl. Ele continua sendo preferível ao ícone genérico do manifesto.
            primary.IconUrl = store.IconUrl;
        }
        primary.StoreRating ??= store.StoreRating;
        primary.StoreRatingCount ??= store.StoreRatingCount;
        primary.StoreCategory ??= store.StoreCategory;
        primary.StoreSubCategory ??= store.StoreSubCategory;
        primary.Description = string.IsNullOrWhiteSpace(store.Description) ? primary.Description : store.Description;
        primary.PackageLocale ??= store.PackageLocale;
        return primary;
    }

    private static IEnumerable<AppEntry> MergeProductEntries(IEnumerable<AppEntry> entries) =>
        entries.GroupBy(GetProductIdentityKey, StringComparer.OrdinalIgnoreCase)
            .Select(MergeProductSources);

    private static IEnumerable<AppEntry> InterleaveCatalogSources(IEnumerable<AppEntry> entries)
    {
        List<AppEntry> wingetApps = entries
            .Where(app => app.Source.Equals("winget", StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(app => app.Score)
            .ThenByDescending(app => app.GitHubStars ?? 0)
            .ToList();
        List<AppEntry> storeApps = entries
            .Where(app => app.Source.Equals("msstore", StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(GetStoreRatingScore)
            .ThenByDescending(app => app.StoreRatingCount ?? 0)
            .ToList();

        for (int index = 0; index < Math.Max(wingetApps.Count, storeApps.Count); index++)
        {
            if (index < wingetApps.Count)
                yield return wingetApps[index];
            if (index < storeApps.Count)
                yield return storeApps[index];
        }
    }

    private static bool MatchesCategory(AppEntry app, string[] keywords)
    {
        IEnumerable<string> metadata = app.Tags
            .Concat([app.StoreCategory, app.StoreSubCategory])
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .Select(value => value!);

        return metadata.Any(value => keywords.Any(keyword =>
            value.Contains(keyword, StringComparison.OrdinalIgnoreCase)));
    }

    private const int StorePopularMinimumRatingCount = 50;
    private const int StorePopularFallbackRatingCount = 5;
    private const double StorePopularMinimumRating = 4.0;

    private void UpdatePopularApps()
    {
        int limit = _shelfView == ShelfView.Popular ? 48 : 6;
        List<AppEntry> selectedStoreApps = SelectPopularStoreApps(_allApps.Concat(StoreBannerApps),
            _shelfView == ShelfView.Store ? 48 : 6);
        HashSet<string> storeProductKeys = selectedStoreApps
            .Select(GetProductIdentityKey)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        PopularApps.Clear();
        foreach (AppEntry app in _allApps
                     .Where(app => app.Source.Equals("winget", StringComparison.OrdinalIgnoreCase)
                                   && HasRealIcon(app) && HasKnownPtBrSupport(app)
                                   && app.Score >= 25 && MatchesLocalRegion(app)
                                   && !storeProductKeys.Contains(GetProductIdentityKey(app)))
                     .OrderByDescending(app => app.Score)
                     .ThenByDescending(app => app.GitHubStars ?? 0)
                     .Take(limit))
            PopularApps.Add(app);

        limit = _shelfView == ShelfView.Store ? 48 : 6;
        StoreHighlights.Clear();
        foreach (AppEntry app in selectedStoreApps.Take(limit))
            StoreHighlights.Add(app);
    }

    private static List<AppEntry> SelectPopularStoreApps(IEnumerable<AppEntry> apps, int limit)
    {
        if (limit <= 0)
            return [];

        List<AppEntry> ratedApps = apps
            .Where(app => app.Source.Equals("msstore", StringComparison.OrdinalIgnoreCase)
                          && HasRealIcon(app) && HasKnownPtBrSupport(app) && MatchesLocalRegion(app)
                          && app.StoreRating is >= StorePopularMinimumRating and <= 5
                          && app.StoreRatingCount is > 0)
            .DistinctBy(GetProductIdentityKey, StringComparer.OrdinalIgnoreCase)
            .OrderByDescending(IsFamiliarStoreApp)
            .ThenByDescending(GetStoreRatingScore)
            .ThenByDescending(app => app.StoreRatingCount ?? 0)
            .ThenByDescending(app => app.Score)
            .ThenBy(app => app.Name, StringComparer.CurrentCultureIgnoreCase)
            .ToList();

        // Prioriza produtos bem avaliados com volume consistente de opiniões. Se
        // necessário, completa com os que têm menos avaliações, sem dados ausentes.
        List<AppEntry> popularApps = ratedApps
            .Where(app => app.StoreRatingCount >= StorePopularMinimumRatingCount)
            .Take(limit)
            .ToList();

        if (popularApps.Count < limit)
        {
            var selectedIds = popularApps.Select(app => app.Id)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
            popularApps.AddRange(ratedApps
                .Where(app => app.StoreRatingCount >= StorePopularFallbackRatingCount
                              && !selectedIds.Contains(app.Id))
                .Take(limit - popularApps.Count));
        }

        return popularApps;
    }

    private static double GetStoreRatingScore(AppEntry app) =>
        app.StoreRating is > 0 and <= 5 && app.StoreRatingCount is > 0
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
            ? "Voltar à página inicial" : "Ver mais aplicativos populares da Microsoft Store";
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

        bool categoryBrowsing = string.IsNullOrWhiteSpace(query) && _selectedCategoryTag != "all";
        IEnumerable<AppEntry> source = categoryBrowsing
            ? InterleaveCatalogSources(MergeProductEntries(_allApps.Concat(StoreBannerApps)
                .Where(app =>
                    MatchesSelectedCatalogSource(app)
                    && ((app.Source.Equals("winget", StringComparison.OrdinalIgnoreCase)
                         && HasRealIcon(app) && HasKnownPtBrSupport(app)
                         && app.Score >= 25 && MatchesLocalRegion(app))
                        || (app.Source.Equals("msstore", StringComparison.OrdinalIgnoreCase)
                            && HasRealIcon(app) && HasKnownPtBrSupport(app)
                            && MatchesLocalRegion(app))))
                .Where(app => CategoryTagMap.TryGetValue(_selectedCategoryTag, out string[]? keywords)
                              && MatchesCategory(app, keywords))))
            : string.IsNullOrWhiteSpace(query)
            ? _shelfView == ShelfView.Store
                ? SelectPopularStoreApps(_allApps.Concat(StoreBannerApps), 48)
                : _allApps.Where(app => app.Source.Equals("winget", StringComparison.OrdinalIgnoreCase)
                                             && HasRealIcon(app) && HasKnownPtBrSupport(app)
                                             && app.Score >= 25 && MatchesLocalRegion(app))
                    .OrderByDescending(app => app.Score)
                    .ThenByDescending(app => app.GitHubStars ?? 0)
            : GetSearchResults(query);

        if (_selectedCategoryTag != "all" && CategoryTagMap.TryGetValue(_selectedCategoryTag, out string[]? keywords))
            source = source.Where(app => MatchesCategory(app, keywords));

        source = _catalogSortMode switch
        {
            CatalogSortMode.Name => source.OrderBy(app => app.Name, StringComparer.CurrentCultureIgnoreCase),
            CatalogSortMode.Id => source.OrderBy(app => app.Id, StringComparer.OrdinalIgnoreCase),
            _ => source,
        };

        List<AppEntry> results = source.Take(48).ToList();
        Apps.Clear();
        foreach (AppEntry app in results)
            Apps.Add(app);

        bool searchCompleted = string.IsNullOrWhiteSpace(query)
            || string.Equals(_liveSearchQuery, query, StringComparison.OrdinalIgnoreCase);
        EmptySearchResultPanel.Visibility = results.Count == 0 && searchCompleted
            ? Visibility.Visible
            : Visibility.Collapsed;

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
        // Une o catálogo local e as duas fontes consultadas ao vivo antes de limitar a
        // quantidade. A API da Store pode retornar correspondências aproximadas; sem
        // um ranking comum, elas podiam ocupar as primeiras posições e esconder um
        // resultado exato como WhatsApp quando pesquisado pelo nome.
        List<AppEntry> localResults = _storeService.Search(query).ToList();
        IEnumerable<AppEntry> liveResults = string.Equals(_liveSearchQuery, query, StringComparison.OrdinalIgnoreCase)
            ? _liveSearchResults
            : [];

        return localResults.Concat(liveResults)
            .Where(MatchesSelectedCatalogSource)
            .GroupBy(GetProductIdentityKey, StringComparer.OrdinalIgnoreCase)
            .Select(group =>
            {
                List<AppEntry> sourceEntries = group.ToList();
                return (App: MergeProductSources(sourceEntries),
                    Relevance: sourceEntries.Min(app => GetSearchRelevance(app, query)));
            })
            .Where(result => result.Relevance < int.MaxValue)
            .OrderBy(result => result.Relevance)
            .ThenBy(result => result.App.Name, StringComparer.CurrentCultureIgnoreCase)
            .Select(result => result.App);
    }

    private bool MatchesSelectedCatalogSource(AppEntry app) => _catalogSortMode switch
    {
        CatalogSortMode.MicrosoftStore => app.Source.Equals("msstore", StringComparison.OrdinalIgnoreCase),
        CatalogSortMode.WinGet => app.Source.Equals("winget", StringComparison.OrdinalIgnoreCase),
        _ => true,
    };

    private static int GetSearchRelevance(AppEntry app, string query)
    {
        string name = app.Name ?? string.Empty;
        string id = app.Id ?? string.Empty;
        string term = query.Trim();
        if (term.Length == 0)
            return int.MaxValue;

        string normalizedTerm = NormalizeSearchText(term);
        string normalizedName = NormalizeSearchText(name);
        string normalizedId = NormalizeSearchText(id);
        if (normalizedTerm.Length == 0)
            return int.MaxValue;

        if (normalizedName.Equals(normalizedTerm, StringComparison.Ordinal)) return 0;
        if (normalizedId.Equals(normalizedTerm, StringComparison.Ordinal)) return 1;
        if (normalizedName.StartsWith(normalizedTerm, StringComparison.Ordinal)) return 2;
        if (name.Split([' ', '.', '-', '_'], StringSplitOptions.RemoveEmptyEntries)
            .Any(token => NormalizeSearchText(token).StartsWith(normalizedTerm, StringComparison.Ordinal))) return 3;
        if (normalizedName.Contains(normalizedTerm, StringComparison.Ordinal)) return 4;
        if (normalizedId.StartsWith(normalizedTerm, StringComparison.Ordinal)) return 5;
        if (normalizedId.Contains(normalizedTerm, StringComparison.Ordinal)) return 6;
        if ((app.Tags ?? []).Any(tag => NormalizeSearchText(tag).Contains(normalizedTerm, StringComparison.Ordinal))) return 7;
        if (NormalizeSearchText(app.Publisher).Contains(normalizedTerm, StringComparison.Ordinal)) return 8;
        if (NormalizeSearchText(app.Description).Contains(normalizedTerm, StringComparison.Ordinal)) return 9;

        return int.MaxValue;
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

            IReadOnlyList<AppEntry> resolvedStoreEntries = [];
            if (storeIds.Length > 0)
            {
                try
                {
                    resolvedStoreEntries = await _msStoreCatalogService.FetchAsync(storeIds, cancellationToken);
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    // A busca direta da Store já trouxe resultados úteis. Falhar ao
                    // enriquecer IDs retornados pelo WinGet não deve descartá-los.
                    System.Diagnostics.Debug.WriteLine($"[HomePage] Falha ao enriquecer resultados da Store: {ex.Message}");
                }
            }
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

        _debounceTimer.Stop();
        _isRefreshing = true;
        RefreshCatalogButton.IsEnabled = false;

        try
        {
            string query = SearchBox.Text?.Trim() ?? string.Empty;
            if (query.Length > 0 && _catalogLoaded)
            {
                StatusText.Text = "Atualizando resultados da pesquisa...";
                await SearchLiveCatalogsAsync(query);
                return;
            }

            StatusText.Text = "Atualizando catálogo...";
            _allApps = await _storeService.LoadCatalogAsync(forceRefresh: true);
            UpdateCatalogSyncText();
            await _installedAppsService.RefreshAsync();
            SyncInstalledFlags(_allApps);
            await LoadStoreBannersAsync(force: true);
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

    private void UpdateCatalogSyncText()
    {
        DateTime? syncUtc = _storeService.LastCatalogSyncUtc;
        CatalogSyncText.Text = syncUtc is DateTime timestamp
            ? $"Catálogo sincronizado em {timestamp.ToLocalTime():dd/MM/yyyy HH:mm}."
            : "Data de sincronização do catálogo indisponível.";
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
