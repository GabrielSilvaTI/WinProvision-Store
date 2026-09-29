using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Win32;
using WinProvision.Core.Models;
using WinProvision.Core.Services;
using WinProvision.Store.Services;
using Wpf.Ui;
using AutoSuggestBox = Wpf.Ui.Controls.AutoSuggestBox;
using AutoSuggestBoxQuerySubmittedEventArgs = Wpf.Ui.Controls.AutoSuggestBoxQuerySubmittedEventArgs;
using AutoSuggestBoxTextChangedEventArgs = Wpf.Ui.Controls.AutoSuggestBoxTextChangedEventArgs;
using ControlAppearance = Wpf.Ui.Controls.ControlAppearance;
using SymbolIcon = Wpf.Ui.Controls.SymbolIcon;
using SymbolRegular = Wpf.Ui.Controls.SymbolRegular;

namespace WinProvision.Store;

public partial class UpdatesPage : Page
{
    private enum SortField
    {
        Name,
        Id,
        Version,
        NewVersion,
        Source
    }

    private readonly WingetExecutor _wingetExecutor;
    private readonly WinGetService _winGetService;
    private readonly StoreService _storeService;
    private readonly OperationsQueueService _queue;
    private readonly ScheduledUpdatesService _scheduledUpdatesService;
    private readonly IgnoredUpdatesService _ignoredUpdatesService;
    private readonly AppDetailsOverlayService _detailsOverlayService;
    private readonly ISnackbarService _snackbarService;

    // Coleções
    private readonly List<UpgradablePackage> _rawPackages = [];
    private readonly ObservableCollection<UpgradablePackage> _filteredPackages = [];
    private readonly ObservableCollection<IgnoredUpdateEntry> _ignoredPackages = [];

    // Estado de ordenação e filtros
    private SortField _currentSortField = SortField.Name;
    private bool _sortAscending = true;
    private bool _suppressSortModeSelectionChanged;
    private DateTime? _lastVerificationTime;
    private bool _suppressAutoUpdateToggleEvent;
    private bool _selectAllByDefault = true;
    private UpgradablePackage? _selectedPackage;

    public UpdatesPage()
    {
        InitializeComponent();

        _wingetExecutor = App.Services.GetRequiredService<WingetExecutor>();
        _winGetService = App.Services.GetRequiredService<WinGetService>();
        _storeService = App.Services.GetRequiredService<StoreService>();
        _queue = App.Services.GetRequiredService<OperationsQueueService>();
        _scheduledUpdatesService = App.Services.GetRequiredService<ScheduledUpdatesService>();
        _ignoredUpdatesService = App.Services.GetRequiredService<IgnoredUpdatesService>();
        _detailsOverlayService = App.Services.GetRequiredService<AppDetailsOverlayService>();
        _snackbarService = App.Services.GetRequiredService<ISnackbarService>();

        UpdatesTableView.ItemsSource = _filteredPackages;
        UpdatesGridView.ItemsSource = _filteredPackages;
        UpdatesIconsView.ItemsSource = _filteredPackages;
        IgnoredUpdatesModalList.ItemsSource = _ignoredPackages;

        RefreshIgnoredUpdates();

        Loaded += async (_, _) =>
        {
            await CheckUpdatesAsync();
            await LoadAutoUpdateStateAsync();
        };
    }

    // ----------------------------------------------------------------
    // Atualizações Automáticas & Opções
    // ----------------------------------------------------------------

    private async Task LoadAutoUpdateStateAsync()
    {
        _suppressAutoUpdateToggleEvent = true;
        try
        {
            bool isEnabled = await _scheduledUpdatesService.IsEnabledAsync();
            AutoUpdateSwitch.IsChecked = isEnabled;
        }
        finally
        {
            _suppressAutoUpdateToggleEvent = false;
        }
    }

    private async void AutoUpdateSwitch_Checked(object sender, RoutedEventArgs e)
    {
        if (_suppressAutoUpdateToggleEvent) return;

        AutoUpdateSwitch.IsEnabled = false;
        var result = await _scheduledUpdatesService.EnableAsync();

        if (!result.Success)
        {
            _suppressAutoUpdateToggleEvent = true;
            AutoUpdateSwitch.IsChecked = false;
            _suppressAutoUpdateToggleEvent = false;
            StatusText.Text = FormatScheduledTaskFailure("ativar", result);
        }
        else
        {
            StatusText.Text = "Atualizações automáticas ativadas (ao iniciar o PC).";
        }

        AutoUpdateSwitch.IsEnabled = true;
    }

    private async void AutoUpdateSwitch_Unchecked(object sender, RoutedEventArgs e)
    {
        if (_suppressAutoUpdateToggleEvent) return;

        AutoUpdateSwitch.IsEnabled = false;
        var result = await _scheduledUpdatesService.DisableAsync();

        if (!result.Success)
        {
            _suppressAutoUpdateToggleEvent = true;
            AutoUpdateSwitch.IsChecked = true;
            _suppressAutoUpdateToggleEvent = false;
            StatusText.Text = FormatScheduledTaskFailure("desativar", result);
        }
        else
        {
            StatusText.Text = "Atualizações automáticas desativadas.";
        }

        AutoUpdateSwitch.IsEnabled = true;
    }

