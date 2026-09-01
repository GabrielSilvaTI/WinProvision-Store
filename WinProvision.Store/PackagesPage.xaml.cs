using System;
using System.Collections.Specialized;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Media;
using Microsoft.Extensions.DependencyInjection;
using WinProvision.Core.Models;
using WinProvision.Core.Models.Office;
using WinProvision.Core.Services;
using WinProvision.Core.Services.Office;
using WinProvision.Core.Services.Profile;
using WinProvision.Core.Services.Provisioning;

namespace WinProvision.Store;

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
    private PackageProfileTab? _observedTab;
    private ICollectionView? _collectionView;
    private CancellationTokenSource? _metricsCts;
    private readonly HashSet<AppEntry> _metricAttemptedItems = [];

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

        // Conecta a lista de abas ao TabControl
        ProfileTabControl.ItemsSource = _collectionService.Tabs;
        ProfileTabControl.SelectedItem = _collectionService.ActiveTab;
        AttachToActiveTab();
        SetViewMode(false);
        UpdateStatus();
    }

    private void UpdateStatus()
    {
        var active = _collectionService.ActiveTab;
        if (active == null) return;

        int selected = active.Items.Count(a => a.IsSelectedForInstall);
        SelectionSummaryText.Text = $"{active.Items.Count} pacote(s) · {selected} selecionado(s)";
        StatusText.Text = active.Items.Count == 0
            ? $"[{active.Title}] Nenhum aplicativo adicionado ainda."
            : $"[{active.Title}] {active.Items.Count} pacote(s) na coleção.";

        CloseProfileButton.IsEnabled = !active.IsDefault;
        CloseProfileButton.Visibility = active.IsDefault ? Visibility.Collapsed : Visibility.Visible;
        RefreshCollectionSummary(active);
    }

    private void NewTabButton_Click(object sender, RoutedEventArgs e)
    {
        var newTab = _collectionService.CreateNewTab();
        ProfileTabControl.SelectedItem = newTab;
        AttachToActiveTab();
        UpdateStatus();
    }

    private void CloseTabButton_Click(object sender, RoutedEventArgs e)
    {
        var tab = _collectionService.ActiveTab;
        if (tab is null || tab.IsDefault) return;

        var result = MessageBox.Show(
            $"Excluir o perfil '{tab.Title}'? Os aplicativos desta guia serão removidos da coleção, mas nenhum aplicativo será desinstalado do Windows.",
            "Excluir perfil", MessageBoxButton.YesNo, MessageBoxImage.Warning);

        if (result != MessageBoxResult.Yes) return;

        _collectionService.CloseTab(tab);
        ProfileTabControl.SelectedItem = _collectionService.ActiveTab;
        AttachToActiveTab();
        UpdateStatus();
    }

    private void ProfileTabControl_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (ProfileTabControl.SelectedItem is PackageProfileTab selectedTab)
        {
            _collectionService.ActiveTab = selectedTab;
            AttachToActiveTab();
            UpdateStatus();
        }
    }

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
        AppCollectionItemsControl.ItemsSource = _collectionView;
        AppCollectionListItemsControl.ItemsSource = _collectionView;
        _ = RefreshCollectionMetricsAsync(_observedTab);
        RefreshCollectionSummary(_observedTab);
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
            Dispatcher.BeginInvoke(() =>
            {
                if (_collectionService.ActiveTab is { } tab)
                    RefreshCollectionSummary(tab);
                UpdateStatus();
            });
    }

    private bool FilterCollectionItem(object item)
    {
        if (item is not AppEntry app) return false;
        string query = PackageSearchTextBox?.Text?.Trim() ?? string.Empty;
        if (query.Length == 0) return true;
        return app.Name.Contains(query, StringComparison.OrdinalIgnoreCase)
            || app.Publisher.Contains(query, StringComparison.OrdinalIgnoreCase)
            || app.Id.Contains(query, StringComparison.OrdinalIgnoreCase);
    }

    private void PackageSearchTextBox_TextChanged(object sender, TextChangedEventArgs e) => _collectionView?.Refresh();

    private void GridViewToggleButton_Click(object sender, RoutedEventArgs e) => SetViewMode(false);
    private void ListViewToggleButton_Click(object sender, RoutedEventArgs e) => SetViewMode(true);

    private void SetViewMode(bool list)
    {
        GridViewScrollViewer.Visibility = list ? Visibility.Collapsed : Visibility.Visible;
        ListViewScrollViewer.Visibility = list ? Visibility.Visible : Visibility.Collapsed;
        GridViewToggleButton.IsChecked = !list;
        ListViewToggleButton.IsChecked = list;
        Brush accent = GetThemeBrush("SystemAccentColorPrimaryBrush", SystemColors.HighlightBrush);
        Brush primaryText = GetThemeBrush("TextFillColorPrimaryBrush", SystemColors.ControlTextBrush);
        GridViewToggleButton.Background = !list ? accent : Brushes.Transparent;
        ListViewToggleButton.Background = list ? accent : Brushes.Transparent;
        GridViewToggleButton.Foreground = !list ? Brushes.White : primaryText;
        ListViewToggleButton.Foreground = list ? Brushes.White : primaryText;
    }

    private Brush GetThemeBrush(string key, Brush fallback) =>
        TryFindResource(key) as Brush ?? fallback;

    private async Task RefreshCollectionMetricsAsync(PackageProfileTab tab)
    {
        _metricsCts?.Cancel();
        _metricsCts?.Dispose();
        _metricsCts = new CancellationTokenSource();
        CancellationToken ct = _metricsCts.Token;
        // Inclui itens nunca consultados E itens já consultados que falharam (size ainda
        // null) — estes últimos são tentados de novo a cada troca de aba/alteração na
        // coleção, sem ficar bloqueados pra sempre (ver comentário abaixo).
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
                    // Sempre marca como "tentado" (sucesso ou falha) — é isso que faz o
                    // resumo parar de mostrar "Calculando…" assim que a consulta termina.
                    // A decisão de tentar de novo mais tarde não depende deste HashSet:
                    // depende só de InstallerSizeBytes continuar null (ver seleção
                    // de "apps" acima), então falhas nunca ficam bloqueadas pra sempre, mas
                    // também nunca ficam presas em "Calculando…" enquanto não há nenhuma
                    // consulta de fato em andamento.
                    _metricAttemptedItems.Add(app);

                    if (tab.Items.Contains(app))
                        app.InstallerSizeBytes = size;

                    // Uma consulta pode terminar depois que o usuário trocou de perfil.
                    // Nesse caso atualizamos somente o modelo antigo; nunca sobrescrevemos
                    // o resumo visual do perfil que está atualmente aberto.
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
                // Mesmo numa exceção inesperada (não tratada dentro do próprio
                // PackageMetricsService), marca como tentado — senão o item fica preso
                // em "Calculando…" pra sempre, já que nenhuma consulta nova é disparada
                // pra ele enquanto não houver troca de aba/alteração na coleção.
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

    private void RefreshCollectionSummary(PackageProfileTab tab)
    {
        long totalBytes = tab.Items.Sum(a => a.InstallerSizeBytes ?? 0);
        int measurablePackageCount = tab.Items.Count(a => a.Office is null);
        bool loading = tab.Items.Any(a => a.Office is null && !_metricAttemptedItems.Contains(a));
        bool hasUnknownSize = tab.Items.Any(a => a.Office is null && _metricAttemptedItems.Contains(a) && !a.InstallerSizeBytes.HasValue);

        if (tab.Items.Count == 0)
        {
            EstimatedSizeText.Text = "—";
            EstimatedTimeText.Text = "—";
            return;
        }

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
        // Estimativa conservadora: ~8 MB/s de download + uma pequena margem por pacote
        // para validação, extração e execução do instalador. Não representa o tempo exato
        // do instalador, mas reage proporcionalmente ao conteúdo real da coleção.
        double overhead = Math.Max(10, packageCount * 12);
        double seconds = overhead + (bytes / 1024d / 1024d) / 8d;
        if (seconds >= 3600) return $"~ {seconds / 3600:0.0} h" + (unknown ? " +" : string.Empty);
        if (seconds >= 60) return $"~ {Math.Ceiling(seconds / 60):0} min" + (unknown ? " +" : string.Empty);
        return $"~ {Math.Max(1, Math.Ceiling(seconds)):0} s" + (unknown ? " +" : string.Empty);
    }


    // ----------------------------------------------------------------
    // Instalar (botão geral da barra de ferramentas, estilo UnigetUI)
    // ----------------------------------------------------------------

    /// <summary>
    /// Instala tudo que estiver marcado (CheckBox) na guia atual, um de cada vez.
    /// Pra cada item, decide o que rodar olhando pro item: AppEntry.Office
    /// preenchido => pipeline do Office via ODT (mesmo request que a OfficePage
    /// monta); Office == null => pacote winget comum, mesmo fluxo usado no card da
    /// Visão Geral/detalhes.
    /// </summary>
    private async void InstallSelectedButton_Click(object sender, RoutedEventArgs e)
    {
        var activeTab = _collectionService.ActiveTab;
        var selected = activeTab?.Items.Where(a => a.IsSelectedForInstall).ToList() ?? new List<AppEntry>();

        if (selected.Count == 0)
        {
            StatusText.Text = "Marque o checkbox de ao menos um pacote na guia atual antes de instalar.";
            return;
        }

        InstallSelectedButton.IsEnabled = false;
        StatusText.Text = $"Instalando {selected.Count} pacote(s) selecionado(s)... acompanhe na fila de operações.";

        int succeeded = 0;
        int failed = 0;

        try
        {
            foreach (var app in selected)
            {
                bool success = app.Office is { } officeOptions
                    ? await InstallOfficePlanAsync(app, officeOptions)
                    : await InstallWingetAppAsync(app);

                if (success) succeeded++;
                else failed++;
            }

            StatusText.Text = failed == 0
                ? $"{succeeded} pacote(s) instalado(s) com sucesso."
                : $"{succeeded} pacote(s) instalado(s), {failed} falharam. Veja a fila de operações para detalhes.";
        }
        finally
        {
            InstallSelectedButton.IsEnabled = true;
        }
    }

    private async Task<bool> InstallOfficePlanAsync(AppEntry app, OfficeInstallOptions options)
    {
        var plan = OfficePlanCatalog.All.FirstOrDefault(p => string.Equals(p.ProductId, options.ProductId, StringComparison.OrdinalIgnoreCase));
        if (plan is null)
        {
            StatusText.Text = $"Não foi possível instalar \"{app.Name}\": o plano de Office (ProductId '{options.ProductId}') não existe no catálogo desta versão do app.";
            return false;
        }

        var request = new OfficeInstallRequest(
            plan,
            options.Architecture,
            options.LanguageId,
            options.ExcludedApps,
            DisplayNone: options.Silent,
            AdditionalLanguageIds: options.AdditionalLanguageIds,
            DisplayLevel: options.Silent ? OfficeDisplayLevel.Silent : OfficeDisplayLevel.Visible,
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

    private async Task<bool> InstallWingetAppAsync(AppEntry app)
    {
        try
        {
            var result = await OperationRunner.RunInstallAsync(_queue, _wingetExecutor, app.Id, app.Name, app.IconUrl);
            return result.Success;
        }
        catch
        {
            return false;
        }
    }

    private void RemoveButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: AppEntry app } && _collectionService.ActiveTab != null)
        {
            _collectionService.ActiveTab.Items.Remove(app);
            UpdateStatus();
        }
    }

    private void ClearCollectionButton_Click(object sender, RoutedEventArgs e)
    {
        if (_collectionService.ActiveTab == null || _collectionService.ActiveTab.Items.Count == 0) return;

        _collectionService.ActiveTab.Items.Clear();
        UpdateStatus();
    }

    private async void ImportProfileButton_Click(object sender, RoutedEventArgs e)
    {
        var openFileDialog = new Microsoft.Win32.OpenFileDialog
        {
            Filter = "JSON Profile (*.json)|*.json",
            Title = "Selecione o perfil de aplicativos"
        };

        if (openFileDialog.ShowDialog() != true) return;

        StatusText.Text = "Lendo perfil de aplicativos...";

        try
        {
            var manifest = await _profileService.ImportAsync(openFileDialog.FileName);
            var installedIds = await _wingetExecutor.GetInstalledPackageIdsAsync();
            var (toInstall, _) = _profileService.Reconcile(manifest, installedIds);

            // Apps winget comuns: resolvidos contra o catálogo remoto vivo (StoreService),
            // igual a antes.
            var pendingWingetIds = toInstall
                .Where(a => a.OfficeOptions is null)
                .Select(a => a.Id)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
            var matchedWingetApps = _storeService.GetAll().Where(app => pendingWingetIds.Contains(app.Id));

            // Planos de Office: não existem no catálogo remoto, e o winget nunca "vê"
            // uma instalação do Office (reconciliação acima não se aplica a eles) —
            // reconstruídos direto a partir do próprio .json (autocontido).
            var officeEntries = manifest.Apps
                .Where(a => a.OfficeOptions is not null)
                .Select(a => new AppEntry
                {
                    Id = a.Id,
                    Name = a.Name ?? a.Id,
                    Publisher = a.Publisher ?? "Microsoft",
                    IconUrl = a.IconUrl ?? string.Empty,
                    Description = a.Description,
                    Office = a.OfficeOptions,
                });

            // Adiciona em uma nova guia nomeada com o arquivo importado
            var importedTab = _collectionService.CreateNewTab(Path.GetFileNameWithoutExtension(openFileDialog.FileName));
            foreach (var app in matchedWingetApps.Concat(officeEntries))
            {
                importedTab.Items.Add(app);
            }

            ProfileTabControl.SelectedItem = importedTab;

            // Se o .json trouxer também uma seção de provisionamento, não descarta —
            // marca como estado atual (entra no próximo backup/sincronização) e a tela
            // Provisionamento já reflete isso na próxima vez que for aberta.
            if (manifest.Provisioning is { } provisioning)
            {
                _provisioningService.SetCurrent(provisioning);
                StatusText.Text = $"Perfil importado em nova guia: {importedTab.Items.Count} pacote(s). Provisionamento incluído — veja a tela Provisionamento e clique em \"Aplicar agora\" para valer nesta máquina.";
            }
            else
            {
                StatusText.Text = $"Perfil importado em nova guia: {importedTab.Items.Count} pacote(s).";
            }
        }
        catch (Exception ex)
        {
            StatusText.Text = $"Erro ao importar: {ex.Message}";
        }
    }

    private async void ExportProfileButton_Click(object sender, RoutedEventArgs e)
    {
        var activeTab = _collectionService.ActiveTab;
        if (activeTab == null || activeTab.Items.Count == 0)
        {
            StatusText.Text = "A guia atual está vazia — nada para exportar.";
            return;
        }

        var saveFileDialog = new Microsoft.Win32.SaveFileDialog
        {
            Filter = "JSON Profile (*.json)|*.json",
            FileName = $"{activeTab.Title.ToLower().Replace(" ", "-")}.json",
            Title = "Salvar Perfil de Aplicativos"
        };

        if (saveFileDialog.ShowDialog() != true) return;

        // Inclui o provisionamento atual (se houver) no mesmo arquivo — este é o
        // ".json completo" (apps/Office + provisionamento) que tanto /auto quanto o
        // backup em nuvem esperam. _provisioningService.Current fica preenchido tanto por
        // "Aplicar agora" quanto por Exportar/Importar na tela Provisionamento.
        var provisioning = _provisioningService.Current;
        var manifest = _profileService.BuildFromSelection(activeTab.Items, activeTab.Title, provisioning);
        await _profileService.ExportAsync(manifest, saveFileDialog.FileName);

        StatusText.Text = provisioning is not null
            ? $"Perfil '{activeTab.Title}' exportado com sucesso, incluindo o provisionamento atual."
            : $"Perfil '{activeTab.Title}' exportado com sucesso (sem provisionamento — nada configurado na tela Provisionamento ainda).";
    }

    // -------------------------------------------------------------
    // EXPORTAÇÃO DINÂMICA -> SCRIPT POWERSHELL (.PS1)
    // -------------------------------------------------------------
    private async void ExportScriptButton_Click(object sender, RoutedEventArgs e)
    {
        var activeTab = _collectionService.ActiveTab;
        if (activeTab == null || activeTab.Items.Count == 0)
        {
            StatusText.Text = "A guia atual está vazia — nada para gerar script.";
            return;
        }

        var saveFileDialog = new Microsoft.Win32.SaveFileDialog
        {
            Filter = "PowerShell Script (*.ps1)|*.ps1",
            FileName = $"{activeTab.Title.ToLower().Replace(" ", "-")}-install.ps1",
            Title = "Salvar Script de Instalação PowerShell"
        };

        if (saveFileDialog.ShowDialog() != true) return;

        var scriptBuilder = new StringBuilder();

        // Cabeçalho e configurações do PowerShell
        scriptBuilder.AppendLine("# ========================================================");
        scriptBuilder.AppendLine($"# WinProvision - Script Dinâmico de Instalação");
        scriptBuilder.AppendLine($"# Guia / Perfil: {activeTab.Title}");
        scriptBuilder.AppendLine($"# Gerado em: {DateTime.Now:dd/MM/yyyy HH:mm:ss}");
        scriptBuilder.AppendLine("# ========================================================");
        scriptBuilder.AppendLine();
        scriptBuilder.AppendLine("$ErrorActionPreference = 'Continue'");
        scriptBuilder.AppendLine("Write-Host 'Iniciando instalação dos aplicativos...' -ForegroundColor Green");
        scriptBuilder.AppendLine();

        // Itera sobre a lista de itens da guia ativa
        foreach (var app in activeTab.Items)
        {
            if (app.Office is not null)
            {
                // Plano de Office: não é um pacote winget, então não dá pra gerar um
                // "winget install" válido pra ele aqui. Deixa registrado no script pra
                // não sumir silenciosamente — a instalação real desse item continua
                // sendo feita pelo botão Instalar na tela Pacotes (ou na OfficePage),
                // que já sabe rodar o pipeline do ODT com os parâmetros salvos.
                scriptBuilder.AppendLine($"# {app.Name} é um plano de Office (ODT) — não instalável via winget.");
                scriptBuilder.AppendLine("# Use o botão \"Instalar\" na tela Pacotes do WinProvision Store para aplicar este plano.");
                scriptBuilder.AppendLine();
                continue;
            }

            scriptBuilder.AppendLine($"Write-Host 'Instalando {app.Name} ({app.Id})...' -ForegroundColor Cyan");
            scriptBuilder.AppendLine($"winget install --id \"{app.Id}\" --exact --source winget --accept-source-agreements --disable-interactivity --silent --accept-package-agreements --force");
            scriptBuilder.AppendLine();
        }

        scriptBuilder.AppendLine("Write-Host 'Processo de instalação concluído!' -ForegroundColor Green");

        try
        {
            await File.WriteAllTextAsync(saveFileDialog.FileName, scriptBuilder.ToString(), Encoding.UTF8);
            StatusText.Text = $"Script .ps1 para '{activeTab.Title}' exportado com sucesso!";
        }
        catch (Exception ex)
        {
            StatusText.Text = $"Erro ao salvar o script: {ex.Message}";
        }
    }
}