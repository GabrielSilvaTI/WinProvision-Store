using System;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Microsoft.Extensions.DependencyInjection;
using WinProvision.Core.Models;
using WinProvision.Core.Models.Office;
using WinProvision.Core.Services;
using WinProvision.Core.Services.Office;

namespace WinProvision.Store;

public partial class OfficePage : Page
{
    private static readonly Brush InstalledBrush = new SolidColorBrush(Color.FromRgb(0x22, 0xB1, 0x4C));
    private static readonly Brush ExcludedBrush = new SolidColorBrush(Color.FromRgb(0xE0, 0x9F, 0x3E));
    private static readonly Brush NotInstalledBrush = new SolidColorBrush(Color.FromRgb(0x9E, 0x9E, 0x9E));

    private readonly OfficeDeploymentToolService _officeService;
    private readonly OfficeInstalledProductsDetector _installedDetector;
    private readonly OperationsQueueService _queue;
    private readonly PackageCollectionService _collectionService;

    private readonly ObservableCollection<AppToggleItem> _appToggleItems = new();
    private readonly ObservableCollection<AppStatusRow> _appStatusRows = new();

    // Evita reentrância quando revertemos o ToggleButton programaticamente
    // (ex.: falha ao aplicar a política) — sem isso, o Unchecked/Checked
    // disparado pela reversão chamaria o handler de novo.
    private bool _suppressAutoUpdateToggleEvent;

    /// <summary>Item da grade "Seleção de Aplicativos" — implementa INotifyPropertyChanged só para o botão "Selecionar todos" conseguir ligar todos os toggles de uma vez de forma visível.</summary>
    private sealed class AppToggleItem(string id, string displayName, string iconUrl, bool isOnByDefault) : INotifyPropertyChanged
    {
        public string Id { get; } = id;
        public string DisplayName { get; } = displayName;
        public string IconUrl { get; } = iconUrl;