    private static string FormatScheduledTaskFailure(string action, WingetExecutionResult result)
    {
        if (result.FailureReason == WingetFailureReason.ElevationCanceled)
            return $"Falha ao {action}: elevação (UAC) recusada.";

        string detail = string.IsNullOrWhiteSpace(result.Output)
            ? $"código {result.ExitCode}"
            : result.Output.Trim().Replace(Environment.NewLine, " ");
        return $"Falha ao {action}: {detail}";
    }

    private void SelectAllByDefaultSwitch_Click(object sender, RoutedEventArgs e)
    {
        _selectAllByDefault = SelectAllByDefaultSwitch.IsChecked == true;
    }

    // ----------------------------------------------------------------
    // Verificação de Atualizações (CheckUpdatesAsync)
    // ----------------------------------------------------------------

    private async void CheckUpdatesButton_Click(object sender, RoutedEventArgs e) => await CheckUpdatesAsync();

    private async Task CheckUpdatesAsync()
    {
        ReloadButton.IsEnabled = false;
        UpdateSelectedButton.IsEnabled = false;
        TopProgressBar.Visibility = Visibility.Visible;
        StatusText.Text = "Procurando atualizações disponíveis...";
        SubtitleText.Text = "Procurando atualizações disponíveis...";

        try
        {
            var upgradable = await _winGetService.GetUpgradablePackagesAsync(
                onLogReceived: line => _ = Dispatcher.InvokeAsync(() => StatusText.Text = line));

            var catalog = _storeService.GetAll();
            var catalogById = new Dictionary<string, AppEntry>(StringComparer.OrdinalIgnoreCase);
            foreach (var app in catalog)
                catalogById.TryAdd(app.Id, app);

            foreach (var package in upgradable)
            {
                if (catalogById.TryGetValue(package.Id, out var match))
                {
                    package.IconUrl = match.IconUrl;
                }
            }

            _rawPackages.Clear();
            foreach (var package in upgradable)
            {
                if (!HasNewerAvailableVersion(package))
                {
                    continue;
                }

                if (_ignoredUpdatesService.IsIgnored(package.Id, package.AvailableVersion))
                {
                    continue;
                }

                package.IsSelectedForUpdate = _selectAllByDefault;
                _rawPackages.Add(package);
            }

            _lastVerificationTime = DateTime.Now;
            RefreshSearchSuggestions();
            UpdatesInfoBar.IsOpen = false;
            ApplyFilters();
            StatusText.Text = _rawPackages.Count == 0
                ? "Nenhuma atualização disponível. Tudo em dia."
                : $"Encontradas {_rawPackages.Count} atualizações.";
        }
        catch (Exception ex)
        {
            WinProvisionLog.Write($"UPDATE UI discovery failed {ex.GetType().Name}: {ex.Message}");
            StatusText.Text = "Não foi possível verificar atualizações. Tente novamente.";
            SubtitleText.Text = "Falha ao verificar atualizações.";
            UpdatesInfoBar.Message = "Confira sua conexão e tente recarregar a lista.";
            UpdatesInfoBar.IsOpen = true;
        }
        finally
        {
            ReloadButton.IsEnabled = true;
            UpdateSelectedButton.IsEnabled = true;
            TopProgressBar.Visibility = Visibility.Collapsed;
        }
    }

