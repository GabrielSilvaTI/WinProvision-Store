using System;
using System.Collections.Generic;
using System.Diagnostics;
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
using Wpf.Ui.Controls;
using Wpf.Ui.Extensions;

namespace WinProvision.Store;

public partial class InstalledPackagesPage : Page
{
    private bool _suppressSortModeSelectionChanged;
    private readonly InstalledPackagesViewModel _viewModel;
    private readonly AppDetailsOverlayService _detailsOverlayService;
    private readonly PackageCollectionService _collectionService;
    private readonly IgnoredUpdatesService _ignoredUpdatesService;
    private readonly StoreService _storeService;
    private readonly WingetExecutor _wingetExecutor;
    private readonly OperationsQueueService _queue;
    private readonly ISnackbarService _snackbarService;
    private readonly IContentDialogService _contentDialogService;

    public InstalledPackagesPage()
    {
        InitializeComponent();

        _viewModel = App.Services.GetRequiredService<InstalledPackagesViewModel>();
        _detailsOverlayService = App.Services.GetRequiredService<AppDetailsOverlayService>();
        _collectionService = App.Services.GetRequiredService<PackageCollectionService>();
        _ignoredUpdatesService = App.Services.GetRequiredService<IgnoredUpdatesService>();
        _storeService = App.Services.GetRequiredService<StoreService>();
        _wingetExecutor = App.Services.GetRequiredService<WingetExecutor>();
        _queue = App.Services.GetRequiredService<OperationsQueueService>();
        _snackbarService = App.Services.GetRequiredService<ISnackbarService>();
        _contentDialogService = App.Services.GetRequiredService<IContentDialogService>();

        DataContext = _viewModel;

        Loaded += async (_, _) =>
        {
            if (_viewModel.Packages.Count == 0)
            {
                await _viewModel.LoadAsync();
            }
            RefreshSearchSuggestions();
            ApplyPackageFilters();
        };
    }

    // ─── Pesquisa e Filtros ──────────────────────────────────────────────────

    private void SearchBox_TextChanged(AutoSuggestBox sender, AutoSuggestBoxTextChangedEventArgs e) => ApplyPackageFilters();

    private void SearchBox_QuerySubmitted(AutoSuggestBox sender, AutoSuggestBoxQuerySubmittedEventArgs e) => ApplyPackageFilters();

    private void RefreshSearchSuggestions()
    {
        InstalledSearchBox.OriginalItemsSource = _viewModel.Packages
            .SelectMany(package => new[] { package.Name, package.Id })
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Take(300)
            .ToList();
    }

    private void FilterOption_Changed(object sender, RoutedEventArgs e) => ApplyPackageFilters();
    private void SourceChip_Changed(object sender, RoutedEventArgs e)
    {
        FilterAllSourcesChip.IsChecked = FilterWingetCheckBox.IsChecked == true
            && FilterStoreCheckBox.IsChecked == true
            && FilterLocalCheckBox.IsChecked == true;
        ApplyPackageFilters();
    }

    private void AllSourcesChip_Click(object sender, RoutedEventArgs e)
    {
        bool selectAll = FilterAllSourcesChip.IsChecked == true;
        FilterWingetCheckBox.IsChecked = selectAll;
        FilterStoreCheckBox.IsChecked = selectAll;
        FilterLocalCheckBox.IsChecked = selectAll;
        ApplyPackageFilters();
    }
    private void SearchMode_Changed(object sender, RoutedEventArgs e) => ApplyPackageFilters();

    private void SelectAllSources_Click(object sender, RoutedEventArgs e)
    {
        FilterAllSourcesChip.IsChecked = true;
        FilterWingetCheckBox.IsChecked = true;
        FilterStoreCheckBox.IsChecked = true;
        FilterLocalCheckBox.IsChecked = true;
        ApplyPackageFilters();
    }