        private bool _isOn = isOnByDefault;
        public bool IsOn
        {
            get => _isOn;
            set
            {
                if (_isOn == value) return;
                _isOn = value;
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsOn)));
            }
        }

        public event PropertyChangedEventHandler? PropertyChanged;

        [MethodImpl(MethodImplOptions.NoInlining)]
        private void _() { } // mantém CS8618 quieto sem afetar leitura acima
    }

    private sealed record AppStatusRow(string DisplayName, string IconUrl, string StatusText, Brush StatusBrush);

    // Apps ligados por padrão, igual ao comportamento típico de uma instalação completa do Microsoft 365.
    private static readonly HashSet<string> DefaultOnApps = new(StringComparer.OrdinalIgnoreCase)
    {
        "Word", "Excel", "PowerPoint", "Outlook", "OneNote", "Teams",
    };

    public OfficePage()
    {
        // IsChecked="True" no XAML do AutoUpdatesToggleButton dispara o evento Checked
        // já durante o InitializeComponent() abaixo — antes do resto do construtor rodar
        // e atribuir _officeService/_queue/StatusText etc., o que causava
        // NullReferenceException ao abrir a página. Suprime o handler até o construtor
        // terminar de inicializar tudo.
        _suppressAutoUpdateToggleEvent = true;
        InitializeComponent();
        _suppressAutoUpdateToggleEvent = false;

        _officeService = App.Services.GetRequiredService<OfficeDeploymentToolService>();
        _installedDetector = App.Services.GetRequiredService<OfficeInstalledProductsDetector>();
        _queue = App.Services.GetRequiredService<OperationsQueueService>();
        _collectionService = App.Services.GetRequiredService<PackageCollectionService>();

        CategoryComboBox.DisplayMemberPath = nameof(CategoryOption.Label);
        CategoryComboBox.ItemsSource = new[]
        {
            new CategoryOption(OfficeEditionCategory.Corporate365, "Corporativo / 365"),
            new CategoryOption(OfficeEditionCategory.Ltsc, "LTSC (licença de volume)"),
            new CategoryOption(OfficeEditionCategory.Personal, "Pessoal (Family / Home)"),
            new CategoryOption(OfficeEditionCategory.VisioProject, "Visio / Project"),
        };
        CategoryComboBox.SelectedIndex = 0;

        foreach (var (id, displayName, iconUrl) in OfficeAppCatalog.CoreApps)
        {
            _appToggleItems.Add(new AppToggleItem(id, displayName, iconUrl, DefaultOnApps.Contains(id)));
        }
        AppToggleItemsControl.ItemsSource = _appToggleItems;

        AppStatusItemsControl.ItemsSource = _appStatusRows;
        RefreshInstalledProducts();

        // A OfficePage agora é Singleton (mesma instância entre navegações — é o que faz
        // o plano/apps/idioma escolhidos aqui continuarem do jeito que você deixou quando
        // sai e volta pra essa aba). Só a lista "Produtos Instalados" precisa reler o
        // registro a cada visita (pode ter mudado enquanto você estava em outra tela, ex.:
        // instalou algo pela tela Pacotes) — por isso esse refresh entra no Loaded, que
        // dispara de novo toda vez que a página reaparece, em vez de só no construtor.
        Loaded += (_, _) => RefreshInstalledProducts();
    }

    private record CategoryOption(OfficeEditionCategory Category, string Label);
    private record ChannelOption(string Id, string Label);

    private void CategoryComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (CategoryComboBox.SelectedItem is not CategoryOption option) return;

        var plans = OfficePlanCatalog.ByCategory(option.Category).ToList();
        PlanComboBox.ItemsSource = plans;
        PlanComboBox.SelectedIndex = plans.Count > 0 ? 0 : -1;
    }

    private void PlanComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (PlanComboBox.SelectedItem is not OfficePlan plan)
        {
            ChannelComboBox.ItemsSource = null;
            ChannelComboBox.IsEnabled = false;
            return;
        }

        ChannelComboBox.DisplayMemberPath = nameof(ChannelOption.Label);

        if (plan.Category == OfficeEditionCategory.Corporate365)
        {
            var options = OfficeChannelCatalog.SubscriptionChannels
                .Select(c => new ChannelOption(c.Id, c.DisplayName))
                .ToList();

            ChannelComboBox.ItemsSource = options;
            ChannelComboBox.SelectedItem = options.FirstOrDefault(o => o.Id == plan.Channel) ?? options.FirstOrDefault();
            ChannelComboBox.IsEnabled = true;
        }
        else
        {
            // Licença perpétua/volume: canal fixo, só exibido informativamente.
            ChannelComboBox.ItemsSource = new[] { new ChannelOption(plan.Channel ?? "-", plan.Channel ?? "Fixo pela licença") };
            ChannelComboBox.SelectedIndex = 0;
            ChannelComboBox.IsEnabled = false;
        }
    }

    private void SelectAllAppsButton_Click(object sender, RoutedEventArgs e)
    {
        foreach (var item in _appToggleItems)
            item.IsOn = true;
    }

    // ----------------------------------------------------------------
    // Instalar / Reparar
    // ----------------------------------------------------------------

    private async void InstallRepairButton_Click(object sender, RoutedEventArgs e)
    {
        if (PlanComboBox.SelectedItem is not OfficePlan plan)
        {
            StatusText.Text = "Selecione um plano antes de instalar.";
            return;
        }

        int architecture = (ArchitectureComboBox.SelectedItem as ComboBoxItem)?.Tag as string == "32" ? 32 : 64;
        string languageId = (LanguageComboBox.SelectedItem as ComboBoxItem)?.Tag as string ?? "pt-br";
        bool silent = (InstallDisplayModeComboBox.SelectedItem as ComboBoxItem)?.Tag as string != "visible";
        string? channelOverride = plan.Category == OfficeEditionCategory.Corporate365
            ? (ChannelComboBox.SelectedItem as ChannelOption)?.Id
            : null;

        var additionalLanguages = new[]
        {
            LangAdditionalPtBr, LangAdditionalEnUs, LangAdditionalEsEs, LangAdditionalFrFr, LangAdditionalDeDe,
        }
        .Where(cb => cb.IsChecked == true)
        .Select(cb => (string)cb.Tag)
        .ToArray();

        var excludedApps = _appToggleItems
            .Where(item => !item.IsOn) // toggle desligado => app excluído
            .Select(item => item.Id)
            .Concat(new[]
            {
                (ExcludeOneDriveCheckBox, "Groove"),
                (ExcludeSkypeCheckBox, "Lync"),
                (ExcludeBingCheckBox, "Bing"),
            }
            .Where(pair => pair.Item1.IsChecked == true)
            .Select(pair => pair.Item2))
            .ToArray();

        var request = new OfficeInstallRequest(
            plan,
            architecture,
            languageId,
            excludedApps,
            DisplayNone: silent,
            AdditionalLanguageIds: additionalLanguages,
            DisplayLevel: silent ? OfficeDisplayLevel.Silent : OfficeDisplayLevel.Visible,
            ChannelOverride: channelOverride,
            AutoUpdatesEnabled: AutoUpdatesToggleButton.IsChecked == true);

        StatusText.Text = $"Instalando/reparando {plan.DisplayName}... acompanhe na fila de operações.";

        try
        {
            bool success = await OperationRunner.RunOfficeInstallAsync(_queue, _officeService, request);
            StatusText.Text = success
                ? $"{plan.DisplayName} instalado/reparado com sucesso."
                : $"Falha ao instalar {plan.DisplayName}. Veja a fila de operações para detalhes.";

            RefreshInstalledProducts();
        }
        catch (Exception ex)
        {
            StatusText.Text = $"Erro ao instalar: {ex.Message}";
        }
    }

    // ----------------------------------------------------------------
    // Atualizações (toggle liga/desliga em Ações Rápidas)
    // ----------------------------------------------------------------

    private async void AutoUpdatesToggleButton_Toggled(object sender, RoutedEventArgs e)
    {
        if (_suppressAutoUpdateToggleEvent) return;

        bool enabled = AutoUpdatesToggleButton.IsChecked == true;
        AutoUpdatesToggleButton.IsEnabled = false;
        StatusText.Text = $"{(enabled ? "Ativando" : "Desativando")} atualizações automáticas do Office... acompanhe na fila de operações.";

        try
        {
            bool success = await OperationRunner.RunOfficeSetAutoUpdateAsync(_queue, _officeService, enabled);
            StatusText.Text = success
                ? $"Atualizações automáticas {(enabled ? "ativadas" : "desativadas")} com sucesso."
                : "Não foi possível aplicar a política de atualização. Veja a fila de operações para detalhes.";

            if (!success)
            {
                RevertAutoUpdatesToggle(!enabled);
            }
        }
        catch (Exception ex)
        {
            StatusText.Text = $"Erro ao aplicar política de atualização: {ex.Message}";
            RevertAutoUpdatesToggle(!enabled);
        }
        finally
        {
            AutoUpdatesToggleButton.IsEnabled = true;
        }
    }

    /// <summary>Volta o ToggleButton pro estado anterior sem re-disparar o handler acima.</summary>
    private void RevertAutoUpdatesToggle(bool previousState)
    {
        _suppressAutoUpdateToggleEvent = true;
        AutoUpdatesToggleButton.IsChecked = previousState;
        _suppressAutoUpdateToggleEvent = false;
    }

    // ----------------------------------------------------------------
    // Adicionar ao catálogo
    // ----------------------------------------------------------------

    private void AddToCatalogButton_Click(object sender, RoutedEventArgs e)
    {
        if (PlanComboBox.SelectedItem is not OfficePlan plan)
        {
            StatusText.Text = "Selecione um plano antes de adicionar ao catálogo.";
            return;
        }

        int architecture = (ArchitectureComboBox.SelectedItem as ComboBoxItem)?.Tag as string == "32" ? 32 : 64;
        string languageId = (LanguageComboBox.SelectedItem as ComboBoxItem)?.Tag as string ?? "pt-br";
        bool silent = (InstallDisplayModeComboBox.SelectedItem as ComboBoxItem)?.Tag as string != "visible";
        string? channelOverride = plan.Category == OfficeEditionCategory.Corporate365
            ? (ChannelComboBox.SelectedItem as ChannelOption)?.Id
            : null;

        var additionalLanguages = new[]
        {
            LangAdditionalPtBr, LangAdditionalEnUs, LangAdditionalEsEs, LangAdditionalFrFr, LangAdditionalDeDe,
        }
        .Where(cb => cb.IsChecked == true)
        .Select(cb => (string)cb.Tag)
        .ToList();

        var excludedApps = _appToggleItems
            .Where(item => !item.IsOn) // toggle desligado => app excluído
            .Select(item => item.Id)
            .Concat(new[]
            {
                (ExcludeOneDriveCheckBox, "Groove"),
                (ExcludeSkypeCheckBox, "Lync"),
                (ExcludeBingCheckBox, "Bing"),
            }
            .Where(pair => pair.Item1.IsChecked == true)
            .Select(pair => pair.Item2))
            .ToList();

        var selectedAppNames = _appToggleItems.Where(item => item.IsOn).Select(item => item.DisplayName).ToList();
        string appsSummary = selectedAppNames.Count > 0 ? string.Join(", ", selectedAppNames) : "nenhum aplicativo selecionado";

        // Guarda todos os parâmetros da tela (não só um resumo em texto) no
        // AppEntry.Office — é isso que permite a tela Pacotes reinstalar/reparar
        // esse plano exato mais tarde (botão Instalar) e o mesmo .json de perfil
        // levar apps winget e planos de Office juntos, sem depender do catálogo
        // remoto pra planos de Office (que não existem nele).
        var officeOptions = new OfficeInstallOptions
        {
            ProductId = plan.ProductId,
            Architecture = architecture,
            LanguageId = languageId,
            AdditionalLanguageIds = additionalLanguages,
            ExcludedApps = excludedApps,
            Silent = silent,
            ChannelOverride = channelOverride,
            AutoUpdatesEnabled = AutoUpdatesToggleButton.IsChecked == true,
        };

        var entry = new AppEntry
        {
            Id = $"office.{plan.ProductId}".ToLowerInvariant(),
            Name = $"Office — {plan.DisplayName}",
            Publisher = "Microsoft",
            Version = channelOverride ?? plan.Channel ?? "-",
            Description = $"Plano salvo pela página Office. Apps incluídos: {appsSummary}.",
            IconUrl = "https://pub-166b41912a994dbe86583ba10596d673.r2.dev/Office/Icon/MS365.png",
            Tags = { "Office", plan.Category.ToString() },
            Office = officeOptions,
        };

        int added = _collectionService.AddRangeToActive([entry]);
        string tabTitle = _collectionService.ActiveTab?.Title ?? "Perfil Padrão";

        StatusText.Text = added == 1
            ? $"Plano \"{plan.DisplayName}\" adicionado à guia '{tabTitle}'. Use o botão Instalar na tela Pacotes para instalar/reparar a partir de lá."
            : $"Esse plano já estava na guia '{tabTitle}'.";
    }

    // ----------------------------------------------------------------
    // Desinstalar
    // ----------------------------------------------------------------

    private async void UninstallButton_Click(object sender, RoutedEventArgs e)
    {
        var confirm = MessageBox.Show(
            "Isso vai remover TODAS as instalações do Office (Click-to-Run) desta máquina, usando a tag RemoveAll do Office Deployment Tool. Continuar?",
            "Desinstalar Office",
            MessageBoxButton.YesNo,
            MessageBoxImage.Warning);

        if (confirm != MessageBoxResult.Yes)
            return;

        bool silent = (InstallDisplayModeComboBox.SelectedItem as ComboBoxItem)?.Tag as string != "visible";
        bool cleanStore = CleanStoreEditionCheckBox.IsChecked == true;

        var request = new OfficeRemoveRequest(
            RemoveAll: true,
            DisplayLevel: silent ? OfficeDisplayLevel.Silent : OfficeDisplayLevel.Visible,
            CleanStoreEdition: cleanStore);

        StatusText.Text = "Removendo todas as instalações do Office... acompanhe na fila de operações.";

        try
        {
            bool success = await OperationRunner.RunOfficeRemoveAsync(_queue, _officeService, request, "Remoção completa do Office (RemoveAll)");
            StatusText.Text = success
                ? "Office removido com sucesso."
                : "Falha na remoção. Veja a fila de operações para detalhes.";

            RefreshInstalledProducts();
        }
        catch (Exception ex)
        {
            StatusText.Text = $"Erro ao remover: {ex.Message}";
        }
    }

    // ----------------------------------------------------------------
    // Utilidades
    // ----------------------------------------------------------------

    private void RefreshInstalledButton_Click(object sender, RoutedEventArgs e) => RefreshInstalledProducts();

    private void OpenWorkFolderButton_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            Directory.CreateDirectory(_officeService.WorkRoot);
            Process.Start(new ProcessStartInfo("explorer.exe", $"\"{_officeService.WorkRoot}\"") { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            StatusText.Text = $"Não foi possível abrir a pasta: {ex.Message}";
        }
    }

    private void RefreshInstalledProducts()
    {
        var installed = _installedDetector.GetInstalledProducts();

        _appStatusRows.Clear();
        int installedCount = 0;

        foreach (var (id, displayName, iconUrl) in OfficeAppCatalog.CoreApps)
        {
            AppStatusRow row;

            if (installed.Count == 0)
            {
                row = new AppStatusRow(displayName, iconUrl, "Não instalado", NotInstalledBrush);
            }
            else if (installed.Any(p => p.ExcludedApps.Any(x => string.Equals(x, id, StringComparison.OrdinalIgnoreCase))))
            {
                row = new AppStatusRow(displayName, iconUrl, "Excluído", ExcludedBrush);
            }
            else
            {
                row = new AppStatusRow(displayName, iconUrl, "Instalado", InstalledBrush);
                installedCount++;
            }

            _appStatusRows.Add(row);
        }

        InstalledSummaryText.Text = $"{installedCount} de {OfficeAppCatalog.CoreApps.Count} instalados";
        NoInstalledProductsText.Visibility = installed.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
    }
}