    private static bool HasNewerAvailableVersion(UpgradablePackage package)
    {
        string current = package.CurrentVersion.Trim();
        string available = package.AvailableVersion.Trim();

        if (string.IsNullOrEmpty(current)
            || string.IsNullOrEmpty(available)
            || current.Equals("Unknown", StringComparison.OrdinalIgnoreCase)
            || available.Equals("Unknown", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        if (current.Equals(available, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        if (Version.TryParse(current, out Version? currentParsed)
            && Version.TryParse(available, out Version? availableParsed))
        {
            var normalizedCurrent = new Version(
                currentParsed.Major, currentParsed.Minor, Math.Max(currentParsed.Build, 0), Math.Max(currentParsed.Revision, 0));
            var normalizedAvailable = new Version(
                availableParsed.Major, availableParsed.Minor, Math.Max(availableParsed.Build, 0), Math.Max(availableParsed.Revision, 0));
            return normalizedAvailable > normalizedCurrent;
        }

        return true;
    }

    // ----------------------------------------------------------------
    // Filtragem, pesquisa e ordenação
    // ----------------------------------------------------------------

    private void ApplyFilters()
    {
        string query = SearchBox.Text?.Trim() ?? string.Empty;
        bool filterWinget = SourceWingetCheck.IsChecked == true;
        bool filterMsStore = SourceMsStoreCheck.IsChecked == true;
        bool filterOther = SourceOtherCheck.IsChecked == true;
        bool hideSameVersion = HideSameVersionCheck.IsChecked == true;

        IEnumerable<UpgradablePackage> queryable = _rawPackages;

        // Filtro de fontes
        queryable = queryable.Where(p =>
        {
            string source = (p.Source ?? string.Empty).ToLowerInvariant();
            if (source.Contains("winget")) return filterWinget;
            if (source.Contains("msstore")) return filterMsStore;
            return filterOther;
        });

        // Ocultar versões idênticas se marcado
        if (hideSameVersion)
        {
            queryable = queryable.Where(p => !string.Equals(p.CurrentVersion.Trim(), p.AvailableVersion.Trim(), StringComparison.OrdinalIgnoreCase));
        }

        // Pesquisa de texto
        if (!string.IsNullOrWhiteSpace(query))
        {
            bool exact = SearchModeExactRadio.IsChecked == true;
            bool onlyName = SearchModeNameRadio.IsChecked == true;
            bool onlyId = SearchModeIdRadio.IsChecked == true;

            queryable = queryable.Where(p =>
            {
                if (exact)
                {
                    if (onlyName) return string.Equals(p.Name, query, StringComparison.OrdinalIgnoreCase);
                    if (onlyId) return string.Equals(p.Id, query, StringComparison.OrdinalIgnoreCase);
                    return string.Equals(p.Name, query, StringComparison.OrdinalIgnoreCase) ||
                           string.Equals(p.Id, query, StringComparison.OrdinalIgnoreCase);
                }

                if (onlyName) return p.Name.Contains(query, StringComparison.OrdinalIgnoreCase);
                if (onlyId) return p.Id.Contains(query, StringComparison.OrdinalIgnoreCase);
                return p.Name.Contains(query, StringComparison.OrdinalIgnoreCase) ||
                       p.Id.Contains(query, StringComparison.OrdinalIgnoreCase);
            });
        }

        // Ordenação
        queryable = _currentSortField switch
        {
            SortField.Name => _sortAscending
                ? queryable.OrderBy(p => p.Name, StringComparer.OrdinalIgnoreCase)
                : queryable.OrderByDescending(p => p.Name, StringComparer.OrdinalIgnoreCase),
            SortField.Id => _sortAscending
                ? queryable.OrderBy(p => p.Id, StringComparer.OrdinalIgnoreCase)
                : queryable.OrderByDescending(p => p.Id, StringComparer.OrdinalIgnoreCase),
            SortField.Version => _sortAscending
                ? queryable.OrderBy(p => p.CurrentVersion, StringComparer.OrdinalIgnoreCase)
                : queryable.OrderByDescending(p => p.CurrentVersion, StringComparer.OrdinalIgnoreCase),
            SortField.NewVersion => _sortAscending
                ? queryable.OrderBy(p => p.AvailableVersion, StringComparer.OrdinalIgnoreCase)
                : queryable.OrderByDescending(p => p.AvailableVersion, StringComparer.OrdinalIgnoreCase),
            SortField.Source => _sortAscending
                ? queryable.OrderBy(p => p.SourceLabel, StringComparer.OrdinalIgnoreCase)
                : queryable.OrderByDescending(p => p.SourceLabel, StringComparer.OrdinalIgnoreCase),
            _ => queryable
        };

        var filteredList = queryable.ToList();
        _filteredPackages.Clear();
        foreach (var item in filteredList)
        {
            _filteredPackages.Add(item);
        }

        UpdateSubtitleAndCounters();
        SyncMasterCheckBoxState();

        // Estado Vazio
        bool isEmpty = _filteredPackages.Count == 0;
        EmptyStatePanel.Visibility = isEmpty ? Visibility.Visible : Visibility.Collapsed;
        TableHeaderBar.Visibility = !isEmpty && ViewModeListRadio.IsChecked == true
            ? Visibility.Visible
            : Visibility.Collapsed;
        TableScrollViewer.Visibility = isEmpty ? Visibility.Collapsed : (ViewModeListRadio.IsChecked == true ? Visibility.Visible : Visibility.Collapsed);
        GridViewScrollViewer.Visibility = isEmpty ? Visibility.Collapsed : (ViewModeGridRadio.IsChecked == true ? Visibility.Visible : Visibility.Collapsed);
        IconsViewScrollViewer.Visibility = isEmpty ? Visibility.Collapsed : (ViewModeIconsRadio.IsChecked == true ? Visibility.Visible : Visibility.Collapsed);
    }

    private void UpdateSubtitleAndCounters()
    {
        int total = _rawPackages.Count;
        int filtered = _filteredPackages.Count;
        int selected = _filteredPackages.Count(p => p.IsSelectedForUpdate);

        string lastChecked = _lastVerificationTime.HasValue
            ? _lastVerificationTime.Value.ToString("dd/MM/yyyy HH:mm:ss")
            : DateTime.Now.ToString("dd/MM/yyyy HH:mm:ss");

        SubtitleText.Text = $"{total} pacotes encontrados; {filtered} correspondem aos filtros. Última verificação: {lastChecked}";
        SelectionSummaryText.Text = $"Selecionados: {selected} · Ação: Atualizar seleção";

        bool hasSelection = selected > 0;
        UpdateSelectedButton.IsEnabled = hasSelection;
        UninstallSelectedButton.IsEnabled = hasSelection;
        IgnoreSelectedButton.IsEnabled = hasSelection;
        PackageDetailsToolbarButton.IsEnabled = _filteredPackages.Count > 0;

        ToggleSelectAllToolbarButton.IsEnabled = _filteredPackages.Count > 0;
        bool allSelected = _filteredPackages.Count > 0 && selected == _filteredPackages.Count;
        ToggleSelectAllToolbarText.Text = allSelected ? "Limpar seleção" : "Selecionar todos";
        ToggleSelectAllToolbarIcon.Symbol = allSelected
            ? Wpf.Ui.Controls.SymbolRegular.DismissCircle24
            : Wpf.Ui.Controls.SymbolRegular.CheckmarkCircle24;
    }

    private void SyncMasterCheckBoxState()
    {
        if (_filteredPackages.Count == 0)
        {
            MasterSelectCheckBox.IsChecked = false;
            return;
        }

        int selected = _filteredPackages.Count(p => p.IsSelectedForUpdate);
        if (selected == _filteredPackages.Count)
        {
            MasterSelectCheckBox.IsChecked = true;
        }
        else if (selected == 0)
        {
            MasterSelectCheckBox.IsChecked = false;
        }
        else
        {
            MasterSelectCheckBox.IsChecked = null; // Indeterminado
        }
    }

    // ----------------------------------------------------------------
    // Interações de Cabeçalho / Ordenação
    // ----------------------------------------------------------------

    private void HeaderSortName_Click(object sender, RoutedEventArgs e) => SetSort(SortField.Name);
    private void HeaderSortId_Click(object sender, RoutedEventArgs e) => SetSort(SortField.Id);
    private void HeaderSortVersion_Click(object sender, RoutedEventArgs e) => SetSort(SortField.Version);
    private void HeaderSortNewVersion_Click(object sender, RoutedEventArgs e) => SetSort(SortField.NewVersion);
    private void HeaderSortSource_Click(object sender, RoutedEventArgs e) => SetSort(SortField.Source);

    private void SetSort(SortField field)
    {
        if (_currentSortField == field)
        {
            _sortAscending = !_sortAscending;
        }
        else
        {
            _currentSortField = field;
            _sortAscending = true;
        }

        UpdateSortIcons();
        ApplyFilters();
    }

    private void UpdateSortIcons()
    {
        SortNameIcon.Visibility = _currentSortField == SortField.Name ? Visibility.Visible : Visibility.Collapsed;
        SortIdIcon.Visibility = _currentSortField == SortField.Id ? Visibility.Visible : Visibility.Collapsed;
        SortVersionIcon.Visibility = _currentSortField == SortField.Version ? Visibility.Visible : Visibility.Collapsed;
        SortNewVersionIcon.Visibility = _currentSortField == SortField.NewVersion ? Visibility.Visible : Visibility.Collapsed;
        SortSourceIcon.Visibility = _currentSortField == SortField.Source ? Visibility.Visible : Visibility.Collapsed;

        var symbol = _sortAscending ? Wpf.Ui.Controls.SymbolRegular.ArrowUp24 : Wpf.Ui.Controls.SymbolRegular.ArrowDown24;
        SortNameIcon.Symbol = symbol;
        SortIdIcon.Symbol = symbol;
        SortVersionIcon.Symbol = symbol;
        SortNewVersionIcon.Symbol = symbol;
        SortSourceIcon.Symbol = symbol;

        int selectedIndex = _currentSortField switch
        {
            SortField.Id => 1,
            SortField.Version => 2,
            SortField.NewVersion => 3,
            SortField.Source => 4,
            _ => 0
        };
        _suppressSortModeSelectionChanged = true;
        SortModeComboBox.SelectedIndex = selectedIndex;
        _suppressSortModeSelectionChanged = false;
    }

    private void SortModeComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!IsLoaded || _suppressSortModeSelectionChanged || SortModeComboBox.SelectedIndex < 0)
        {
            return;
        }

        _currentSortField = SortModeComboBox.SelectedIndex switch
        {
            1 => SortField.Id,
            2 => SortField.Version,
            3 => SortField.NewVersion,
            4 => SortField.Source,
            _ => SortField.Name
        };
        _sortAscending = true;
        UpdateSortIcons();
        ApplyFilters();
    }

    // ----------------------------------------------------------------
    // Seleção de Pacotes & Interações de Linha
    // ----------------------------------------------------------------

    private void MasterSelectCheckBox_Click(object sender, RoutedEventArgs e)
    {
        bool selectAll = MasterSelectCheckBox.IsChecked == true;
        foreach (var p in _filteredPackages)
        {
            p.IsSelectedForUpdate = selectAll;
        }
        UpdateSubtitleAndCounters();
        SyncMasterCheckBoxState();
    }

    private void ToggleSelectAllToolbarButton_Click(object sender, RoutedEventArgs e)
    {
        if (_filteredPackages.Count == 0)
            return;

        bool selectAll = _filteredPackages.Any(package => !package.IsSelectedForUpdate);
        foreach (var package in _filteredPackages)
            package.IsSelectedForUpdate = selectAll;

        UpdateSubtitleAndCounters();
        SyncMasterCheckBoxState();
    }

    private void PackageRow_Click(object sender, MouseButtonEventArgs e)
    {
        if (e.OriginalSource is DependencyObject source && FindVisualParent<Button>(source) is not null)
            return;

        if (sender is not FrameworkElement element || element.Tag is not UpgradablePackage package)
            return;

        _selectedPackage = package;

        // Se o usuário deu duplo clique, abre detalhes
        if (e.ClickCount == 2)
        {
            OpenPackageDetails(package);
            return;
        }

        package.IsSelectedForUpdate = !package.IsSelectedForUpdate;
        UpdateSubtitleAndCounters();
        SyncMasterCheckBoxState();
    }

    private void PackageRow_RightClick(object sender, MouseButtonEventArgs e)
    {
        if (sender is FrameworkElement element && element.Tag is UpgradablePackage package)
        {
            _selectedPackage = package;
        }
    }

    private static T? FindVisualParent<T>(DependencyObject? child) where T : DependencyObject
    {
        while (child is not null)
        {
            if (child is T parent) return parent;
            child = System.Windows.Media.VisualTreeHelper.GetParent(child);
        }
        return null;
    }

    // ----------------------------------------------------------------
    // Modos de Visualização (Lista / Grade / Ícones)
    // ----------------------------------------------------------------

    private void ViewModeList_Click(object sender, RoutedEventArgs e)
    {
        ViewModeListRadio.IsChecked = true;
        ViewModeGridRadio.IsChecked = false;
        ViewModeIconsRadio.IsChecked = false;
        ApplyFilters();
    }

    private void ViewModeGrid_Click(object sender, RoutedEventArgs e)
    {
        ViewModeListRadio.IsChecked = false;
        ViewModeGridRadio.IsChecked = true;
        ViewModeIconsRadio.IsChecked = false;
        ApplyFilters();
    }

    private void ViewModeIcons_Click(object sender, RoutedEventArgs e)
    {
        ViewModeListRadio.IsChecked = false;
        ViewModeGridRadio.IsChecked = false;
        ViewModeIconsRadio.IsChecked = true;
        ApplyFilters();
    }

    // ----------------------------------------------------------------
    // Painel Lateral de Filtros (Expandir / Ocultar)
    // ----------------------------------------------------------------

    private void ToggleFiltersButton_Click(object sender, RoutedEventArgs e)
    {
        bool isOpen = ToggleFiltersButton.IsChecked == true;
        FilterSidebarColumn.Width = isOpen ? new GridLength(260) : new GridLength(0);
        FilterSplitterColumn.Width = isOpen ? new GridLength(8) : new GridLength(0);
        FilterSidebarPanel.Visibility = isOpen ? Visibility.Visible : Visibility.Collapsed;
        FilterSplitter.Visibility = isOpen ? Visibility.Visible : Visibility.Collapsed;
    }

    private void MoreActionsButton_Click(object sender, RoutedEventArgs e)
    {
        MoreActionsButton.ContextMenu!.PlacementTarget = MoreActionsButton;
        MoreActionsButton.ContextMenu.IsOpen = true;
    }

    private void SourceFilter_Changed(object sender, RoutedEventArgs e)
    {
        AllSourcesChip.IsChecked = SourceWingetCheck.IsChecked == true
            && SourceMsStoreCheck.IsChecked == true
            && SourceOtherCheck.IsChecked == true;
        ApplyFilters();
    }

    private void AllSourcesChip_Click(object sender, RoutedEventArgs e)
    {
        bool selectAll = AllSourcesChip.IsChecked == true;
        SourceWingetCheck.IsChecked = selectAll;
        SourceMsStoreCheck.IsChecked = selectAll;
        SourceOtherCheck.IsChecked = selectAll;
        ApplyFilters();
    }

    private void FilterOption_Changed(object sender, RoutedEventArgs e) => ApplyFilters();
    private void SearchMode_Changed(object sender, RoutedEventArgs e)
    {
        RefreshSearchSuggestions();
        ApplyFilters();
    }

    private void RefreshSearchSuggestions()
    {
        if (SearchBox is null)
            return;

        IEnumerable<string> suggestions = SearchModeNameRadio.IsChecked == true
            ? _rawPackages.Select(package => package.Name)
            : SearchModeIdRadio.IsChecked == true
                ? _rawPackages.Select(package => package.Id)
                : _rawPackages.SelectMany(package => new[] { package.Name, package.Id });

        SearchBox.OriginalItemsSource = suggestions
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Take(300)
            .ToList();
    }

    private void SearchBox_TextChanged(AutoSuggestBox sender, AutoSuggestBoxTextChangedEventArgs e) => ApplyFilters();

    private void SearchBox_QuerySubmitted(AutoSuggestBox sender, AutoSuggestBoxQuerySubmittedEventArgs e) => ApplyFilters();

    private void SelectAllSources_Click(object sender, RoutedEventArgs e)
    {
        AllSourcesChip.IsChecked = true;
        SourceWingetCheck.IsChecked = true;
        SourceMsStoreCheck.IsChecked = true;
        SourceOtherCheck.IsChecked = true;
        ApplyFilters();
    }

    private void ClearSources_Click(object sender, RoutedEventArgs e)
    {
        AllSourcesChip.IsChecked = false;
        SourceWingetCheck.IsChecked = false;
        SourceMsStoreCheck.IsChecked = false;
        SourceOtherCheck.IsChecked = false;
        ApplyFilters();
    }

    // ----------------------------------------------------------------
    // Operações de Atualização (UpdateSelectedButton & Variantes)
    // ----------------------------------------------------------------

    private void UpdateSelectedButton_Click(object sender, RoutedEventArgs e)
    {
        // O Click do ToggleButton interno do SplitButton também sobe pela árvore visual.
        // A seta deve abrir o menu sem executar a ação principal.
        if (e.OriginalSource is System.Windows.Controls.Primitives.ToggleButton || UpdateSelectedButton.IsDropDownOpen)
        {
            e.Handled = true;
            return;
        }

        _ = RunUpdateOnPackagesAsync(_filteredPackages.Where(p => p.IsSelectedForUpdate).ToList());
    }

    private void UninstallSelectedButton_Click(object sender, RoutedEventArgs e) =>
        _ = UninstallSelectedPackagesAsync();

    private void UpdateVariant_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not MenuItem { Tag: string variant })
            return;

        var selected = _filteredPackages.Where(package => package.IsSelectedForUpdate).ToList();
        _ = variant switch
        {
            "Admin" => RunUpdateOnPackagesAsync(selected, elevated: true),
            "Interactive" => RunUpdateOnPackagesAsync(selected, interactive: true),
            "SkipHash" => RunUpdateOnPackagesAsync(selected, ignoreSecurityHash: true),
            _ => Task.CompletedTask
        };
    }

