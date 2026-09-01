using System;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using Microsoft.Extensions.DependencyInjection;
using WinProvision.Core.Models;
using WinProvision.Core.Services;

namespace WinProvision.Store;

public partial class UpdatesPage : Page
{
    private readonly WingetExecutor _wingetExecutor;
    private readonly StoreService _storeService;
    private readonly OperationsQueueService _queue;

    private readonly ObservableCollection<UpgradablePackage> _packages = [];

    // Evita que a atualização em massa do checkbox "Selecionar todos" (ou o loop
    // inverso, recalculando o estado dele a partir dos itens) dispare recursão
    // entre ItemCheckBox_Changed e SelectAllCheckBox_Click.
    private bool _suppressSelectionSync;

    public UpdatesPage()
    {
        InitializeComponent();

        _wingetExecutor = App.Services.GetRequiredService<WingetExecutor>();
        _storeService = App.Services.GetRequiredService<StoreService>();
        _queue = App.Services.GetRequiredService<OperationsQueueService>();

        UpdatesList.ItemsSource = _packages;
        UpdateSelectedButton.IsEnabled = false;

        // UpdatesPage é Singleton (ver App.xaml.cs) — o Loaded dispara de novo a cada
        // navegação até aqui pelo menu (mesmo padrão do OfficePage.RefreshInstalledProducts),
        // então a verificação roda sozinha toda vez que o usuário entra na tela, sem precisar
        // clicar em "Verificar".
        Loaded += async (_, _) => await CheckUpdatesAsync();
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
            // onLogReceived aqui é só o que o WingetBootstrapper reporta (baixando/instalando
            // dependências), não a saída do "winget upgrade" em si — GetUpgradablePackagesAsync
            // não expõe progresso incremental do próprio comando, só do bootstrap, que só roda
            // de fato na primeira verificação da sessão (ver WingetExecutor.EnsureWingetBootstrappedOnceAsync).
            var upgradable = await _wingetExecutor.GetUpgradablePackagesAsync(onLogReceived: line => StatusText.Text = line);

            // Cruza com o catálogo já carregado só pra reaproveitar o mesmo ícone
            // exibido em Visão Geral/Pacotes (ver comentário em UpgradablePackage.IconUrl).
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
                _packages.Add(package);
            }

            SelectAllCheckBox.IsChecked = false;

            // Atualiza o card lateral com a hora da busca
            LastSearchText.Text = $"Última busca: Hoje às {DateTime.Now:HH:mm}";

            StatusText.Text = _packages.Count == 0
                ? "Nenhuma atualização disponível. Tudo em dia."
                : $"{_packages.Count} atualização(ões) disponível(is).";

            EmptyStateText.Text = "Nenhuma atualização disponível. Tudo em dia.";
        }
        catch (Exception ex)
        {
            StatusText.Text = $"Erro ao verificar atualizações: {ex.Message}";
        }
        finally
        {
            CheckUpdatesButton.IsEnabled = true;
            BusyIndicator.Visibility = Visibility.Collapsed;
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

        // Decide pelo estado real dos itens antes deste clique (não pelo valor que
        // o próprio CheckBox de três estados acabou de assumir sozinho), pra não
        // depender do ciclo Checked -> Indeterminate -> Unchecked do IsThreeState.
        bool selectAll = !_packages.All(p => p.IsSelectedForUpdate);

        _suppressSelectionSync = true;
        foreach (var package in _packages)
        {
            package.IsSelectedForUpdate = selectAll;
        }
        _suppressSelectionSync = false;

        SelectAllCheckBox.IsChecked = selectAll;
        UpdateSelectedButton.IsEnabled = selectAll;
    }

    private void SyncSelectAllCheckBoxState()
    {
        int selected = _packages.Count(p => p.IsSelectedForUpdate);

        SelectAllCheckBox.IsChecked = selected == 0
            ? false
            : selected == _packages.Count ? true : null;

        UpdateSelectedButton.IsEnabled = selected > 0;
    }

    // ----------------------------------------------------------------
    // Atualizar selecionados (winget update)
    // ----------------------------------------------------------------

    private async void UpdateSelectedButton_Click(object sender, RoutedEventArgs e)
    {
        var selected = _packages.Where(p => p.IsSelectedForUpdate).ToList();

        if (selected.Count == 0)
        {
            StatusText.Text = "Marque o checkbox de ao menos um item antes de atualizar.";
            return;
        }

        UpdateSelectedButton.IsEnabled = false;
        CheckUpdatesButton.IsEnabled = false;
        StatusText.Text = $"Atualizando {selected.Count} pacote(s)... acompanhe na fila de operações.";

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

    // ----------------------------------------------------------------
    // Nova Ação: Atualizar item individualmente (Botão na linha)
    // ----------------------------------------------------------------

    private void SingleUpdateButton_Click(object sender, RoutedEventArgs e)
    {
        // Pega o elemento que disparou o clique (o botão "Atualizar agora")
        if (sender is FrameworkElement element && element.DataContext is UpgradablePackage appToUpdate)
        {
            // 1. Marca apenas este aplicativo como selecionado
            appToUpdate.IsSelectedForUpdate = true;

            // 2. Chama a sua função existente que já faz o trabalho de atualizar a fila
            UpdateSelectedButton_Click(sender, e);
        }
    }
}