    private void ClearSources_Click(object sender, RoutedEventArgs e)
    {
        FilterAllSourcesChip.IsChecked = false;
        FilterWingetCheckBox.IsChecked = false;
        FilterStoreCheckBox.IsChecked = false;
        FilterLocalCheckBox.IsChecked = false;
        ApplyPackageFilters();
    }

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

    private void ApplyPackageFilters()
    {
        string query = InstalledSearchBox?.Text?.Trim() ?? string.Empty;
        bool searchName = SearchModeNameRadio?.IsChecked == true;
        bool searchId = SearchModeIdRadio?.IsChecked == true;
        bool exact = SearchModeExactRadio?.IsChecked == true;
        bool onlySelected = FilterOnlySelectedCheckBox?.IsChecked == true;

        foreach (InstalledPackageRow package in _viewModel.Packages)
        {
            bool sourceMatch = package.SourceCategory switch
            {
                "WinGet" => FilterWingetCheckBox?.IsChecked == true,
                "Microsoft Store" => FilterStoreCheckBox?.IsChecked == true,
                _ => FilterLocalCheckBox?.IsChecked == true
            };

            bool nameMatch = exact
                ? string.Equals(package.Name, query, StringComparison.OrdinalIgnoreCase)
                : package.Name.Contains(query, StringComparison.OrdinalIgnoreCase);
            bool idMatch = exact
                ? string.Equals(package.Id, query, StringComparison.OrdinalIgnoreCase)
                : package.Id.Contains(query, StringComparison.OrdinalIgnoreCase);

            bool queryMatch = string.IsNullOrEmpty(query)
                || (exact && (nameMatch || idMatch))
                || (searchName && nameMatch)
                || (searchId && idMatch)
                || (SearchModeBothRadio?.IsChecked == true && (nameMatch || idMatch))
                || (SearchModeAllRadio?.IsChecked == true && (nameMatch || idMatch));

            bool removableMatch = FilterRemovableCheckBox?.IsChecked != true || package.CanRemove;
            bool systemMatch = FilterHideSystemCheckBox?.IsChecked != true || !package.IsSystemComponent;
            bool selectedMatch = !onlySelected || package.IsSelected;

            package.SetSearchMatch(sourceMatch && queryMatch && removableMatch && systemMatch && selectedMatch);
        }

        _viewModel.RefreshFilteredState();
        UpdateMasterSelectAllState();
    }

    // ─── Desinstalação e Variantes ───────────────────────────────────────────

    private async void Remove_Click(object sender, RoutedEventArgs e)
    {
        var selected = _viewModel.Packages.Where(p => p.IsSearchMatch && p.IsSelected && p.CanRemove).ToList();
        if (selected.Count == 0)
        {
            _viewModel.Status = "Selecione ao menos um pacote com suporte à desinstalação.";
            return;
        }

        var result = await _contentDialogService.ShowSimpleDialogAsync(new SimpleContentDialogCreateOptions
        {
            Title = "Desinstalar aplicativos?",
            Content = $"Os {selected.Count} aplicativos selecionados serão removidos do computador.",
            PrimaryButtonText = "Desinstalar",
            CloseButtonText = "Cancelar"
        });

        if (result == ContentDialogResult.Primary)
        {
            await _viewModel.RemoveSelectedAsync();
            RefreshSearchSuggestions();
            ApplyPackageFilters();
            bool completed = _viewModel.Status.StartsWith("Desinstalação concluída", StringComparison.OrdinalIgnoreCase);
            _snackbarService.Show(completed ? "Desinstalação concluída" : "Desinstalação parcial",
                _viewModel.Status,
                completed ? ControlAppearance.Success : ControlAppearance.Caution,
                new SymbolIcon(completed ? SymbolRegular.CheckmarkCircle24 : SymbolRegular.Warning24),
                TimeSpan.FromSeconds(4));
        }
    }