    private async Task RunUpdateOnPackagesAsync(
        List<UpgradablePackage> selected,
        bool elevated = false,
        bool interactive = false,
        bool ignoreSecurityHash = false)
    {
        if (selected.Count == 0)
        {
            StatusText.Text = "Nenhum pacote selecionado. Marque ao menos um aplicativo para atualizar.";
            return;
        }

        UpdateSelectedButton.IsEnabled = false;
        UninstallSelectedButton.IsEnabled = false;
        ToggleSelectAllToolbarButton.IsEnabled = false;
        ReloadButton.IsEnabled = false;
        StatusText.Text = $"Iniciando atualização de {selected.Count} aplicativo(s)...";

        int succeeded = 0;
        int failed = 0;

        foreach (var package in selected)
        {
            package.IsUpdating = true;
            try
            {
                var result = await OperationRunner.RunUpdateAsync(
                    _queue,
                    _wingetExecutor,
                    package.Id,
                    package.Name,
                    package.IconUrl,
                    string.IsNullOrWhiteSpace(package.Source) ? "winget" : package.Source,
                    elevated,
                    interactive,
                    ignoreSecurityHash);

                if (result.Success)
                {
                    succeeded++;
                    _rawPackages.Remove(package);
                    _filteredPackages.Remove(package);
                }
                else
                {
                    failed++;
                    package.IsSelectedForUpdate = false;
                }
            }
            catch
            {
                failed++;
                package.IsSelectedForUpdate = false;
            }
            finally
            {
                package.IsUpdating = false;
            }
        }

        StatusText.Text = failed == 0
            ? $"Atualização concluída: {succeeded} pacote(s) atualizado(s) com sucesso."
            : $"Atualização concluída: {succeeded} sucesso(s), {failed} falha(s). Verifique a fila.";
        _snackbarService.Show(
            failed == 0 ? "Atualização concluída" : "Atualização parcialmente concluída",
            StatusText.Text,
            failed == 0 ? ControlAppearance.Success : ControlAppearance.Caution,
            new SymbolIcon(failed == 0 ? SymbolRegular.CheckmarkCircle24 : SymbolRegular.Warning24),
            TimeSpan.FromSeconds(4));

        ApplyFilters();
        ReloadButton.IsEnabled = true;
    }

