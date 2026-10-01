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
    private readonly ObservableCollection<OfficePlan> _visioProductOptions = new();
    private readonly ObservableCollection<OfficePlan> _projectProductOptions = new();

    // Evita reentrância quando revertemos o ToggleButton programaticamente
    // (ex.: falha ao aplicar a política) — sem isso, o Unchecked/Checked
    // disparado pela reversão chamaria o handler de novo.
    private bool _suppressAutoUpdateToggleEvent;

    /// <summary>Item da grade "Seleção de Aplicativos" — implementa INotifyPropertyChanged só para o botão "Selecionar todos" conseguir ligar todos os toggles de uma vez de forma visível.</summary>
    private sealed class AppToggleItem(string id, string displayName, string iconUrl, bool isOnByDefault, bool isAdditionalProduct = false) : INotifyPropertyChanged
    {
        public string Id { get; } = id;
        public string DisplayName { get; } = displayName;
        public string IconUrl { get; } = iconUrl;
        public double IconScale { get; } = OfficePage.GetOfficeIconScale(id);
        public bool IsAdditionalProduct { get; } = isAdditionalProduct;

        private bool _isEnabled = true;
        public bool IsEnabled
        {
            get => _isEnabled;
            set { if (_isEnabled == value) return; _isEnabled = value; PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsEnabled))); }
        }

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

    private sealed record AppStatusRow(string DisplayName, string IconUrl, string StatusText, Brush StatusBrush,
        Wpf.Ui.Controls.SymbolRegular StatusSymbol, double IconScale);

    // Os arquivos têm canvas igual, mas esses três desenhos ocupam menos área útil.
    // O RenderTransform compensa a margem interna sem alterar o layout das linhas/tiles.
    private static double GetOfficeIconScale(string id) => id switch
    {
        "Publisher" => 1.8,
        "Visio" => 1.8,
        "Project" => 1.8,
        _ => 1.0,
    };

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
            new CategoryOption(OfficeEditionCategory.Personal, "Microsoft 365"),
            new CategoryOption(OfficeEditionCategory.Corporate365, "Microsoft 365 Empresarial"),
            new CategoryOption(OfficeEditionCategory.Ltsc, "Office LTSC"),
            new CategoryOption(OfficeEditionCategory.VisioProject, "Visio / Project"),
        };
        foreach (var (id, displayName, iconUrl) in OfficeAppCatalog.CoreApps)
        {
            _appToggleItems.Add(new AppToggleItem(id, displayName, iconUrl, DefaultOnApps.Contains(id)));
        }
        foreach (var (id, displayName, iconUrl) in OfficeAppCatalog.AdditionalProducts)
            _appToggleItems.Add(new AppToggleItem(id, displayName, iconUrl, isOnByDefault: false, isAdditionalProduct: true));
        AppToggleItemsControl.ItemsSource = _appToggleItems;

        AppStatusItemsControl.ItemsSource = _appStatusRows;
        VisioProductComboBox.ItemsSource = _visioProductOptions;
        ProjectProductComboBox.ItemsSource = _projectProductOptions;
        // Selecionar a categoria dispara SelectionChanged em cascata até o plano e o canal.
        // Só fazemos isso depois de preparar os toggles e combos usados por esses eventos.
        CategoryComboBox.SelectedIndex = 0;
        RefreshInstalledProducts();

        // A OfficePage agora é Singleton (mesma instância entre navegações — é o que faz
        // o plano/apps/idioma escolhidos aqui continuarem do jeito que você deixou quando
        // sai e volta pra essa aba). Só a lista "Produtos Instalados" precisa reler o
        // registro a cada visita (pode ter mudado enquanto você estava em outra tela, ex.:
        // instalou algo pela tela Pacotes) — por isso esse refresh entra no Loaded, que
        // dispara de novo toda vez que a página reaparece, em vez de só no construtor.
        Loaded += async (_, _) =>
        {
            RefreshInstalledProducts();
            var selectedId = (PlanComboBox.SelectedItem as OfficePlan)?.ProductId;
            if (CategoryComboBox.SelectedItem is CategoryOption category)
            {
                var plans = GetPlansForCategory(category.Category);
                PlanComboBox.ItemsSource = plans;
                PlanComboBox.SelectedItem = plans.FirstOrDefault(p => p.ProductId.Equals(selectedId, StringComparison.OrdinalIgnoreCase)) ?? plans.FirstOrDefault();
            }
        };
    }

    private record CategoryOption(OfficeEditionCategory Category, string Label);
    private record ChannelOption(string Id, string Label);

    private void CategoryComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (CategoryComboBox.SelectedItem is not CategoryOption option) return;

        var plans = GetPlansForCategory(option.Category);
        PlanComboBox.ItemsSource = plans;
        var preferredId = option.Category switch
        {
            OfficeEditionCategory.Personal => "O365HomePremRetail",
            OfficeEditionCategory.Corporate365 => "O365ProPlusRetail",
            OfficeEditionCategory.Ltsc => "ProPlus2024Volume",
            _ => null,
        };
        PlanComboBox.SelectedItem = plans.FirstOrDefault(p => p.ProductId.Equals(preferredId, StringComparison.OrdinalIgnoreCase))
            ?? plans.FirstOrDefault();
    }

    private static List<OfficePlan> GetPlansForCategory(OfficeEditionCategory category)
    {
        return OfficePlanCatalog.ByCategory(category).ToList();
    }

    private void PlanComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (PlanComboBox.SelectedItem is not OfficePlan plan)
        {
            ChannelComboBox.ItemsSource = null;
            ChannelComboBox.IsEnabled = false;
            ChannelComboBox.ToolTip = null;
            return;
        }

        ChannelComboBox.DisplayMemberPath = nameof(ChannelOption.Label);

        if (plan.SupportsSelectableChannel)
        {
            ChannelComboBox.ToolTip = "O canal selecionado será aplicado ao produto principal e aos produtos adicionais compatíveis.";
            var options = OfficeChannelCatalog.SubscriptionChannels
                .Select(c => new ChannelOption(c.Id, c.DisplayName))
                .ToList();

            ChannelComboBox.ItemsSource = options;
            ChannelComboBox.SelectedItem = options.FirstOrDefault(o => o.Id == plan.Channel) ?? options.FirstOrDefault();
            ChannelComboBox.IsEnabled = true;
        }
        else
        {
            ChannelComboBox.ToolTip = "O canal de atualização é determinado por esta edição do Office LTSC.";
            ChannelComboBox.ItemsSource = new[] { new ChannelOption(plan.Channel ?? "-", plan.Channel ?? "Fixo pela licença") };
            ChannelComboBox.SelectedIndex = 0;
            ChannelComboBox.IsEnabled = false;
        }

        RefreshAdditionalProductOptions(plan);
    }

    private void RefreshAdditionalProductOptions(OfficePlan selectedPlan)
    {
        string? selectedVisioId = (VisioProductComboBox.SelectedItem as OfficePlan)?.ProductId;
        string? selectedProjectId = (ProjectProductComboBox.SelectedItem as OfficePlan)?.ProductId;
        _visioProductOptions.Clear();
        _projectProductOptions.Clear();
        var effectiveChannel = selectedPlan.SupportsSelectableChannel
            ? (ChannelComboBox.SelectedItem as ChannelOption)?.Id ?? selectedPlan.Channel
            : selectedPlan.Channel;

        bool subscriptionChannel = OfficeChannelCatalog.SubscriptionChannels.Any(c =>
            c.Id.Equals(effectiveChannel, StringComparison.OrdinalIgnoreCase));
        var compatiblePlans = OfficePlanCatalog.All.Where(p =>
                p.Category == OfficeEditionCategory.VisioProject &&
                !p.ProductId.Equals(selectedPlan.ProductId, StringComparison.OrdinalIgnoreCase) &&
                !HasSameAdditionalProductType(p, selectedPlan) &&
                ((subscriptionChannel && p.SupportsSelectableChannel) ||
                 string.Equals(p.Channel, effectiveChannel, StringComparison.OrdinalIgnoreCase)))
            .OrderBy(p => subscriptionChannel &&
                (p.ProductId.Equals("VisioProRetail", StringComparison.OrdinalIgnoreCase) ||
                 p.ProductId.Equals("ProjectProRetail", StringComparison.OrdinalIgnoreCase)) ? 0 : 1)
            .ThenBy(p => p.DisplayName, StringComparer.CurrentCultureIgnoreCase);

        foreach (var plan in compatiblePlans)
        {
            if (IsVisioPlan(plan)) _visioProductOptions.Add(plan);
            else if (IsProjectPlan(plan)) _projectProductOptions.Add(plan);
        }

        VisioProductComboBox.SelectedItem = _visioProductOptions.FirstOrDefault(p => p.ProductId.Equals(selectedVisioId, StringComparison.OrdinalIgnoreCase))
            ?? _visioProductOptions.FirstOrDefault();
        ProjectProductComboBox.SelectedItem = _projectProductOptions.FirstOrDefault(p => p.ProductId.Equals(selectedProjectId, StringComparison.OrdinalIgnoreCase))
            ?? _projectProductOptions.FirstOrDefault();

        var visioToggle = _appToggleItems.FirstOrDefault(item => item.Id == "Visio");
        var projectToggle = _appToggleItems.FirstOrDefault(item => item.Id == "Project");
        if (visioToggle is not null)
        {
            visioToggle.IsEnabled = _visioProductOptions.Count > 0 && !IsVisioPlan(selectedPlan);
            if (!visioToggle.IsEnabled) visioToggle.IsOn = false;
        }
        if (projectToggle is not null)
        {
            projectToggle.IsEnabled = _projectProductOptions.Count > 0 && !IsProjectPlan(selectedPlan);
            if (!projectToggle.IsEnabled) projectToggle.IsOn = false;
        }

        UpdateAdditionalProductSelectors();
    }

    private static bool IsVisioPlan(OfficePlan plan) => plan.ProductId.StartsWith("Visio", StringComparison.OrdinalIgnoreCase);
    private static bool IsProjectPlan(OfficePlan plan) => plan.ProductId.StartsWith("Project", StringComparison.OrdinalIgnoreCase);
    private static bool HasSameAdditionalProductType(OfficePlan left, OfficePlan right) =>
        (IsVisioPlan(left) && IsVisioPlan(right)) || (IsProjectPlan(left) && IsProjectPlan(right));

    private void AppTileToggle_Click(object sender, RoutedEventArgs e) => UpdateAdditionalProductSelectors();

    private void UpdateAdditionalProductSelectors()
    {
        bool visioSelected = _appToggleItems.FirstOrDefault(item => item.Id == "Visio")?.IsOn == true;
        bool projectSelected = _appToggleItems.FirstOrDefault(item => item.Id == "Project")?.IsOn == true;
        VisioProductSelector.Visibility = visioSelected ? Visibility.Visible : Visibility.Collapsed;
        ProjectProductSelector.Visibility = projectSelected ? Visibility.Visible : Visibility.Collapsed;
        AdditionalProductsPanel.Visibility = visioSelected || projectSelected ? Visibility.Visible : Visibility.Collapsed;
    }

    private static string GetCategoryLabel(OfficeEditionCategory category) => category switch
    {
        OfficeEditionCategory.Personal => "Microsoft 365",
        OfficeEditionCategory.Corporate365 => "Microsoft 365 Empresarial",
        OfficeEditionCategory.Ltsc => "Office LTSC",
        _ => category.ToString(),
    };

    private OfficePlan[] GetAdditionalProducts()
    {
        var products = new List<OfficePlan>(2);
        if (_appToggleItems.FirstOrDefault(item => item.Id == "Visio")?.IsOn == true &&
            VisioProductComboBox.SelectedItem is OfficePlan visio)
            products.Add(visio);
        if (_appToggleItems.FirstOrDefault(item => item.Id == "Project")?.IsOn == true &&
            ProjectProductComboBox.SelectedItem is OfficePlan project)
            products.Add(project);
        return products.ToArray();
    }

    private void ChannelComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (PlanComboBox.SelectedItem is OfficePlan plan)
            RefreshAdditionalProductOptions(plan);
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
        => await InstallOfficeAsync();

    private async Task InstallOfficeAsync()
    {
        var request = CreateInstallRequest();
        if (request is null) return;
        var plan = request.Plan;

        if (OfficeConfigXmlBuilder.ValidateRequest(request) is { } validationError)
        {
            StatusText.Text = validationError;
            return;
        }

        StatusText.Text = $"Instalando {plan.DisplayName}...";

        try
        {
            bool success = await OperationRunner.RunOfficeInstallAsync(_queue, _officeService, request);
            StatusText.Text = success
                ? $"{plan.DisplayName} instalado/reparado com sucesso."
                : $"Falha ao instalar {plan.DisplayName}. Veja a fila de operações para detalhes.";

            RefreshInstalledProducts();
        }
        catch
        {
            StatusText.Text = "Não foi possível instalar o Office. Tente novamente.";
        }
    }

    private OfficeInstallRequest? CreateInstallRequest()
    {
        if (PlanComboBox.SelectedItem is not OfficePlan plan)
        {
            StatusText.Text = "Selecione um plano antes de continuar.";
            return null;
        }

        int architecture = (ArchitectureComboBox.SelectedItem as ComboBoxItem)?.Tag as string == "32" ? 32 : 64;
        string languageId = (LanguageComboBox.SelectedItem as ComboBoxItem)?.Tag as string ?? "pt-br";
        var displayLevel = (InterfaceComboBox.SelectedItem as ComboBoxItem)?.Tag as string == "Visible"
            ? OfficeDisplayLevel.Visible
            : OfficeDisplayLevel.Silent;
        string? channelOverride = plan.SupportsSelectableChannel
            ? (ChannelComboBox.SelectedItem as ChannelOption)?.Id
            : null;
        var additionalLanguages = new[] { LangAdditionalPtBr, LangAdditionalEnUs, LangAdditionalEsEs, LangAdditionalFrFr, LangAdditionalDeDe }
            .Where(cb => cb.IsChecked == true).Select(cb => (string)cb.Tag).ToArray();
        var excludedApps = _appToggleItems.Where(item => !item.IsAdditionalProduct && !item.IsOn).Select(item => item.Id)
            .Concat(new[] { (ExcludeOneDriveCheckBox, "Groove"), (ExcludeSkypeCheckBox, "Lync"), (ExcludeBingCheckBox, "Bing") }
                .Where(pair => pair.Item1.IsChecked == true).Select(pair => pair.Item2)).ToArray();

        var request = new OfficeInstallRequest(plan, architecture, languageId, excludedApps,
            DisplayNone: displayLevel == OfficeDisplayLevel.Silent,
            AdditionalLanguageIds: additionalLanguages,
            DisplayLevel: displayLevel,
            ChannelOverride: channelOverride,
            AutoUpdatesEnabled: AutoUpdatesToggleButton.IsChecked == true,
            AdditionalProducts: GetAdditionalProducts());

        if (OfficeConfigXmlBuilder.ValidateRequest(request) is { } validationError)
        {
            StatusText.Text = validationError;
            return null;
        }
        return request;
    }

    private void PreviewConfigurationButton_Click(object sender, RoutedEventArgs e)
    {
        var request = CreateInstallRequest();
        if (request is null) return;

        string xml = OfficeConfigXmlBuilder.Build(request).ToString(System.Xml.Linq.SaveOptions.None);
        var preview = new Window
        {
            Title = "Prévia da configuração do Office",
            Width = 860,
            Height = 620,
            MinWidth = 600,
            MinHeight = 400,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Owner = Window.GetWindow(this),
            Opacity = 0,
        };
        preview.SetResourceReference(Control.BackgroundProperty, "ApplicationBackgroundBrush");

        var root = new DockPanel { Margin = new Thickness(20) };
        var actions = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 12, 0, 0) };
        DockPanel.SetDock(actions, Dock.Bottom);
        var xmlBox = new TextBox
        {
            Text = xml,
            IsReadOnly = true,
            AcceptsReturn = true,
            AcceptsTab = true,
            TextWrapping = TextWrapping.NoWrap,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
            FontFamily = new System.Windows.Media.FontFamily("Cascadia Code, Consolas"),
            FontSize = 13,
            Padding = new Thickness(12),
        };
        xmlBox.SetResourceReference(Control.BackgroundProperty, "ControlFillColorDefaultBrush");
        xmlBox.SetResourceReference(Control.ForegroundProperty, "TextFillColorPrimaryBrush");

        var copyButton = new Button { Content = "Copiar", MinWidth = 90, Margin = new Thickness(0, 0, 8, 0), Padding = new Thickness(12, 6, 12, 6) };
        copyButton.Click += (_, _) =>
        {
            try { Clipboard.SetText(xml); StatusText.Text = "XML copiado para a área de transferência."; }
            catch { StatusText.Text = "Não foi possível copiar o XML. Selecione o conteúdo e use Ctrl+C."; }
        };
        var exportButton = new Button { Content = "Exportar XML", MinWidth = 110, Margin = new Thickness(0, 0, 8, 0), Padding = new Thickness(12, 6, 12, 6) };
        exportButton.Click += (_, _) =>
        {
            var dialog = new Microsoft.Win32.SaveFileDialog
            {
                Title = "Exportar configuração do Office",
                FileName = "configuration.xml",
                DefaultExt = ".xml",
                Filter = "Arquivo XML (*.xml)|*.xml",
                AddExtension = true,
            };
            if (dialog.ShowDialog(preview) == true)
            {
                try
                {
                    File.WriteAllText(dialog.FileName, xml, new System.Text.UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
                    StatusText.Text = $"Configuração exportada para {dialog.FileName}.";
                }
                catch (Exception ex)
                {
                    StatusText.Text = $"Não foi possível exportar o XML: {ex.Message}";
                }
            }
        };
        var closeButton = new Button { Content = "Fechar", MinWidth = 90, Padding = new Thickness(12, 6, 12, 6), IsDefault = true };
        closeButton.Click += (_, _) => preview.Close();
        actions.Children.Add(copyButton);
        actions.Children.Add(exportButton);
        actions.Children.Add(closeButton);
        root.Children.Add(xmlBox);
        root.Children.Add(actions);
        preview.Content = root;
        preview.Loaded += (_, _) => preview.BeginAnimation(OpacityProperty,
            new System.Windows.Media.Animation.DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(140)));
        preview.ShowDialog();
    }

    // ----------------------------------------------------------------
    // Atualizações (toggle liga/desliga em Ações Rápidas)
    // ----------------------------------------------------------------

    private async void AutoUpdatesToggleButton_Toggled(object sender, RoutedEventArgs e)
    {
        if (_suppressAutoUpdateToggleEvent) return;

        bool enabled = AutoUpdatesToggleButton.IsChecked == true;
        AutoUpdatesToggleButton.IsEnabled = false;
        StatusText.Text = $"{(enabled ? "Ativando" : "Desativando")} atualizações do Office...";

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
        catch
        {
            StatusText.Text = "Não foi possível alterar as atualizações do Office.";
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
        var request = CreateInstallRequest();
        if (request is null) return;
        var plan = request.Plan;

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
            Architecture = request.Architecture,
            LanguageId = request.LanguageId,
            AdditionalLanguageIds = request.AdditionalLanguageIds?.ToList() ?? [],
            ExcludedApps = request.ExcludedApps.ToList(),
            Silent = request.DisplayLevel == OfficeDisplayLevel.Silent,
            ChannelOverride = request.ChannelOverride,
            AutoUpdatesEnabled = request.AutoUpdatesEnabled,
            AdditionalProductIds = request.AdditionalProducts?.Select(p => p.ProductId).ToList() ?? [],
        };

        var bundleIds = new[] { plan.ProductId }.Concat(GetAdditionalProducts().Select(p => p.ProductId)).ToArray();
        var entry = new AppEntry
        {
            Id = $"office.{string.Join("+", bundleIds)}".ToLowerInvariant(),
            Name = $"Office — {plan.DisplayName}" + (GetAdditionalProducts().Length > 0 ? $" + {string.Join(" + ", GetAdditionalProducts().Select(p => p.DisplayName))}" : string.Empty),
            Publisher = "Microsoft",
            Version = request.ChannelOverride ?? plan.Channel ?? "-",
            Description = $"Plano salvo pela página Office. Apps incluídos: {appsSummary}. Produtos adicionais: {string.Join(", ", GetAdditionalProducts().Select(p => p.DisplayName))}.",
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
        // A instalação pode ter sido removida enquanto a página permaneceu aberta.
        // Revalida antes de exibir a confirmação ou iniciar a operação.
        if (!_installedDetector.HasAnyInstallation())
        {
            RefreshInstalledProducts();
            StatusText.Text = "Nenhuma instalação do Office foi encontrada.";
            return;
        }

        var confirm = await StoreConfirmationDialog.ShowAsync(
            "Remover o Office",
            "Isso removerá todas as instalações do Office deste computador. Continuar?",
            "Desinstalar",
            "Cancelar");

        if (confirm != Wpf.Ui.Controls.ContentDialogResult.Primary)
            return;

        const bool silent = true;
        bool cleanStore = CleanStoreEditionCheckBox.IsChecked == true;

        var request = new OfficeRemoveRequest(
            RemoveAll: true,
            DisplayLevel: silent ? OfficeDisplayLevel.Silent : OfficeDisplayLevel.Visible,
            CleanStoreEdition: cleanStore,
            UseRemoveMSI: true,
            UseAggressiveUninstall: true);

        StatusText.Text = "Removendo o Office... Acompanhe o progresso na fila.";

        try
        {
            bool success = await OperationRunner.RunOfficeRemoveAsync(_queue, _officeService, request, "Remoção completa do Office (RemoveAll)");
            StatusText.Text = success
                ? "Office removido com sucesso."
                : "Falha na remoção. Veja a fila de operações para detalhes.";

            RefreshInstalledProducts();
        }
        catch
        {
            StatusText.Text = "Não foi possível remover o Office. Tente novamente.";
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
        catch
        {
            StatusText.Text = "Não foi possível abrir a pasta. Tente novamente.";
        }
    }

    private void RefreshInstalledProducts()
    {
        var installed = _installedDetector.GetInstalledProducts();
        var suiteProducts = installed.Where(product => !IsAdditionalProductId(product.ProductId)).ToArray();
        UninstallButton.IsEnabled = installed.Count > 0;
        UninstallButton.ToolTip = installed.Count > 0
            ? "Remover todas as instalações do Office"
            : "Instale o Office antes de desinstalar.";

        _appStatusRows.Clear();
        int installedCount = 0;

        foreach (var (id, displayName, iconUrl) in OfficeAppCatalog.CoreApps)
        {
            AppStatusRow row;

            if (suiteProducts.Length == 0)
            {
                row = new AppStatusRow(displayName, iconUrl, "Não instalado", NotInstalledBrush,
                    Wpf.Ui.Controls.SymbolRegular.DismissCircle24, GetOfficeIconScale(id));
            }
            else if (suiteProducts.Any(p => p.ExcludedApps.Any(x => string.Equals(x, id, StringComparison.OrdinalIgnoreCase))))
            {
                row = new AppStatusRow(displayName, iconUrl, "Excluído", ExcludedBrush,
                    Wpf.Ui.Controls.SymbolRegular.ErrorCircle24, GetOfficeIconScale(id));
            }
            else
            {
                row = new AppStatusRow(displayName, iconUrl, "Instalado", InstalledBrush,
                    Wpf.Ui.Controls.SymbolRegular.CheckmarkCircle24, GetOfficeIconScale(id));
                installedCount++;
            }

            _appStatusRows.Add(row);
        }

        foreach (var (id, displayName, iconUrl) in OfficeAppCatalog.AdditionalProducts)
        {
            bool productInstalled = installed.Any(p => string.Equals(p.ProductId, id, StringComparison.OrdinalIgnoreCase) ||
                p.ProductId.StartsWith(id, StringComparison.OrdinalIgnoreCase));
            _appStatusRows.Add(new AppStatusRow(displayName, iconUrl,
                productInstalled ? "Instalado" : "Não instalado",
                productInstalled ? InstalledBrush : NotInstalledBrush,
                productInstalled ? Wpf.Ui.Controls.SymbolRegular.CheckmarkCircle24 : Wpf.Ui.Controls.SymbolRegular.DismissCircle24,
                GetOfficeIconScale(id)));
            if (productInstalled) installedCount++;
        }

        InstalledSummaryText.Text = $"{installedCount} de {OfficeAppCatalog.CoreApps.Count + OfficeAppCatalog.AdditionalProducts.Count} instalados";
        NoInstalledProductsText.Visibility = installed.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    private static bool IsAdditionalProductId(string productId) =>
        productId.StartsWith("Visio", StringComparison.OrdinalIgnoreCase) ||
        productId.StartsWith("Project", StringComparison.OrdinalIgnoreCase);
}