    private void UninstallVariantsButton_Click(object sender, RoutedEventArgs e)
    {
        var menu = new ContextMenu();

        var adminItem = new System.Windows.Controls.MenuItem { Header = "Desinstalar como administrador" };
        adminItem.Icon = new Wpf.Ui.Controls.SymbolIcon { Symbol = SymbolRegular.Shield24 };
        adminItem.Click += async (_, _) =>
        {
            await _viewModel.RemoveSelectedAsync();
            ApplyPackageFilters();
        };
        menu.Items.Add(adminItem);

        var interactiveItem = new System.Windows.Controls.MenuItem { Header = "Desinstalação interativa" };
        interactiveItem.Icon = new Wpf.Ui.Controls.SymbolIcon { Symbol = SymbolRegular.Window24 };
        interactiveItem.Click += async (_, _) =>
        {
            await _viewModel.RemoveSelectedAsync(interactive: true);
            ApplyPackageFilters();
        };
        menu.Items.Add(interactiveItem);

        menu.Items.Add(new Separator());

        var downloadItem = new System.Windows.Controls.MenuItem { Header = "Baixar instaladores selecionados" };
        downloadItem.Icon = new Wpf.Ui.Controls.SymbolIcon { Symbol = SymbolRegular.ArrowDownload24 };
        downloadItem.Click += (_, _) => DownloadSelectedInstallers();
        menu.Items.Add(downloadItem);

        menu.PlacementTarget = UninstallVariantsButton;
        menu.IsOpen = true;
    }

    private async void Refresh_Click(object sender, RoutedEventArgs e)
    {
        await _viewModel.LoadAsync();
        ApplyPackageFilters();
    }