    private async Task UninstallSelectedPackagesAsync()
    {
        var selected = _filteredPackages.Where(p => p.IsSelectedForUpdate).ToList();
        if (selected.Count == 0) return;

        var confirm = new Wpf.Ui.Controls.MessageBox
        {
            Title = "Desinstalar Pacotes",
            Content = $"Deseja desinstalar os {selected.Count} pacote(s) selecionado(s)?",
            PrimaryButtonText = "Desinstalar",
            CloseButtonText = "Cancelar"
        };

        StoreDialogStyles.Apply(confirm);
        if (await confirm.ShowDialogAsync() != Wpf.Ui.Controls.MessageBoxResult.Primary)
            return;

        foreach (var package in selected)
        {
            try
            {
                var result = await _wingetExecutor.UninstallAppAsync(package.Id, installedVersion: package.CurrentVersion);
                if (result.Success)
                {
                    _rawPackages.Remove(package);
                    _filteredPackages.Remove(package);
                }
            }
            catch { }
        }

        ApplyFilters();
    }

    // ----------------------------------------------------------------
    // Context Menu Handlers
    // ----------------------------------------------------------------

    private UpgradablePackage? GetContextPackage(object sender)
    {
        if (sender is MenuItem mi && mi.DataContext is UpgradablePackage p) return p;
        return _selectedPackage;
    }

