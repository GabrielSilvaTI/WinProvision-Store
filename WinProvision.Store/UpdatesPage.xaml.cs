using System;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using Microsoft.Extensions.DependencyInjection;
using WinProvision.Core.Models;
using WinProvision.Core.Services;
using WinProvision.Store.Services;

namespace WinProvision.Store;

public partial class UpdatesPage : Page
{
    private readonly WingetExecutor _wingetExecutor;
    private readonly WinGetService _winGetService;
    private readonly StoreService _storeService;
    private readonly OperationsQueueService _queue;
    private readonly ScheduledUpdatesService _scheduledUpdatesService;
    private readonly IgnoredUpdatesService _ignoredUpdatesService;
    private readonly AppDetailsOverlayService _detailsOverlayService;

    private readonly ObservableCollection<UpgradablePackage> _packages = new();

    private bool _suppressAutoUpdateToggleEvent;

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

        UpdatesList.ItemsSource = _packages;
        UpdatesListView.ItemsSource = _packages;
        SetViewMode(list: false);

        Loaded += async (_, _) =>
        {
            await CheckUpdatesAsync();
            await LoadAutoUpdateStateAsync();
        };
    }

    // ----------------------------------------------------------------
    // Atualizações Automáticas (Tarefa Agendada)
    // ----------------------------------------------------------------

    private async Task LoadAutoUpdateStateAsync()
    {
        _suppressAutoUpdateToggleEvent = true;
        try
        {
            bool isEnabled = await _scheduledUpdatesService.IsEnabledAsync();
            AutoUpdateToggle.IsChecked = isEnabled;
        }
        finally
        {
            _suppressAutoUpdateToggleEvent = false;
        }
    }

    private async void AutoUpdateToggle_Checked(object sender, RoutedEventArgs e)
    {
        if (_suppressAutoUpdateToggleEvent) return;

        AutoUpdateToggle.IsEnabled = false;
        var result = await _scheduledUpdatesService.EnableAsync();

        if (!result.Success)
        {
            _suppressAutoUpdateToggleEvent = true;
            AutoUpdateToggle.IsChecked = false;
            _suppressAutoUpdateToggleEvent = false;
            StatusText.Text = FormatScheduledTaskFailure("ativar", result);
        }
        else
        {
            StatusText.Text = "Atualizações automáticas ativadas (Ao iniciar o PC).";
        }

        AutoUpdateToggle.IsEnabled = true;
    }

    private async void AutoUpdateToggle_Unchecked(object sender, RoutedEventArgs e)
    {
        if (_suppressAutoUpdateToggleEvent) return;

        AutoUpdateToggle.IsEnabled = false;
        var result = await _scheduledUpdatesService.DisableAsync();

        if (!result.Success)
        {
            _suppressAutoUpdateToggleEvent = true;
            AutoUpdateToggle.IsChecked = true;
            _suppressAutoUpdateToggleEvent = false;
            StatusText.Text = FormatScheduledTaskFailure("desativar", result);
        }
        else
        {
            StatusText.Text = "Atualizações automáticas desativadas.";
        }

        AutoUpdateToggle.IsEnabled = true;
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

    // ----------------------------------------------------------------
    // Verificar atualizações (winget upgrade)
    // ----------------------------------------------------------------

    private async void CheckUpdatesButton_Click(object sender, RoutedEventArgs e) => await CheckUpdatesAsync();

    private void GridViewToggleButton_Click(object sender, RoutedEventArgs e) => SetViewMode(list: false);

    private void ListViewToggleButton_Click(object sender, RoutedEventArgs e) => SetViewMode(list: true);

    private void SetViewMode(bool list)
    {
        GridViewScrollViewer.Visibility = list ? Visibility.Collapsed : Visibility.Visible;
        ListViewScrollViewer.Visibility = list ? Visibility.Visible : Visibility.Collapsed;
        GridViewToggleButton.IsChecked = !list;
        ListViewToggleButton.IsChecked = list;

        Brush accent = TryFindResource("SystemAccentColorPrimaryBrush") as Brush ?? SystemColors.HighlightBrush;
        Brush primaryText = TryFindResource("TextFillColorPrimaryBrush") as Brush ?? SystemColors.ControlTextBrush;
        GridViewToggleButton.Background = !list ? accent : Brushes.Transparent;
        ListViewToggleButton.Background = list ? accent : Brushes.Transparent;
        GridViewToggleButton.Foreground = !list ? Brushes.White : primaryText;
        ListViewToggleButton.Foreground = list ? Brushes.White : primaryText;
    }

    private async Task CheckUpdatesAsync()
    {
        CheckUpdatesButton.IsEnabled = false;
        UpdateSelectedButton.IsEnabled = false;
        BusyIndicator.Visibility = Visibility.Visible;
        StatusText.Text = "Procurando atualizações...";

        try
        {
            var upgradable = await _winGetService.GetUpgradablePackagesAsync(
                onLogReceived: line => StatusText.Text = line);

            var catalog = _storeService.GetAll();
            foreach (var package in upgradable)
            {
                var match = catalog.FirstOrDefault(a => string.Equals(a.Id, package.Id, StringComparison.OrdinalIgnoreCase));
                if (match is not null)
                {
                    package.IconUrl = match.IconUrl;
                }
            }

            _packages.Clear();
            foreach (var package in upgradable)
            {
                if (_ignoredUpdatesService.IsIgnored(package.Id, package.AvailableVersion))
                {
                    continue;
                }
                _packages.Add(package);
            }

            SetSelectAllButtonState(0);
            LastSearchText.Text = $"Última busca: Hoje às {DateTime.Now:HH:mm}";

            StatusText.Text = _packages.Count == 0
                ? "Nenhuma atualização disponível. Tudo em dia."
                : FormatAvailableUpdatesMessage(_packages.Count);
        }
        catch (Exception ex)
        {
            WinProvisionLog.Write($"UPDATE UI discovery failed {ex.GetType().Name}: {ex.Message}");
            StatusText.Text = "Não foi possível verificar atualizações. Tente novamente.";
        }
        finally
        {
            CheckUpdatesButton.IsEnabled = true;
            UpdateSelectedButton.IsEnabled = true;
            BusyIndicator.Visibility = Visibility.Collapsed;
            SyncSelectAllCheckBoxState();
        }
    }

    private static string FormatAvailableUpdatesMessage(int count)
    {
        return count == 1
            ? "Há 1 atualização disponível."
            : $"Há {count} atualizações disponíveis.";
    }

    // ----------------------------------------------------------------
    // Seleção (checkbox por item + "Selecionar todos")
    // ----------------------------------------------------------------

    private void UpdateItemCard_Click(object sender, MouseButtonEventArgs e)
    {
        if (e.OriginalSource is DependencyObject source && FindVisualParent<Button>(source) is not null)
        {
            return;
        }

        if (sender is not FrameworkElement element
            || element.DataContext is not UpgradablePackage package
            || package.IsUpdating)
        {
            return;
        }

        package.IsSelectedForUpdate = !package.IsSelectedForUpdate;
        SyncSelectAllCheckBoxState();
        e.Handled = true;
    }

    private static T? FindVisualParent<T>(DependencyObject? child)
        where T : DependencyObject
    {
        while (child is not null)
        {
            if (child is T parent)
            {
                return parent;
            }

            child = VisualTreeHelper.GetParent(child);
        }

        return null;
    }

    private void SelectAllCheckBox_Click(object sender, RoutedEventArgs e)
    {
        if (_packages.Count == 0) return;

        bool selectAll = !_packages.All(p => p.IsSelectedForUpdate);

        foreach (var package in _packages)
        {
            package.IsSelectedForUpdate = selectAll;
        }

        SetSelectAllButtonState(selectAll ? _packages.Count : 0);
    }

    private void SyncSelectAllCheckBoxState()
    {
        int selected = _packages.Count(p => p.IsSelectedForUpdate);

        SetSelectAllButtonState(selected);
    }

    private void SetSelectAllButtonState(int selected)
    {
        bool allSelected = _packages.Count > 0 && selected == _packages.Count;
        SelectAllText.Text = allSelected ? "Desmarcar todos" : "Selecionar todos";
        SelectAllIcon.Symbol = allSelected
            ? Wpf.Ui.Controls.SymbolRegular.DismissCircle24
            : Wpf.Ui.Controls.SymbolRegular.CheckmarkCircle24;
    }

    // ----------------------------------------------------------------
    // Atualizar selecionados (winget update)
    // ----------------------------------------------------------------

    private async void UpdateSelectedButton_Click(object sender, RoutedEventArgs e)
    {
        var selected = _packages.Where(p => p.IsSelectedForUpdate).ToList();

        if (selected.Count == 0)
        {
            StatusText.Text = "Selecione um aplicativo.";
            return;
        }

        UpdateSelectedButton.IsEnabled = false;
        CheckUpdatesButton.IsEnabled = false;
        StatusText.Text = "Atualizando aplicativos...";

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
                    string.IsNullOrWhiteSpace(package.Source) ? "winget" : package.Source);

                if (result.Success)
                {
                    succeeded++;
                    _packages.Remove(package);
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
            ? "Atualização concluída."
            : $"Atualização concluída. Falhas: {failed}. Veja a fila para detalhes.";

        SyncSelectAllCheckBoxState();
        UpdateSelectedButton.IsEnabled = true;
        CheckUpdatesButton.IsEnabled = true;
    }

    private void SingleUpdateButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement element && element.DataContext is UpgradablePackage appToUpdate)
        {
            appToUpdate.IsSelectedForUpdate = true;
            UpdateSelectedButton_Click(sender, e);
        }
    }

    // ----------------------------------------------------------------
    // Menu de Contexto (Três Pontinhos)
    // ----------------------------------------------------------------

    private void IgnoreVersionMenuItem_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement element && element.DataContext is UpgradablePackage package)
        {
            _ignoredUpdatesService.Ignore(package.Id, package.AvailableVersion);
            _packages.Remove(package);
            SyncSelectAllCheckBoxState();
            StatusText.Text = $"Versão {package.AvailableVersion} de {package.Name} ignorada.";
        }
    }

    private void DetailsMenuItem_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement element && element.DataContext is UpgradablePackage package)
        {
            var catalog = _storeService.GetAll();
            var appEntry = catalog.FirstOrDefault(a => string.Equals(a.Id, package.Id, StringComparison.OrdinalIgnoreCase));

            if (appEntry is not null)
            {
                _detailsOverlayService.Show(appEntry);
            }
            else
            {
                StatusText.Text = "Detalhes não disponíveis para este pacote (não encontrado no catálogo).";
            }
        }
    }
}
