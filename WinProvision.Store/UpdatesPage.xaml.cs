using Microsoft.Extensions.DependencyInjection;
using System;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using WinProvision.Core.Models;
using WinProvision.Core.Services;
using WinProvision.Store.Services;

namespace WinProvision.Store;

public partial class UpdatesPage : Page
{
    private readonly WingetExecutor _wingetExecutor;
    private readonly StoreService _storeService;
    private readonly OperationsQueueService _queue;
    private readonly ScheduledUpdatesService _scheduledUpdatesService;
    private readonly IgnoredUpdatesService _ignoredUpdatesService;
    private readonly AppDetailsOverlayService _detailsOverlayService;

    private readonly ObservableCollection<UpgradablePackage> _packages = new();

    private bool _suppressSelectionSync;
    private bool _suppressAutoUpdateToggleEvent;

    public UpdatesPage()
    {
        InitializeComponent();

        _wingetExecutor = App.Services.GetRequiredService<WingetExecutor>();
        _storeService = App.Services.GetRequiredService<StoreService>();
        _queue = App.Services.GetRequiredService<OperationsQueueService>();
        _scheduledUpdatesService = App.Services.GetRequiredService<ScheduledUpdatesService>();
        _ignoredUpdatesService = App.Services.GetRequiredService<IgnoredUpdatesService>();
        _detailsOverlayService = App.Services.GetRequiredService<AppDetailsOverlayService>();

        UpdatesList.ItemsSource = _packages;

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
            string detail = string.IsNullOrWhiteSpace(result.Output) ? "(sem saída)" : result.Output.Trim();
            StatusText.Text = $"Falha ao ativar (código {result.ExitCode}): {detail}";
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
            StatusText.Text = "Falha ao desativar atualizações automáticas (UAC recusado ou erro).";
        }
        else
        {
            StatusText.Text = "Atualizações automáticas desativadas.";
        }

        AutoUpdateToggle.IsEnabled = true;
    }

    // ----------------------------------------------------------------
    // Verificar atualizações (winget upgrade)
    // ----------------------------------------------------------------

    private async void CheckUpdatesButton_Click(object sender, RoutedEventArgs e) => await CheckUpdatesAsync();

    private async Task CheckUpdatesAsync()
    {
        CheckUpdatesButton.IsEnabled = false;
        UpdateSelectedButton.IsEnabled = false;
        BusyIndicator.Visibility = Visibility.Visible;
        StatusText.Text = "Verificando atualizações disponíveis...";

        try
        {
            var upgradable = await _wingetExecutor.GetUpgradablePackagesAsync(onLogReceived: line => StatusText.Text = line);

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
                : $"{_packages.Count} atualização(ões) disponível(is).";
        }
        catch (Exception ex)
        {
            StatusText.Text = $"Erro ao verificar atualizações: {ex.Message}";
        }
        finally
        {
            CheckUpdatesButton.IsEnabled = true;
            BusyIndicator.Visibility = Visibility.Collapsed;
            SyncSelectAllCheckBoxState();
        }
    }

    // ----------------------------------------------------------------
    // Seleção (checkbox por item + "Selecionar todos")
    // ----------------------------------------------------------------

    private void ItemCheckBox_Changed(object sender, RoutedEventArgs e)
    {
        if (_suppressSelectionSync) return;
        SyncSelectAllCheckBoxState();
    }

    private void SelectAllCheckBox_Click(object sender, RoutedEventArgs e)
    {
        if (_packages.Count == 0) return;

        bool selectAll = !_packages.All(p => p.IsSelectedForUpdate);

        _suppressSelectionSync = true;
        foreach (var package in _packages)
        {
            package.IsSelectedForUpdate = selectAll;
        }
        _suppressSelectionSync = false;

        SetSelectAllButtonState(selectAll ? _packages.Count : 0);
        UpdateSelectedButton.IsEnabled = selectAll;
    }

    private void SyncSelectAllCheckBoxState()
    {
        int selected = _packages.Count(p => p.IsSelectedForUpdate);

        SetSelectAllButtonState(selected);

        UpdateSelectedButton.IsEnabled = selected > 0;
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
            StatusText.Text = "Selecione ao menos um aplicativo.";
            return;
        }

        UpdateSelectedButton.IsEnabled = false;
        CheckUpdatesButton.IsEnabled = false;
        StatusText.Text = $"Atualizando {selected.Count} aplicativo(s)...";

        int succeeded = 0;
        int failed = 0;

        foreach (var package in selected)
        {
            package.IsUpdating = true;

            try
            {
                var result = await OperationRunner.RunUpdateAsync(_queue, _wingetExecutor, package.Id, package.Name, package.IconUrl);

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
            ? $"{succeeded} pacote(s) atualizado(s) com sucesso."
            : $"{succeeded} pacote(s) atualizado(s), {failed} falharam. Veja a fila de operações para detalhes.";

        SyncSelectAllCheckBoxState();
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