    private void ContextMenuUpdate_Click(object sender, RoutedEventArgs e)
    {
        if (GetContextPackage(sender) is { } pkg) _ = RunUpdateOnPackagesAsync([pkg]);
    }

    private void ContextMenuUpdateAdmin_Click(object sender, RoutedEventArgs e)
    {
        if (GetContextPackage(sender) is { } pkg) _ = RunUpdateOnPackagesAsync([pkg], elevated: true);
    }

    private void ContextMenuUpdateInteractive_Click(object sender, RoutedEventArgs e)
    {
        if (GetContextPackage(sender) is { } pkg) _ = RunUpdateOnPackagesAsync([pkg], interactive: true);
    }

    private void ContextMenuUpdateSkipHash_Click(object sender, RoutedEventArgs e)
    {
        if (GetContextPackage(sender) is { } pkg) _ = RunUpdateOnPackagesAsync([pkg], ignoreSecurityHash: true);
    }

    private void ContextMenuDetails_Click(object sender, RoutedEventArgs e)
    {
        if (GetContextPackage(sender) is { } pkg) OpenPackageDetails(pkg);
    }

    private void ContextMenuIgnoreVersion_Click(object sender, RoutedEventArgs e)
    {
        if (GetContextPackage(sender) is { } pkg)
        {
            _ignoredUpdatesService.Ignore(pkg.Id, pkg.AvailableVersion);
            RefreshIgnoredUpdates();
            _rawPackages.Remove(pkg);
            _filteredPackages.Remove(pkg);
            ApplyFilters();
            StatusText.Text = $"Versão {pkg.AvailableVersion} de {pkg.Name} ignorada.";
        }
    }