    private async void OptionsButton_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new Wpf.Ui.Controls.MessageBox
        {
            Title = "Opções de Desinstalação",
            Content = "WinProvision Store utiliza mecanismos nativos de desinstalação:\n\n• Desinstalação silenciosa de pacotes WinGet e MSIX\n• Fallback automático para comandos locais do Registro do Windows (ARP)\n• Remoção completa de pacotes Office C2R via ODT",
            CloseButtonText = "OK"
        };
        StoreDialogStyles.Apply(dialog);
        await dialog.ShowDialogAsync();
    }

    private void ManualUninstall_Click(object sender, RoutedEventArgs e)
    {
        var target = _viewModel.Packages.FirstOrDefault(p => p.IsSelected)
                     ?? _viewModel.Packages.FirstOrDefault(p => p.IsSearchMatch);

        if (target == null)
        {
            _viewModel.Status = "Selecione um pacote na lista para executar a desinstalação manual.";
            return;
        }

        ExecuteManualUninstall(target);
    }

    private void ExecuteManualUninstall(InstalledPackageRow target)
    {
        string cmd = !string.IsNullOrWhiteSpace(target.Package.UninstallString)
            ? target.Package.UninstallString
            : $"winget uninstall --id \"{target.Id}\"";

        var dialog = new Wpf.Ui.Controls.MessageBox
        {
            Title = "Desinstalação manual",
            Content = $"Comando de desinstalação para {target.Name}:\n\n{cmd}\n\nDeseja abrir o terminal e executar o comando?",
            PrimaryButtonText = "Abrir Terminal",
            SecondaryButtonText = "Copiar Comando",
            CloseButtonText = "Cancelar"
        };

        StoreDialogStyles.Apply(dialog);
        _ = dialog.ShowDialogAsync().ContinueWith(t =>
        {
            Dispatcher.Invoke(() =>
            {
                if (t.Result == Wpf.Ui.Controls.MessageBoxResult.Primary)
                {
                    try
                    {
                        Process.Start(new ProcessStartInfo
                        {
                            FileName = "cmd.exe",
                            Arguments = $"/k {cmd}",
                            UseShellExecute = true
                        });
                        _viewModel.Status = $"Terminal iniciado para desinstalação de '{target.Name}'.";
                    }
                    catch (Exception ex)
                    {
                        _viewModel.Status = $"Erro ao abrir terminal: {ex.Message}";
                    }
                }
                else if (t.Result == Wpf.Ui.Controls.MessageBoxResult.Secondary)
                {
                    Clipboard.SetText(cmd);
                    _viewModel.Status = "Comando copiado para a área de transferência.";
                }
            });
        });
    }

    // ─── Coleção e Exportação ────────────────────────────────────────────────

    private void AddToCollectionButton_Click(object sender, RoutedEventArgs e)
    {
        var selected = _viewModel.Packages.Where(p => p.IsSearchMatch && p.IsSelected).ToList();
        var installable = selected.Where(p => p.CanAddToCollection).ToList();
        if (installable.Count == 0)
        {
            _viewModel.Status = selected.Count == 0
                ? "Selecione um pacote WinGet ou Microsoft Store para adicionar à coleção."
                : "Pacotes locais não podem ser adicionados à coleção, pois não podem ser instalados pelo WinProvision.";
            return;
        }

        var activeTab = _collectionService.ActiveTab ?? _collectionService.CreateNewTab();
        int added = 0;
        foreach (var row in installable)
        {
            if (!activeTab.Items.Any(i => string.Equals(i.Id, row.Id, StringComparison.OrdinalIgnoreCase)))
            {
                activeTab.Items.Add(ToAppEntry(row));
                added++;
            }
        }

        int skipped = selected.Count - installable.Count;
        _viewModel.Status = skipped > 0
            ? $"{added} pacote(s) adicionado(s) à coleção '{activeTab.Title}'; {skipped} pacote(s) local(is) ignorado(s)."
            : $"{added} pacote(s) adicionado(s) à coleção '{activeTab.Title}'.";
    }

    private void ExportCsvButton_Click(object sender, RoutedEventArgs e)
    {
        var list = _viewModel.Packages.Where(p => p.IsSearchMatch).ToList();
        if (list.Count == 0)
        {
            _viewModel.Status = "Nenhum pacote disponível para exportar.";
            return;
        }

        var saveDialog = new Microsoft.Win32.SaveFileDialog
        {
            Title = "Exportar Pacotes Instalados para CSV",
            Filter = "Arquivo CSV (*.csv)|*.csv",
            FileName = $"PacotesInstalados_{DateTime.Now:yyyyMMdd_HHmm}.csv"
        };

        if (saveDialog.ShowDialog() != true) return;

        try
        {
            var sb = new StringBuilder();
            sb.AppendLine("Nome,ID,Versao,Origem,Escopo,LocalInstalacao");
            foreach (var p in list)
            {
                sb.AppendLine($"\"{EscapeCsv(p.Name)}\",\"{EscapeCsv(p.Id)}\",\"{EscapeCsv(p.Version)}\",\"{EscapeCsv(p.SourceLabel)}\",\"{EscapeCsv(p.Scope)}\",\"{EscapeCsv(p.Package.InstallLocation)}\"");
            }

            File.WriteAllText(saveDialog.FileName, sb.ToString(), Encoding.UTF8);
            _viewModel.Status = $"Exportado para '{Path.GetFileName(saveDialog.FileName)}' com sucesso.";
            _snackbarService.Show("Exportação concluída", _viewModel.Status, ControlAppearance.Success,
                new SymbolIcon(SymbolRegular.CheckmarkCircle24), TimeSpan.FromSeconds(3));
        }
        catch (Exception ex)
        {
            _viewModel.Status = $"Erro ao exportar CSV: {ex.Message}";
        }
    }

    private static string EscapeCsv(string value) => (value ?? string.Empty).Replace("\"", "\"\"");

    private async void DownloadSelectedInstallers()
    {
        var selected = _viewModel.Packages.Where(p => p.IsSearchMatch && p.IsSelected).ToList();
        if (selected.Count == 0)
        {
            _viewModel.Status = "Selecione ao menos um pacote para baixar o instalador.";
            return;
        }

        var dialog = new Microsoft.Win32.OpenFolderDialog
        {
            Title = "Escolha a pasta de destino para os instaladores"
        };

        if (dialog.ShowDialog() != true || string.IsNullOrWhiteSpace(dialog.FolderName))
            return;

        string targetFolder = dialog.FolderName;
        int downloaded = 0;
        var failures = new List<string>();
        for (int index = 0; index < selected.Count; index++)
        {
            var app = selected[index];
            _viewModel.Status = $"Baixando instalador {index + 1} de {selected.Count}: {app.Name}...";
            try
            {
                string source = app.SourceCategory == "Microsoft Store" ? "msstore" : "winget";
                var result = await _wingetExecutor.DownloadInstallerAsync(app.Id, targetFolder, source);
                if (result.Success) downloaded++;
                else failures.Add(app.Name);
            }
            catch (Exception ex)
            {
                failures.Add(app.Name);
                WinProvisionLog.Write($"DOWNLOAD INSTALLER FAILED id=\"{app.Id}\" error=\"{ex.Message}\"");
            }
        }

        _viewModel.Status = failures.Count == 0
            ? $"{downloaded} instalador(es) baixado(s) em {targetFolder}."
            : $"{downloaded} baixado(s); falha em {failures.Count}: {string.Join(", ", failures)}.";
        _snackbarService.Show(
            failures.Count == 0 ? "Downloads concluídos" : "Downloads parcialmente concluídos",
            _viewModel.Status,
            failures.Count == 0 ? ControlAppearance.Success : ControlAppearance.Caution,
            new SymbolIcon(failures.Count == 0 ? SymbolRegular.CheckmarkCircle24 : SymbolRegular.Warning24),
            TimeSpan.FromSeconds(4));
    }

    // ─── Atualizações Ignoradas ──────────────────────────────────────────────

    private void IgnoreSelectedButton_Click(object sender, RoutedEventArgs e)
    {
        var selected = _viewModel.Packages.Where(p => p.IsSearchMatch && p.IsSelected).ToList();
        if (selected.Count == 0)
        {
            _viewModel.Status = "Nenhum pacote selecionado para ignorar atualizações.";
            return;
        }

        foreach (var pkg in selected)
        {
            _ignoredUpdatesService.Ignore(pkg.Id, pkg.Version);
        }

        _viewModel.Status = $"{selected.Count} pacote(s) ignorado(s) nas atualizações.";
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
        var list = _ignoredUpdatesService.GetIgnoredUpdates();
        IgnoredUpdatesItemsControl.ItemsSource = list;
        IgnoredUpdatesEmptyModalText.Visibility = list.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    private void RestoreIgnoredUpdate_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: IgnoredUpdateEntry entry })
        {
            _ignoredUpdatesService.Unignore(entry.AppId);
            RefreshIgnoredUpdates();
            _viewModel.Status = $"Atualizações para '{entry.AppId}' restauradas.";
        }
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
        _viewModel.Status = "Todas as atualizações ignoradas foram restauradas.";
    }

    // ─── Ordenação da Tabela ─────────────────────────────────────────────────

    private void SortByName_Click(object sender, RoutedEventArgs e) => SortColumn("Name");
    private void SortById_Click(object sender, RoutedEventArgs e) => SortColumn("Id");
    private void SortByVersion_Click(object sender, RoutedEventArgs e) => SortColumn("Version");
    private void SortBySource_Click(object sender, RoutedEventArgs e) => SortColumn("Source");

    private void SortColumn(string column)
    {
        _viewModel.ApplySort(column);
        UpdateSortIndicators(_viewModel.CurrentSortColumn, _viewModel.CurrentSortAscending);
    }

    private void UpdateSortIndicators(string column, bool ascending)
    {
        SortNameIcon.Visibility = Visibility.Collapsed;
        SortIdIcon.Visibility = Visibility.Collapsed;
        SortVersionIcon.Visibility = Visibility.Collapsed;
        SortSourceIcon.Visibility = Visibility.Collapsed;

        var symbol = ascending ? SymbolRegular.ChevronDown12 : SymbolRegular.ChevronUp12;
        switch (column.ToLowerInvariant())
        {
            case "id":
                SortIdIcon.Symbol = symbol;
                SortIdIcon.Visibility = Visibility.Visible;
                SetSortModeSelection(1);
                break;
            case "version":
                SortVersionIcon.Symbol = symbol;
                SortVersionIcon.Visibility = Visibility.Visible;
                SetSortModeSelection(2);
                break;
            case "source":
                SortSourceIcon.Symbol = symbol;
                SortSourceIcon.Visibility = Visibility.Visible;
                SetSortModeSelection(3);
                break;
            default:
                SortNameIcon.Symbol = symbol;
                SortNameIcon.Visibility = Visibility.Visible;
                SetSortModeSelection(0);
                break;
        }
    }

    private void SetSortModeSelection(int selectedIndex)
    {
        if (SortModeComboBox.SelectedIndex == selectedIndex) return;
        _suppressSortModeSelectionChanged = true;
        SortModeComboBox.SelectedIndex = selectedIndex;
        _suppressSortModeSelectionChanged = false;
    }

    private void SortModeComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!IsLoaded || _suppressSortModeSelectionChanged) return;
        string column = SortModeComboBox.SelectedIndex switch
        {
            1 => "Id",
            2 => "Version",
            3 => "Source",
            _ => "Name"
        };
        _viewModel.ApplySort(column, true);
        UpdateSortIndicators(column, true);
    }

    // ─── Seleção e Modos de Exibição ─────────────────────────────────────────

    private void ToggleSelectAll_Click(object sender, RoutedEventArgs e)
    {
        bool select = MasterSelectAllCheckBox.IsChecked == true;
        foreach (var package in _viewModel.Packages.Where(x => x.IsSearchMatch && x.CanSelect))
            package.IsSelected = select;

        _viewModel.SelectionChanged();
        UpdateMasterSelectAllState();
    }

    private void RowCheckBox_Click(object sender, RoutedEventArgs e)
    {
        e.Handled = true;
        _viewModel.SelectionChanged();
        UpdateMasterSelectAllState();
    }

    private void Row_Click(object sender, MouseButtonEventArgs e)
    {
        if (IsCheckBoxSource(e.OriginalSource as DependencyObject))
            return;

        if (sender is FrameworkElement { DataContext: InstalledPackageRow row } && row.CanSelect)
        {
            row.IsSelected = !row.IsSelected;
            UpdateMasterSelectAllState();
        }
    }

    private static bool IsCheckBoxSource(DependencyObject? source)
    {
        while (source is not null)
        {
            if (source is CheckBox)
                return true;

            source = source is System.Windows.Media.Visual or System.Windows.Media.Media3D.Visual3D
                ? System.Windows.Media.VisualTreeHelper.GetParent(source)
                : LogicalTreeHelper.GetParent(source);
        }

        return false;
    }

    private void Row_RightClick(object sender, MouseButtonEventArgs e)
    {
        // ContextMenu abre nativamente
    }

    private void UpdateMasterSelectAllState()
    {
        var visible = _viewModel.Packages.Where(p => p.IsSearchMatch && p.CanSelect).ToList();
        if (visible.Count == 0)
        {
            MasterSelectAllCheckBox.IsChecked = false;
            return;
        }
        int selected = visible.Count(p => p.IsSelected);
        if (selected == visible.Count)
            MasterSelectAllCheckBox.IsChecked = true;
        else if (selected == 0)
            MasterSelectAllCheckBox.IsChecked = false;
        else
            MasterSelectAllCheckBox.IsChecked = null;
    }

    private void GridViewToggleButton_Click(object sender, RoutedEventArgs e)
    {
        GridViewScrollViewer.Visibility = Visibility.Visible;
        ListViewScrollViewer.Visibility = Visibility.Collapsed;
        IconsViewScrollViewer.Visibility = Visibility.Collapsed;
        TableHeaderBar.Visibility = Visibility.Collapsed;
        GridViewToggleButton.IsChecked = true;
        ListViewToggleButton.IsChecked = false;
        IconsViewToggleButton.IsChecked = false;
    }

    private void ListViewToggleButton_Click(object sender, RoutedEventArgs e)
    {
        GridViewScrollViewer.Visibility = Visibility.Collapsed;
        ListViewScrollViewer.Visibility = Visibility.Visible;
        IconsViewScrollViewer.Visibility = Visibility.Collapsed;
        TableHeaderBar.Visibility = Visibility.Visible;
        GridViewToggleButton.IsChecked = false;
        ListViewToggleButton.IsChecked = true;
        IconsViewToggleButton.IsChecked = false;
    }

    private void IconsViewToggleButton_Click(object sender, RoutedEventArgs e)
    {
        GridViewScrollViewer.Visibility = Visibility.Collapsed;
        ListViewScrollViewer.Visibility = Visibility.Collapsed;
        IconsViewScrollViewer.Visibility = Visibility.Visible;
        TableHeaderBar.Visibility = Visibility.Collapsed;
        GridViewToggleButton.IsChecked = false;
        ListViewToggleButton.IsChecked = false;
        IconsViewToggleButton.IsChecked = true;
    }

    // ─── Detalhes do Pacote ──────────────────────────────────────────────────

    private void PackageName_Click(object sender, MouseButtonEventArgs e)
    {
        e.Handled = true;
        if (sender is FrameworkElement { DataContext: InstalledPackageRow row })
        {
            _detailsOverlayService.Show(ToAppEntry(row));
        }
    }

    private void PackageDetailsToolbarButton_Click(object sender, RoutedEventArgs e)
    {
        var row = _viewModel.Packages.FirstOrDefault(p => p.IsSelected)
                  ?? _viewModel.Packages.FirstOrDefault(p => p.IsSearchMatch);

        if (row != null)
        {
            _detailsOverlayService.Show(ToAppEntry(row));
        }
        else
        {
            _viewModel.Status = "Selecione um pacote para exibir os detalhes.";
        }
    }

    private AppEntry ToAppEntry(InstalledPackageRow row)
    {
        var catalog = _storeService.GetAll();
        var match = row.CanAddToCollection ? catalog.FirstOrDefault(a =>
            string.Equals(a.Id, row.Id, StringComparison.OrdinalIgnoreCase)
            || string.Equals(a.Name, row.Name, StringComparison.OrdinalIgnoreCase)) : null;

        return match ?? new AppEntry
        {
            Id = row.Id,
            Name = row.Name,
            Version = row.Version,
            Source = row.SourceCategory switch
            {
                "WinGet" => "winget",
                "Microsoft Store" => "msstore",
                _ => string.IsNullOrWhiteSpace(row.Source) ? "local" : row.Source
            },
            IconUrl = row.IconUrl,
            Description = $"Aplicativo instalado ({row.SourceLabel})"
        };
    }

    // ─── Menu de Contexto ────────────────────────────────────────────────────

    private InstalledPackageRow? GetContextRow(object sender) =>
        (sender as FrameworkElement)?.DataContext as InstalledPackageRow;

    private async void ContextMenuUninstall_Click(object sender, RoutedEventArgs e)
    {
        if (GetContextRow(sender) is { } row)
        {
            await _viewModel.RemoveByIdentityAsync(row.Id, row.Name, row.IconUrl);
            ApplyPackageFilters();
        }
    }

    private async void ContextMenuUninstallAdmin_Click(object sender, RoutedEventArgs e)
    {
        if (GetContextRow(sender) is { } row)
        {
            await _viewModel.RemoveByIdentityAsync(row.Id, row.Name, row.IconUrl);
            ApplyPackageFilters();
        }
    }

    private async void ContextMenuUninstallInteractive_Click(object sender, RoutedEventArgs e)
    {
        if (GetContextRow(sender) is { } row)
        {
            await _viewModel.RemoveByIdentityAsync(row.Id, row.Name, row.IconUrl, interactive: true);
            ApplyPackageFilters();
        }
    }

    private void ContextMenuManualUninstall_Click(object sender, RoutedEventArgs e)
    {
        if (GetContextRow(sender) is { } row)
        {
            ExecuteManualUninstall(row);
        }
    }

    private void ContextMenuOpenLocation_Click(object sender, RoutedEventArgs e)
    {
        if (GetContextRow(sender) is { } row)
        {
            string loc = row.Package.InstallLocation;
            if (!string.IsNullOrWhiteSpace(loc) && Directory.Exists(loc))
            {
                Process.Start(new ProcessStartInfo { FileName = "explorer.exe", Arguments = $"\"{loc}\"", UseShellExecute = true });
            }
            else
            {
                _viewModel.Status = $"Local de instalação não registrado para '{row.Name}'.";
            }
        }
    }

    private async void ContextMenuDownload_Click(object sender, RoutedEventArgs e)
    {
        if (GetContextRow(sender) is { } row)
        {
            var dialog = new Microsoft.Win32.OpenFolderDialog
            {
                Title = $"Escolha a pasta para salvar {row.Name}"
            };

            if (dialog.ShowDialog() == true && !string.IsNullOrWhiteSpace(dialog.FolderName))
            {
                _viewModel.Status = $"Baixando instalador de {row.Name}...";
                try
                {
                    string source = row.SourceCategory == "Microsoft Store" ? "msstore" : "winget";
                    var result = await _wingetExecutor.DownloadInstallerAsync(row.Id, dialog.FolderName, source);
                    _viewModel.Status = result.Success
                        ? $"Instalador de {row.Name} baixado com sucesso."
                        : $"Não foi possível baixar {row.Name}. Confira os detalhes do WinGet.";
                }
                catch (Exception ex)
                {
                    _viewModel.Status = $"Falha ao baixar {row.Name}: {ex.Message}";
                }
            }
        }
    }

    private void ContextMenuIgnore_Click(object sender, RoutedEventArgs e)
    {
        if (GetContextRow(sender) is { } row)
        {
            _ignoredUpdatesService.Ignore(row.Id, row.Version);
            _viewModel.Status = $"Atualizações para '{row.Name}' ignoradas.";
        }
    }

    private void ContextMenuAddToCollection_Click(object sender, RoutedEventArgs e)
    {
        if (GetContextRow(sender) is { } row)
        {
            if (!row.CanAddToCollection)
            {
                _viewModel.Status = "Pacotes locais não podem ser adicionados à coleção, pois não podem ser instalados pelo WinProvision.";
                return;
            }

            var activeTab = _collectionService.ActiveTab ?? _collectionService.CreateNewTab();
            if (!activeTab.Items.Any(i => string.Equals(i.Id, row.Id, StringComparison.OrdinalIgnoreCase)))
            {
                activeTab.Items.Add(ToAppEntry(row));
                _viewModel.Status = $"'{row.Name}' adicionado à coleção '{activeTab.Title}'.";
                _snackbarService.Show("Adicionado à coleção", _viewModel.Status, ControlAppearance.Success,
                    new SymbolIcon(SymbolRegular.CheckmarkCircle24), TimeSpan.FromSeconds(3));
            }
            else
            {
                _viewModel.Status = $"'{row.Name}' já está na coleção.";
            }
        }
    }

    private void ContextMenuCopyId_Click(object sender, RoutedEventArgs e)
    {
        if (GetContextRow(sender) is { } row)
        {
            Clipboard.SetText(row.Id);
            _viewModel.Status = $"ID '{row.Id}' copiado para a área de transferência.";
        }
    }

    private void ContextMenuCopyName_Click(object sender, RoutedEventArgs e)
    {
        if (GetContextRow(sender) is { } row)
        {
            Clipboard.SetText(row.Name);
            _viewModel.Status = $"Nome '{row.Name}' copiado para a área de transferência.";
        }
    }

    private void ContextMenuDetails_Click(object sender, RoutedEventArgs e)
    {
        if (GetContextRow(sender) is { } row)
        {
            _detailsOverlayService.Show(ToAppEntry(row));
        }
    }
}
