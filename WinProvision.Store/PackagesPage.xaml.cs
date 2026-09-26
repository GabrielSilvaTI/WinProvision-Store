using System;
using System.Collections.Generic;
using System.Collections.Specialized;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Win32;
using WinProvision.Core.Models;
using WinProvision.Core.Models.Office;
using WinProvision.Core.Services;
using WinProvision.Core.Services.Office;
using WinProvision.Core.Services.Profile;
using WinProvision.Core.Services.Provisioning;
using WinProvision.Store.Services;
using Wpf.Ui.Controls;

namespace WinProvision.Store;

/// <summary>
/// Tela de Coleção de Pacotes:
/// Gestão de perfis múltiplos, toolbar de operações em lote, barra lateral de filtros e fontes,
/// visualização em tabela e cartões, importação e exportação em JSON.
/// e geração de script de instalação automatizado.
/// </summary>
public partial class PackagesPage : Page
{
    private readonly PackageCollectionService _collectionService;
    private readonly ProfileService _profileService;
    private readonly StoreService _storeService;
    private readonly WingetExecutor _wingetExecutor;
    private readonly OfficeDeploymentToolService _officeService;
    private readonly OperationsQueueService _queue;
    private readonly ProvisioningService _provisioningService;
    private readonly PackageMetricsService _metricsService;
    private readonly AppDetailsOverlayService _overlayService;

    private PackageProfileTab? _observedTab;
    private ICollectionView? _collectionView;
    private CancellationTokenSource? _metricsCts;
    private readonly HashSet<AppEntry> _metricAttemptedItems = [];

    private string _currentSortProperty = "Name";
    private ListSortDirection _currentSortDirection = ListSortDirection.Ascending;
    private bool _suppressSortModeSelectionChanged;
    private bool _isSidebarCollapsed;

    public PackagesPage()
    {
        InitializeComponent();

        _collectionService = App.Services.GetRequiredService<PackageCollectionService>();
        _profileService = App.Services.GetRequiredService<ProfileService>();
        _storeService = App.Services.GetRequiredService<StoreService>();
        _wingetExecutor = App.Services.GetRequiredService<WingetExecutor>();
        _officeService = App.Services.GetRequiredService<OfficeDeploymentToolService>();
        _queue = App.Services.GetRequiredService<OperationsQueueService>();
        _provisioningService = App.Services.GetRequiredService<ProvisioningService>();
        _metricsService = App.Services.GetRequiredService<PackageMetricsService>();
        _overlayService = App.Services.GetRequiredService<AppDetailsOverlayService>();

        ProfileTabControl.ItemsSource = _collectionService.Tabs;
        ProfileTabControl.SelectedItem = _collectionService.ActiveTab;

        AttachToActiveTab();
        SetViewMode(false); // Cartões no padrão da Microsoft Store
        UpdateStatus();
    }

    // ─── Conexão com o Perfil Ativo ──────────────────────────────────────────

    private void AttachToActiveTab()
    {
        if (_observedTab is not null)
        {
            _observedTab.Items.CollectionChanged -= ActiveItems_CollectionChanged;
            foreach (var app in _observedTab.Items)
                app.PropertyChanged -= App_PropertyChanged;
        }

        _observedTab = _collectionService.ActiveTab;
        if (_observedTab is null) return;

        _observedTab.Items.CollectionChanged += ActiveItems_CollectionChanged;
        foreach (var app in _observedTab.Items)
            app.PropertyChanged += App_PropertyChanged;

        _collectionView = CollectionViewSource.GetDefaultView(_observedTab.Items);
        _collectionView.Filter = FilterCollectionItem;
        ApplySorting();

        AppCollectionItemsControl.ItemsSource = _collectionView;
        AppCollectionListItemsControl.ItemsSource = _collectionView;
        AppCollectionIconsItemsControl.ItemsSource = _collectionView;

        _ = RefreshCollectionMetricsAsync(_observedTab);
        RefreshCollectionSummary(_observedTab);
        UpdateStatus();
    }

    private void ActiveItems_CollectionChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (e.NewItems is not null)
            foreach (AppEntry app in e.NewItems) app.PropertyChanged += App_PropertyChanged;
        if (e.OldItems is not null)
            foreach (AppEntry app in e.OldItems) app.PropertyChanged -= App_PropertyChanged;