    private void ContextMenuCopyId_Click(object sender, RoutedEventArgs e)
    {
        if (GetContextPackage(sender) is { } pkg)
        {
            try
            {
                Clipboard.SetText(pkg.Id);
                StatusText.Text = $"ID '{pkg.Id}' copiado para a área de transferência.";
            }
            catch { }
        }
    }

    private void OpenPackageDetails(UpgradablePackage package)
    {
        var catalog = _storeService.GetAll();
        var appEntry = catalog.FirstOrDefault(a =>
            string.Equals(a.Id, package.Id, StringComparison.OrdinalIgnoreCase)
            && string.Equals(a.Source, package.Source, StringComparison.OrdinalIgnoreCase));

        appEntry ??= new AppEntry
        {
            Id = package.Id,
            Name = package.Name,
            Source = string.IsNullOrWhiteSpace(package.Source) ? "winget" : package.Source,
            Version = package.AvailableVersion,
            IconUrl = package.IconUrl
        };

        _detailsOverlayService.ShowUpdate(appEntry, package.CurrentVersion, package.AvailableVersion);
    }

    // ----------------------------------------------------------------
    // Toolbar: Detalhes, Ignorar, CSV, Opções, Manual
    // ----------------------------------------------------------------

    private void PackageDetailsToolbarButton_Click(object sender, RoutedEventArgs e)
    {
        var target = _selectedPackage is not null && _filteredPackages.Contains(_selectedPackage)
            ? _selectedPackage
            : _filteredPackages.FirstOrDefault(p => p.IsSelectedForUpdate) ?? _filteredPackages.FirstOrDefault();
        if (target is not null)
        {
            OpenPackageDetails(target);
        }
        else
        {
            StatusText.Text = "Selecione um pacote na lista para visualizar os detalhes.";
        }
    }

    private void IgnoreSelectedButton_Click(object sender, RoutedEventArgs e)
    {
        var selected = _filteredPackages.Where(p => p.IsSelectedForUpdate).ToList();
        if (selected.Count == 0)
        {
            StatusText.Text = "Nenhum pacote selecionado para ignorar.";
            return;
        }

        foreach (var pkg in selected)
        {
            _ignoredUpdatesService.Ignore(pkg.Id, pkg.AvailableVersion);
            _rawPackages.Remove(pkg);
            _filteredPackages.Remove(pkg);
        }

        RefreshIgnoredUpdates();
        ApplyFilters();
        StatusText.Text = $"{selected.Count} pacote(s) ignorado(s).";
    }

    private void ManageIgnoredButton_Click(object sender, RoutedEventArgs e)
    {
        RefreshIgnoredUpdates();
        IgnoredUpdatesOverlay.Visibility = Visibility.Visible;
    }

