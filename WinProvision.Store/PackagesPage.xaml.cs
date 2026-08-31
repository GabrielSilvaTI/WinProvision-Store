using System;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
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

        // Conecta a lista de abas ao TabControl
        ProfileTabControl.ItemsSource = _collectionService.Tabs;
        ProfileTabControl.SelectedItem = _collectionService.ActiveTab;

        UpdateStatus();
    }

    private void UpdateStatus()
    {
        var active = _collectionService.ActiveTab;
        if (active == null) return;

        StatusText.Text = active.Items.Count == 0
            ? $"[{active.Title}] Nenhuma aplicativo adicionado ainda."
            : $"[{active.Title}] {active.Items.Count} pacote(s) na coleção.";
    }

    private void NewTabButton_Click(object sender, RoutedEventArgs e)
    {
        var newTab = _collectionService.CreateNewTab();
        ProfileTabControl.SelectedItem = newTab;
        UpdateStatus();
    }

    private void CloseTabButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: PackageProfileTab tab })
        {
            _collectionService.CloseTab(tab);
            ProfileTabControl.SelectedItem = _collectionService.ActiveTab;
            UpdateStatus();
        }
    }

    private void ProfileTabControl_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (ProfileTabControl.SelectedItem is PackageProfileTab selectedTab)
        {
            _collectionService.ActiveTab = selectedTab;
            UpdateStatus();
        }
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