        _collectionView?.Refresh();
        UpdateStatus();
        if (_observedTab is { } tab) _ = RefreshCollectionMetricsAsync(tab);
    }

    private void App_PropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(AppEntry.IsSelectedForInstall) or nameof(AppEntry.InstallerSizeBytes))
        {
            Dispatcher.BeginInvoke(() =>
            {
                if (_collectionService.ActiveTab is { } tab)
                    RefreshCollectionSummary(tab);
                UpdateStatus();
            });
        }
    }

    // ─── Filtros e Pesquisa ──────────────────────────────────────────────────

    private bool FilterCollectionItem(object item)
    {
        if (item is not AppEntry app) return false;

        // Filtro de texto da barra de busca
        string query = PackageSearchTextBox?.Text?.Trim() ?? string.Empty;
        if (query.Length > 0)
        {
            bool matchesText = app.Name.Contains(query, StringComparison.OrdinalIgnoreCase)
                || app.Publisher.Contains(query, StringComparison.OrdinalIgnoreCase)
                || app.Id.Contains(query, StringComparison.OrdinalIgnoreCase);

            if (!matchesText) return false;
        }

        // Filtro de apenas selecionados
        if (FilterOnlySelectedCheckBox?.IsChecked == true && !app.IsSelectedForInstall)
            return false;

        // Filtros de fontes
        if (FilterSourceAllCheckBox?.IsChecked != true)
        {
            bool isWinget = string.Equals(app.Source, "winget", StringComparison.OrdinalIgnoreCase);
            bool isStore = string.Equals(app.Source, "msstore", StringComparison.OrdinalIgnoreCase);
            bool isOffice = app.Office is not null;

            if (isWinget && FilterSourceWingetCheckBox?.IsChecked != true) return false;
            if (isStore && FilterSourceMsStoreCheckBox?.IsChecked != true) return false;
            if (isOffice && FilterSourceOfficeCheckBox?.IsChecked != true) return false;
        }

        return true;
    }

    private void PackageSearchTextBox_TextChanged(object sender, TextChangedEventArgs e) => _collectionView?.Refresh();

    private void ClearSearchButton_Click(object sender, RoutedEventArgs e)
    {
        PackageSearchTextBox.Text = string.Empty;
        _collectionView?.Refresh();
    }

    private void FilterCheckBox_Click(object sender, RoutedEventArgs e)
    {
        if (sender == FilterSourceAllCheckBox)
        {
            bool selectAll = FilterSourceAllCheckBox.IsChecked == true;
            FilterSourceWingetCheckBox.IsChecked = selectAll;
            FilterSourceMsStoreCheckBox.IsChecked = selectAll;
            FilterSourceOfficeCheckBox.IsChecked = selectAll;
        }
        else if (sender == FilterSourceWingetCheckBox || sender == FilterSourceMsStoreCheckBox || sender == FilterSourceOfficeCheckBox)
        {
            FilterSourceAllCheckBox.IsChecked =
                FilterSourceWingetCheckBox.IsChecked == true &&
                FilterSourceMsStoreCheckBox.IsChecked == true &&
                FilterSourceOfficeCheckBox.IsChecked == true;
        }

        _collectionView?.Refresh();
        UpdateStatus();
    }

    // ─── Ordenação da Tabela ─────────────────────────────────────────────────

    private void SortByName_Click(object sender, MouseButtonEventArgs e) => ToggleSort("Name");
    private void SortById_Click(object sender, MouseButtonEventArgs e) => ToggleSort("Id");
    private void SortByVersion_Click(object sender, MouseButtonEventArgs e) => ToggleSort("Version");
    private void SortBySource_Click(object sender, MouseButtonEventArgs e) => ToggleSort("Source");

    private void ToggleSort(string propertyName)
    {
        if (_currentSortProperty == propertyName)
        {
            _currentSortDirection = _currentSortDirection == ListSortDirection.Ascending
                ? ListSortDirection.Descending
                : ListSortDirection.Ascending;
        }
        else
        {
            _currentSortProperty = propertyName;
            _currentSortDirection = ListSortDirection.Ascending;
        }

        int selectedIndex = _currentSortProperty switch
        {
            "Id" => 1,
            "Version" => 2,
            "Source" => 3,
            _ => 0
        };
        _suppressSortModeSelectionChanged = true;
        SortModeComboBox.SelectedIndex = selectedIndex;
        _suppressSortModeSelectionChanged = false;
        ApplySorting();
    }

    private void SortModeComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!IsLoaded || _suppressSortModeSelectionChanged || SortModeComboBox.SelectedIndex < 0) return;

        _currentSortProperty = SortModeComboBox.SelectedIndex switch
        {
            1 => "Id",
            2 => "Version",
            3 => "Source",
            _ => "Name"
        };
        _currentSortDirection = ListSortDirection.Ascending;
        ApplySorting();
    }

    private void ApplySorting()
    {
        if (_collectionView is null) return;

        using (_collectionView.DeferRefresh())
        {
            _collectionView.SortDescriptions.Clear();
            _collectionView.SortDescriptions.Add(new SortDescription(_currentSortProperty, _currentSortDirection));
        }
    }

    // ─── Atualização de Status e Indicadores ─────────────────────────────────

    private void UpdateStatus()
    {
        var active = _collectionService.ActiveTab;
        if (active == null) return;

        int total = active.Items.Count;
        int selected = active.Items.Count(a => a.IsSelectedForInstall);

        SelectionSummaryText.Text = $"{selected} selecionado(s) · Ação: Instalar seleção";
        StatusText.Text = total == 0
            ? "Coleção vazia."
            : $"Perfil \"{active.Title}\" ({total} pacotes)";

        InstallSelectedButton.IsEnabled = selected > 0;
        RemoveSelectedButton.IsEnabled = selected > 0;
        PackageDetailsToolbarButton.IsEnabled = total > 0;

        // Atualiza estado do checkbox no cabeçalho
        if (SelectAllHeaderCheckBox != null)
        {
            if (total == 0)
            {
                SelectAllHeaderCheckBox.IsChecked = false;
                SelectAllHeaderCheckBox.IsEnabled = false;
            }
            else
            {
                SelectAllHeaderCheckBox.IsEnabled = true;
                if (selected == total)
                    SelectAllHeaderCheckBox.IsChecked = true;
                else if (selected == 0)
                    SelectAllHeaderCheckBox.IsChecked = false;
                else
                    SelectAllHeaderCheckBox.IsChecked = null; // Indeterminado
            }
        }

        if (ToggleSelectAllButton != null)
        {
            ToggleSelectAllButton.IsEnabled = total > 0;
            ToggleSelectAllText.Text = (total > 0 && selected == total) ? "Desmarcar todos" : "Selecionar todos";
            ToggleSelectAllIcon.Symbol = (total > 0 && selected == total)
                ? Wpf.Ui.Controls.SymbolRegular.DismissCircle24
                : Wpf.Ui.Controls.SymbolRegular.CheckmarkCircle24;
        }

        // Estado vazio
        if (EmptyCollectionPanel != null)
        {
            bool isEmpty = total == 0;
            EmptyCollectionPanel.Visibility = isEmpty ? Visibility.Visible : Visibility.Collapsed;
            ListViewScrollViewer.Visibility = (!isEmpty && ListViewToggleButton.IsChecked == true) ? Visibility.Visible : Visibility.Collapsed;
            GridViewScrollViewer.Visibility = (!isEmpty && GridViewToggleButton.IsChecked == true) ? Visibility.Visible : Visibility.Collapsed;
            IconsViewScrollViewer.Visibility = (!isEmpty && IconsViewToggleButton.IsChecked == true) ? Visibility.Visible : Visibility.Collapsed;
            TableHeaderBar.Visibility = (!isEmpty && ListViewToggleButton.IsChecked == true) ? Visibility.Visible : Visibility.Collapsed;
        }

        CloseProfileButton.IsEnabled = !active.IsDefault;
        CloseProfileButton.Visibility = active.IsDefault ? Visibility.Collapsed : Visibility.Visible;

        if (RenameProfileButton != null)
        {
            RenameProfileButton.IsEnabled = !active.IsDefault;
            RenameProfileButton.Visibility = active.IsDefault ? Visibility.Collapsed : Visibility.Visible;
        }

        RefreshCollectionSummary(active);
    }

    private void RefreshCollectionSummary(PackageProfileTab tab)
    {
        var selectedItems = tab.Items.Where(a => a.IsSelectedForInstall).ToList();

        if (selectedItems.Count == 0)
        {
            EstimatedSizeText.Text = "—";
            EstimatedTimeText.Text = "—";
            return;
        }

        long totalBytes = selectedItems.Sum(a => a.InstallerSizeBytes ?? 0);
        int measurablePackageCount = selectedItems.Count(a => a.Office is null);
        bool loading = selectedItems.Any(a => a.Office is null && !_metricAttemptedItems.Contains(a));
        bool hasUnknownSize = selectedItems.Any(a => a.Office is null && _metricAttemptedItems.Contains(a) && !a.InstallerSizeBytes.HasValue);

        if (loading)
        {
            EstimatedSizeText.Text = totalBytes > 0 ? FormatBytes(totalBytes, true) : "Calculando…";
            EstimatedTimeText.Text = totalBytes > 0 ? FormatDuration(totalBytes, true, measurablePackageCount) : "Calculando…";
            return;
        }

        if (totalBytes <= 0 && hasUnknownSize)
        {
            EstimatedSizeText.Text = "Indisponível";
            EstimatedTimeText.Text = "Indisponível";
            return;
        }

        EstimatedSizeText.Text = FormatBytes(totalBytes, hasUnknownSize);
        EstimatedTimeText.Text = FormatDuration(totalBytes, hasUnknownSize, measurablePackageCount);
    }

    private static string FormatBytes(long bytes, bool unknown)
    {
        if (bytes <= 0) return unknown ? "Calculando…" : "—";
        double mb = bytes / 1024d / 1024d;
        string value = mb >= 1024 ? $"~ {mb / 1024:0.0} GB" : $"~ {mb:0} MB";
        return unknown ? value + " +" : value;
    }

    private static string FormatDuration(long bytes, bool unknown, int packageCount)
    {
        if (bytes <= 0) return unknown ? "Calculando…" : "—";
        double overhead = Math.Max(10, packageCount * 12);
        double seconds = overhead + (bytes / 1024d / 1024d) / 8d;
        if (seconds >= 3600) return $"~ {seconds / 3600:0.0} h" + (unknown ? " +" : string.Empty);
        if (seconds >= 60) return $"~ {Math.Ceiling(seconds / 60):0} min" + (unknown ? " +" : string.Empty);
        return $"~ {Math.Max(1, Math.Ceiling(seconds)):0} s" + (unknown ? " +" : string.Empty);
    }

    private async Task RefreshCollectionMetricsAsync(PackageProfileTab tab)
    {
        _metricsCts?.Cancel();
        _metricsCts?.Dispose();
        _metricsCts = new CancellationTokenSource();
        CancellationToken ct = _metricsCts.Token;

        var apps = tab.Items.Where(a => a.Office is null
            && (!_metricAttemptedItems.Contains(a) || !a.InstallerSizeBytes.HasValue)).ToList();
        if (apps.Count == 0)
        {
            RefreshCollectionSummary(tab);
            return;
        }

        using var gate = new SemaphoreSlim(3);
        var tasks = apps.Select(async app =>
        {
            await gate.WaitAsync(ct);
            try
            {
                long? size = await _metricsService.GetInstallerSizeAsync(app, ct);
                await Dispatcher.InvokeAsync(() =>
                {
                    _metricAttemptedItems.Add(app);
                    if (tab.Items.Contains(app))
                        app.InstallerSizeBytes = size;

                    if (ReferenceEquals(_collectionService.ActiveTab, tab))
                    {
                        RefreshCollectionSummary(tab);
                        UpdateStatus();
                    }
                });
            }
            catch (OperationCanceledException) { }
            catch
            {
                await Dispatcher.InvokeAsync(() =>
                {
                    _metricAttemptedItems.Add(app);
                    if (ReferenceEquals(_collectionService.ActiveTab, tab))
                        RefreshCollectionSummary(tab);
                });
            }
            finally { gate.Release(); }
        });

        try { await Task.WhenAll(tasks); } catch (OperationCanceledException) { }
    }

    // ─── Seleção Global ──────────────────────────────────────────────────────

    private void SelectAllHeaderCheckBox_Click(object sender, RoutedEventArgs e)
    {
        var active = _collectionService.ActiveTab;
        if (active == null || active.Items.Count == 0) return;

        bool target = SelectAllHeaderCheckBox.IsChecked == true;
        foreach (var item in active.Items)
            item.IsSelectedForInstall = target;

        UpdateStatus();
    }

    private void ToggleSelectAllButton_Click(object sender, RoutedEventArgs e)
    {
        var active = _collectionService.ActiveTab;
        if (active == null || active.Items.Count == 0) return;

        bool allSelected = active.Items.All(a => a.IsSelectedForInstall);
        bool targetState = !allSelected;

        foreach (var item in active.Items)
            item.IsSelectedForInstall = targetState;

        UpdateStatus();
    }

    // ─── Modos de Visualização (Tabela vs Grade) ─────────────────────────────

    private void ListViewToggleButton_Click(object sender, RoutedEventArgs e) => SetViewMode(true);
    private void GridViewToggleButton_Click(object sender, RoutedEventArgs e) => SetViewMode(false);
    private void IconsViewToggleButton_Click(object sender, RoutedEventArgs e) => SetViewMode(false, true);

    private void SetViewMode(bool tableMode, bool iconMode = false)
    {
        bool hasItems = (_collectionService.ActiveTab?.Items.Count ?? 0) > 0;

        ListViewScrollViewer.Visibility = (tableMode && hasItems) ? Visibility.Visible : Visibility.Collapsed;
        TableHeaderBar.Visibility = (tableMode && hasItems) ? Visibility.Visible : Visibility.Collapsed;
        GridViewScrollViewer.Visibility = (!tableMode && !iconMode && hasItems) ? Visibility.Visible : Visibility.Collapsed;
        IconsViewScrollViewer.Visibility = (iconMode && hasItems) ? Visibility.Visible : Visibility.Collapsed;

        ListViewToggleButton.IsChecked = tableMode;
        GridViewToggleButton.IsChecked = !tableMode && !iconMode;
        IconsViewToggleButton.IsChecked = iconMode;

        Brush accent = TryFindResource("SystemAccentColorPrimaryBrush") as Brush ?? Brushes.DodgerBlue;
        ListViewToggleButton.Background = tableMode ? accent : Brushes.Transparent;
        GridViewToggleButton.Background = !tableMode && !iconMode ? accent : Brushes.Transparent;
        IconsViewToggleButton.Background = iconMode ? accent : Brushes.Transparent;
        ListViewToggleButton.Foreground = tableMode ? Brushes.White : (TryFindResource("TextFillColorPrimaryBrush") as Brush ?? Brushes.White);
        GridViewToggleButton.Foreground = !tableMode && !iconMode ? Brushes.White : (TryFindResource("TextFillColorPrimaryBrush") as Brush ?? Brushes.White);
        IconsViewToggleButton.Foreground = iconMode ? Brushes.White : (TryFindResource("TextFillColorPrimaryBrush") as Brush ?? Brushes.White);
    }

    // ─── Barra Lateral Retrátil ──────────────────────────────────────────────

    private void ToggleSidebarButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender == CollapseSidebarButton)
            SidebarToggleToolbarButton.IsChecked = false;

        _isSidebarCollapsed = SidebarToggleToolbarButton.IsChecked != true;

        FilterSidebarColumn.Width = _isSidebarCollapsed ? new GridLength(0) : new GridLength(220);
        FilterSplitterColumn.Width = _isSidebarCollapsed ? new GridLength(0) : new GridLength(6);
        FilterSidebarPanel.Visibility = _isSidebarCollapsed ? Visibility.Collapsed : Visibility.Visible;
    }

    // ─── Gestão de Guias e Perfis ────────────────────────────────────────────

    private void ProfileTabControl_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (ProfileTabControl.SelectedItem is PackageProfileTab selectedTab)
        {
            _collectionService.ActiveTab = selectedTab;
            CloseRenamePanel();
            AttachToActiveTab();
            SetViewMode(ListViewToggleButton.IsChecked == true, IconsViewToggleButton.IsChecked == true);
            UpdateStatus();
        }
    }

    private void NewTabButton_Click(object sender, RoutedEventArgs e)
    {
        var newTab = _collectionService.CreateNewTab();
        ProfileTabControl.SelectedItem = newTab;
        AttachToActiveTab();
        UpdateStatus();
    }

    private async void CloseTabButton_Click(object sender, RoutedEventArgs e)
    {
        var tab = _collectionService.ActiveTab;
        if (tab is null || tab.IsDefault) return;

        var confirmDialog = new Wpf.Ui.Controls.MessageBox
        {
            Title = "Excluir perfil",
            Content = $"Excluir “{tab.Title}”? Os pacotes continuarão instalados no Windows.",
            PrimaryButtonText = "Excluir",
            CloseButtonText = "Cancelar"
        };

        var result = await confirmDialog.ShowDialogAsync();
        if (result != Wpf.Ui.Controls.MessageBoxResult.Primary) return;

        _collectionService.CloseTab(tab);
        ProfileTabControl.SelectedItem = _collectionService.ActiveTab;
        AttachToActiveTab();
        UpdateStatus();
    }

    private void RenameProfileButton_Click(object sender, RoutedEventArgs e)
    {
        var tab = _collectionService.ActiveTab;
        if (tab is null || tab.IsDefault) return;

        RenameProfileTextBox.Text = tab.Title;
        ViewProfilePanel.Visibility = Visibility.Collapsed;
        EditProfilePanel.Visibility = Visibility.Visible;
        RenameProfileTextBox.Focus();
        RenameProfileTextBox.SelectAll();
    }

    private void SaveProfileNameButton_Click(object sender, RoutedEventArgs e)
    {
        var tab = _collectionService.ActiveTab;
        if (tab is null || tab.IsDefault) return;

        var newTitle = RenameProfileTextBox.Text?.Trim();
        if (!string.IsNullOrWhiteSpace(newTitle))
        {
            tab.Title = newTitle;
            ProfileTabControl.Items.Refresh();
            UpdateStatus();
        }

        CloseRenamePanel();
    }

    private void CancelProfileNameButton_Click(object sender, RoutedEventArgs e) => CloseRenamePanel();

    private void RenameProfileTextBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter) SaveProfileNameButton_Click(sender, e);
        else if (e.Key == Key.Escape) CancelProfileNameButton_Click(sender, e);
    }

    private void CloseRenamePanel()
    {
        if (EditProfilePanel != null && ViewProfilePanel != null)
        {
            EditProfilePanel.Visibility = Visibility.Collapsed;
            ViewProfilePanel.Visibility = Visibility.Visible;
        }
    }

    // ─── Ações de Instalação e Operações em Lote ─────────────────────────────

    private void InstallVariantsButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement fe)
        {
            var menu = new ContextMenu();

            var miAdmin = new System.Windows.Controls.MenuItem { Header = "Instalar como administrador" };
            miAdmin.Click += (_, _) => InstallSelected(elevated: true);
            menu.Items.Add(miAdmin);

            var miInteractive = new System.Windows.Controls.MenuItem { Header = "Instalação interativa" };
            miInteractive.Click += (_, _) => InstallSelected(interactive: true);
            menu.Items.Add(miInteractive);

            var miSkipHash = new System.Windows.Controls.MenuItem { Header = "Pular verificação de integridade" };
            miSkipHash.Click += (_, _) => InstallSelected(skipHash: true);
            menu.Items.Add(miSkipHash);

            menu.Items.Add(new Separator());

            var miDownload = new System.Windows.Controls.MenuItem { Header = "Baixar instaladores dos selecionados" };
            miDownload.Click += (_, _) => DownloadSelectedInstallers();
            menu.Items.Add(miDownload);

            menu.PlacementTarget = fe;
            menu.IsOpen = true;
        }
    }

    private void InstallSelectedButton_Click(object sender, RoutedEventArgs e) => InstallSelected();

    private async void InstallSelected(bool elevated = false, bool interactive = false, bool skipHash = false)
    {
        var activeTab = _collectionService.ActiveTab;
        var selected = activeTab?.Items.Where(a => a.IsSelectedForInstall).ToList() ?? [];

        if (selected.Count == 0)
        {
            StatusText.Text = "Nenhum aplicativo selecionado.";
            return;
        }

        InstallSelectedButton.IsEnabled = false;
        StatusText.Text = $"Enviando {selected.Count} aplicativo(s) para a fila de instalação...";

        int count = 0;
        try
        {
            foreach (var app in selected)
            {
                if (app.Office is { } officeOptions)
                {
                    _ = InstallOfficePlanAsync(app, officeOptions, interactive);
                }
                else
                {
                    _ = OperationRunner.RunInstallAsync(
                        _queue, _wingetExecutor, app.Id, app.Name, app.IconUrl,
                        source: app.Source, interactive: interactive);
                }
                count++;
            }

            StatusText.Text = $"{count} aplicativo(s) adicionados à fila de operações.";
        }
        finally
        {
            InstallSelectedButton.IsEnabled = true;
        }
    }

    private async Task<bool> InstallOfficePlanAsync(AppEntry app, OfficeInstallOptions options, bool interactive = false)
    {
        var plan = OfficePlanCatalog.All.FirstOrDefault(p => string.Equals(p.ProductId, options.ProductId, StringComparison.OrdinalIgnoreCase));
        if (plan is null)
        {
            StatusText.Text = $"Não foi possível instalar \"{app.Name}\": plano não encontrado no catálogo.";
            return false;
        }

        var request = new OfficeInstallRequest(
            plan,
            options.Architecture,
            options.LanguageId,
            options.ExcludedApps,
            DisplayNone: interactive ? false : options.Silent,
            AdditionalLanguageIds: options.AdditionalLanguageIds,
            DisplayLevel: interactive || !options.Silent ? OfficeDisplayLevel.Visible : OfficeDisplayLevel.Silent,
            ChannelOverride: options.ChannelOverride,
            AutoUpdatesEnabled: options.AutoUpdatesEnabled);

        try
        {
            return await OperationRunner.RunOfficeInstallAsync(_queue, _officeService, request);
        }
        catch
        {
            return false;
        }
    }

    private async void DownloadSelectedInstallers()
    {
        var activeTab = _collectionService.ActiveTab;
        var selected = activeTab?.Items.Where(a => a.IsSelectedForInstall).ToList() ?? [];

        if (selected.Count == 0)
        {
            StatusText.Text = "Selecione ao menos um aplicativo para baixar o instalador.";
            return;
        }

        var dialog = new OpenFolderDialog
        {
            Title = "Escolha a pasta de destino para os instaladores",
            Multiselect = false
        };

        if (dialog.ShowDialog() != true || string.IsNullOrWhiteSpace(dialog.FolderName))
            return;

        string targetFolder = dialog.FolderName;
        int downloaded = 0;
        var failures = new List<string>();
        for (int index = 0; index < selected.Count; index++)
        {
            var app = selected[index];
            StatusText.Text = $"Baixando instalador {index + 1} de {selected.Count}: {app.Name}...";
            try
            {
                var result = await _wingetExecutor.DownloadInstallerAsync(app.Id, targetFolder, app.Source);
                if (result.Success) downloaded++;
                else failures.Add(app.Name);
            }
            catch (Exception ex)
            {
                failures.Add(app.Name);
                WinProvisionLog.Write($"DOWNLOAD INSTALLER FAILED id=\"{app.Id}\" error=\"{ex.Message}\"");
            }
        }

        StatusText.Text = failures.Count == 0
            ? $"{downloaded} instalador(es) baixado(s) em {targetFolder}."
            : $"{downloaded} baixado(s); falha em {failures.Count}: {string.Join(", ", failures)}.";
    }

    // ─── Importação / Exportação de coleções JSON ────────────────────────────

    private async void OpenBundleButton_Click(object sender, RoutedEventArgs e)
    {
        var openDialog = new OpenFileDialog
        {
            Title = "Abrir coleção de pacotes",
            Filter = "Arquivo JSON (*.json)|*.json"
        };

        if (openDialog.ShowDialog() != true) return;

        try
        {
            string content = await File.ReadAllTextAsync(openDialog.FileName, Encoding.UTF8);
            using var doc = JsonDocument.Parse(content);
            var root = doc.RootElement;

            List<string> packageIds = [];

            // Aceita o formato de coleção do WinProvision ou uma lista simples de IDs.
            if (root.TryGetProperty("packages", out var packagesElement) && packagesElement.ValueKind == JsonValueKind.Array)
            {
                foreach (var pkg in packagesElement.EnumerateArray())
                {
                    if (pkg.TryGetProperty("Id", out var idProp))
                        packageIds.Add(idProp.GetString() ?? "");
                }
            }
            else if (root.ValueKind == JsonValueKind.Array)
            {
                foreach (var item in root.EnumerateArray())
                {
                    if (item.ValueKind == JsonValueKind.String)
                        packageIds.Add(item.GetString() ?? "");
                    else if (item.TryGetProperty("Id", out var idProp))
                        packageIds.Add(idProp.GetString() ?? "");
                }
            }

            if (packageIds.Count == 0)
            {
                StatusText.Text = "O arquivo não contém pacotes reconhecíveis.";
                return;
            }

            // Cria uma nova guia com o nome do arquivo
            string bundleName = Path.GetFileNameWithoutExtension(openDialog.FileName);
            var tab = _collectionService.CreateNewTab();
            tab.Title = bundleName;
            ProfileTabControl.SelectedItem = tab;

            StatusText.Text = $"Localizando {packageIds.Count} pacotes no catálogo...";

            // Carrega os apps do StoreService
            int added = 0;
            foreach (var id in packageIds)
            {
                if (string.IsNullOrWhiteSpace(id)) continue;
                var app = _storeService.GetAll().FirstOrDefault(a => string.Equals(a.Id, id, StringComparison.OrdinalIgnoreCase));
                if (app is not null)
                {
                    tab.Items.Add(app);
                    added++;
                }
                else
                {
                    // Adiciona stub se não estiver no catálogo carregado
                    tab.Items.Add(new AppEntry
                    {
                        Id = id,
                        Name = id,
                        Source = "winget",
                        Publisher = "WinGet"
                    });
                    added++;
                }
            }

            AttachToActiveTab();
            StatusText.Text = $"Coleção \"{bundleName}\" aberta com {added} pacotes.";
        }
        catch (Exception ex)
        {
            StatusText.Text = $"Erro ao abrir arquivo: {ex.Message}";
        }
    }

    private async void SaveBundleButton_Click(object sender, RoutedEventArgs e)
    {
        var activeTab = _collectionService.ActiveTab;
        if (activeTab == null || activeTab.Items.Count == 0)
        {
            StatusText.Text = "A coleção atual está vazia.";
            return;
        }

        var saveDialog = new SaveFileDialog
        {
            Title = "Salvar coleção de pacotes",
            Filter = "Arquivo JSON (*.json)|*.json",
            FileName = $"{activeTab.Title.ToLowerInvariant().Replace(" ", "-")}.json"
        };

        if (saveDialog.ShowDialog() != true) return;

        try
        {
            var bundleObj = new
            {
                export_version = 3.0,
                packages = activeTab.Items.Select(app => new
                {
                    Id = app.Id,
                    Name = app.Name,
                    Version = app.Version,
                    Source = app.Source,
                    ManagerName = string.Equals(app.Source, "msstore", StringComparison.OrdinalIgnoreCase) ? "Microsoft Store" : "WinGet"
                }).ToList()
            };

            string json = JsonSerializer.Serialize(bundleObj, new JsonSerializerOptions { WriteIndented = true });
            await File.WriteAllTextAsync(saveDialog.FileName, json, Encoding.UTF8);

            StatusText.Text = $"Coleção salva com sucesso em \"{Path.GetFileName(saveDialog.FileName)}\".";
        }
        catch (Exception ex)
        {
            StatusText.Text = $"Erro ao salvar coleção: {ex.Message}";
        }
    }

    private async void ExportScriptButton_Click(object sender, RoutedEventArgs e)
    {
        var activeTab = _collectionService.ActiveTab;
        if (activeTab == null || activeTab.Items.Count == 0)
        {
            StatusText.Text = "Perfil vazio: nada para gerar.";
            return;
        }

        var saveFileDialog = new SaveFileDialog
        {
            Filter = "Script PowerShell (*.ps1)|*.ps1",
            FileName = $"{activeTab.Title.ToLowerInvariant().Replace(" ", "-")}-install.ps1",
            Title = "Salvar script de instalação PowerShell"
        };

        if (saveFileDialog.ShowDialog() != true) return;

        var scriptBuilder = new StringBuilder();
        scriptBuilder.AppendLine("# ========================================================");
        scriptBuilder.AppendLine($"# WinProvision - Script Automatizado de Instalação");
        scriptBuilder.AppendLine($"# Perfil / Coleção: {activeTab.Title}");
        scriptBuilder.AppendLine($"# Gerado em: {DateTime.Now:dd/MM/yyyy HH:mm:ss}");
        scriptBuilder.AppendLine("# ========================================================");
        scriptBuilder.AppendLine();
        scriptBuilder.AppendLine("$ErrorActionPreference = 'Continue'");
        scriptBuilder.AppendLine("Write-Host 'Iniciando instalação dos aplicativos...' -ForegroundColor Green");
        scriptBuilder.AppendLine();

        foreach (var app in activeTab.Items)
        {
            if (app.Office is not null)
            {
                scriptBuilder.AppendLine($"# {app.Name} é um plano de Office (ODT) — gerencie pela interface do WinProvision Store.");
                continue;
            }

            scriptBuilder.AppendLine($"Write-Host 'Instalando {app.Name} ({app.Id})...' -ForegroundColor Cyan");
            scriptBuilder.AppendLine($"winget install --id \"{app.Id}\" --exact --source {app.Source} --accept-source-agreements --disable-interactivity --silent --accept-package-agreements --force");
            scriptBuilder.AppendLine();
        }

        scriptBuilder.AppendLine("Write-Host 'Instalação da coleção concluída com sucesso!' -ForegroundColor Green");

        try
        {
            await File.WriteAllTextAsync(saveFileDialog.FileName, scriptBuilder.ToString(), Encoding.UTF8);
            StatusText.Text = $"Script PowerShell salvo em \"{Path.GetFileName(saveFileDialog.FileName)}\".";
        }
        catch
        {
            StatusText.Text = "Não foi possível salvar o script.";
        }
    }

    // ─── Remoção e Limpeza ───────────────────────────────────────────────────

    private void RemoveSelectedButton_Click(object sender, RoutedEventArgs e)
    {
        var activeTab = _collectionService.ActiveTab;
        if (activeTab == null || activeTab.Items.Count == 0) return;

        var selected = activeTab.Items.Where(a => a.IsSelectedForInstall).ToList();
        if (selected.Count == 0)
        {
            StatusText.Text = "Nenhum aplicativo selecionado para remover.";
            return;
        }

        foreach (var app in selected)
            activeTab.Items.Remove(app);

        UpdateStatus();
        StatusText.Text = $"{selected.Count} aplicativo(s) removido(s) da coleção.";
    }

    private async void ClearCollectionButton_Click(object sender, RoutedEventArgs e)
    {
        var activeTab = _collectionService.ActiveTab;
        if (activeTab == null || activeTab.Items.Count == 0) return;

        var confirmDialog = new Wpf.Ui.Controls.MessageBox
        {
            Title = "Limpar coleção",
            Content = $"Deseja remover todos os {activeTab.Items.Count} aplicativos da guia \"{activeTab.Title}\"?",
            PrimaryButtonText = "Limpar",
            CloseButtonText = "Cancelar"
        };

        var result = await confirmDialog.ShowDialogAsync();
        if (result == Wpf.Ui.Controls.MessageBoxResult.Primary)
        {
            activeTab.Items.Clear();
            UpdateStatus();
            StatusText.Text = "Coleção limpa.";
        }
    }

    private void RemoveButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { Tag: AppEntry app } && _collectionService.ActiveTab != null)
        {
            _collectionService.ActiveTab.Items.Remove(app);
            UpdateStatus();
        }
    }

    // ─── Cliques e Menus de Contexto ─────────────────────────────────────────

    private void TableRow_Click(object sender, MouseButtonEventArgs e)
    {
        if (sender is FrameworkElement { Tag: AppEntry app })
        {
            // Toggle seleção rápida ao clicar na linha
            app.IsSelectedForInstall = !app.IsSelectedForInstall;
        }
    }

    private void TableRow_RightClick(object sender, MouseButtonEventArgs e)
    {
        // Context menu já abre automaticamente no WPF
    }

    private void Card_Click(object sender, MouseButtonEventArgs e)
    {
        if (sender is FrameworkElement { Tag: AppEntry app })
        {
            app.IsSelectedForInstall = !app.IsSelectedForInstall;
        }
    }

    private AppEntry? GetContextApp(object sender) =>
        (sender as FrameworkElement)?.DataContext as AppEntry
        ?? (sender as FrameworkElement)?.Tag as AppEntry;

    private void ContextMenuInstall_Click(object sender, RoutedEventArgs e)
    {
        if (GetContextApp(sender) is { } app)
        {
            _ = OperationRunner.RunInstallAsync(_queue, _wingetExecutor, app.Id, app.Name, app.IconUrl, source: app.Source);
        }
    }

    private void ContextMenuInstallAdmin_Click(object sender, RoutedEventArgs e)
    {
        if (GetContextApp(sender) is { } app)
        {
            _ = OperationRunner.RunInstallAsync(_queue, _wingetExecutor, app.Id, app.Name, app.IconUrl, source: app.Source);
        }
    }

    private void ContextMenuDownload_Click(object sender, RoutedEventArgs e)
    {
        if (GetContextApp(sender) is { } app)
        {
            var dialog = new OpenFolderDialog
            {
                Title = $"Escolha a pasta para salvar {app.Name}"
            };

            if (dialog.ShowDialog() == true && !string.IsNullOrWhiteSpace(dialog.FolderName))
            {
                _ = Task.Run(() =>
                {
                    try
                    {
                        var psi = new ProcessStartInfo
                        {
                            FileName = "winget.exe",
                            Arguments = $"download --id \"{app.Id}\" --exact --source {app.Source} --download-directory \"{dialog.FolderName}\" --accept-source-agreements --accept-package-agreements",
                            UseShellExecute = false,
                            CreateNoWindow = true
                        };
                        Process.Start(psi);
                    }
                    catch { }
                });
            }
        }
    }

    private void ContextMenuRemove_Click(object sender, RoutedEventArgs e)
    {
        if (GetContextApp(sender) is { } app && _collectionService.ActiveTab != null)
        {
            _collectionService.ActiveTab.Items.Remove(app);
            UpdateStatus();
        }
    }

    private void ContextMenuDetails_Click(object sender, RoutedEventArgs e)
    {
        if (GetContextApp(sender) is { } app)
        {
            _overlayService.Show(app);
        }
    }

    private void ContextMenuCopyId_Click(object sender, RoutedEventArgs e)
    {
        if (GetContextApp(sender) is { } app)
        {
            Clipboard.SetText(app.Id);
            StatusText.Text = $"ID \"{app.Id}\" copiado para a área de transferência.";
        }
    }

    private void PackageDetailsToolbarButton_Click(object sender, RoutedEventArgs e)
    {
        var activeTab = _collectionService.ActiveTab;
        var app = activeTab?.Items.FirstOrDefault(a => a.IsSelectedForInstall) ?? activeTab?.Items.FirstOrDefault();

        if (app != null)
        {
            _overlayService.Show(app);
        }
        else
        {
            StatusText.Text = "Selecione ou clique em um aplicativo para ver os detalhes.";
        }
    }
}