    private void CloseIgnoredOverlay_Click(object sender, RoutedEventArgs e)
    {
        IgnoredUpdatesOverlay.Visibility = Visibility.Collapsed;
    }

    private void RefreshIgnoredUpdates()
    {
        _ignoredPackages.Clear();
        foreach (var entry in _ignoredUpdatesService.GetIgnoredUpdates())
        {
            _ignoredPackages.Add(entry);
        }

        IgnoredUpdatesEmptyModalText.Visibility = _ignoredPackages.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    private void RestoreIgnoredUpdate_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { DataContext: IgnoredUpdateEntry entry }) return;

        _ignoredUpdatesService.Unignore(entry.AppId);
        RefreshIgnoredUpdates();
        StatusText.Text = $"Versão {entry.Version} de {entry.AppId} restaurada.";
        _ = CheckUpdatesAsync();
    }

    private void RestoreAllIgnored_Click(object sender, RoutedEventArgs e)
    {
        var entries = _ignoredUpdatesService.GetIgnoredUpdates();
        foreach (var entry in entries)
        {
            _ignoredUpdatesService.Unignore(entry.AppId);
        }

        RefreshIgnoredUpdates();
        IgnoredUpdatesOverlay.Visibility = Visibility.Collapsed;
        StatusText.Text = "Todas as versões ignoradas foram restauradas.";
        _ = CheckUpdatesAsync();
    }

    private void ExportCsvButton_Click(object sender, RoutedEventArgs e)
    {
        if (_filteredPackages.Count == 0)
        {
            StatusText.Text = "Nenhum pacote para exportar.";
            return;
        }

        var saveDialog = new SaveFileDialog
        {
            Title = "Exportar Atualizações para CSV",
            Filter = "Arquivo CSV (*.csv)|*.csv",
            FileName = $"Atualizacoes_{DateTime.Now:yyyyMMdd_HHmm}.csv"
        };

        if (saveDialog.ShowDialog() != true) return;

        try
        {
            var sb = new StringBuilder();
            sb.AppendLine("Nome,ID,VersaoAtual,NovaVersao,Origem");
            foreach (var p in _filteredPackages)
            {
                sb.AppendLine($"\"{EscapeCsv(p.Name)}\",\"{EscapeCsv(p.Id)}\",\"{EscapeCsv(p.CurrentVersion)}\",\"{EscapeCsv(p.AvailableVersion)}\",\"{EscapeCsv(p.SourceLabel)}\"");
            }

            File.WriteAllText(saveDialog.FileName, sb.ToString(), Encoding.UTF8);
            StatusText.Text = $"Exportado para '{Path.GetFileName(saveDialog.FileName)}' com sucesso.";
            _snackbarService.Show("Exportação concluída", StatusText.Text, ControlAppearance.Success,
                new SymbolIcon(SymbolRegular.CheckmarkCircle24), TimeSpan.FromSeconds(3));
        }
        catch (Exception ex)
        {
            StatusText.Text = $"Erro ao exportar CSV: {ex.Message}";
        }
    }

    private static string EscapeCsv(string value) => value.Replace("\"", "\"\"");

    // Diálogo Manual
    private void ManualUpdateButton_Click(object sender, RoutedEventArgs e)
    {
        ManualPackageIdBox.Text = _selectedPackage?.Id ?? string.Empty;
        ManualUpdateOverlay.Visibility = Visibility.Visible;
        ManualPackageIdBox.Focus();
    }

    private void CloseManualOverlay_Click(object sender, RoutedEventArgs e)
    {
        ManualUpdateOverlay.Visibility = Visibility.Collapsed;
    }

    private async void ExecuteManualUpdate_Click(object sender, RoutedEventArgs e)
    {
        string input = ManualPackageIdBox.Text?.Trim() ?? string.Empty;
        if (string.IsNullOrWhiteSpace(input)) return;

        ManualUpdateOverlay.Visibility = Visibility.Collapsed;
        StatusText.Text = $"Executando atualização de '{input}'...";

        var result = await OperationRunner.RunUpdateAsync(_queue, _wingetExecutor, input, input);
        StatusText.Text = result.Success
            ? $"Atualização de '{input}' concluída com sucesso."
            : $"Falha ao atualizar '{input}'. Verifique o log.";

        await CheckUpdatesAsync();
    }

    // Diálogo Opções
    private void OptionsButton_Click(object sender, RoutedEventArgs e)
    {
        OptionsOverlay.Visibility = Visibility.Visible;
    }

    private void CloseOptionsOverlay_Click(object sender, RoutedEventArgs e)
    {
        OptionsOverlay.Visibility = Visibility.Collapsed;
    }

    private void DismissModalOverlay_Click(object sender, MouseButtonEventArgs e)
    {
        IgnoredUpdatesOverlay.Visibility = Visibility.Collapsed;
        ManualUpdateOverlay.Visibility = Visibility.Collapsed;
        OptionsOverlay.Visibility = Visibility.Collapsed;
    }

    private void ModalCard_Click(object sender, MouseButtonEventArgs e)
    {
        e.Handled = true; // Impede que o clique dentro do cartão feche o modal
    }
}
