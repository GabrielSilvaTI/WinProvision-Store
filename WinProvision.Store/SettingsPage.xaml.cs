using System;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using Microsoft.Extensions.DependencyInjection;
using WinProvision.Core.Models;
using WinProvision.Core.Services;
using WinProvision.Core.Services.Backup;
using WinProvision.Core.Services.Profile;
using WinProvision.Core.Services.Provisioning;

namespace WinProvision.Store;

public partial class SettingsPage : Page
{
    private readonly GitHubBackupService _backupService;
    private readonly LocalBackupService _localBackupService;
    private readonly BackupAutoSyncService _autoSyncService;
    private readonly PackageCollectionService _collectionService;
    private readonly ProfileService _profileService;
    private readonly StoreService _storeService;
    private readonly ProvisioningService _provisioningService;

    public SettingsPage()
    {
        InitializeComponent();

        _backupService = App.Services.GetRequiredService<GitHubBackupService>();
        _localBackupService = App.Services.GetRequiredService<LocalBackupService>();
        _autoSyncService = App.Services.GetRequiredService<BackupAutoSyncService>();
        _collectionService = App.Services.GetRequiredService<PackageCollectionService>();
        _profileService = App.Services.GetRequiredService<ProfileService>();
        _provisioningService = App.Services.GetRequiredService<ProvisioningService>();
        _storeService = App.Services.GetRequiredService<StoreService>();

        RefreshConnectionUi();
        RefreshLocalBackupUi();

        // Backup automático pode terminar em segundo plano (após um install/uninstall)
        // enquanto esta página está aberta — reflete o resultado sem precisar reabrir a tela.
        _autoSyncService.SyncAttempted += AutoSyncService_SyncAttempted;
        Unloaded += (_, _) => _autoSyncService.SyncAttempted -= AutoSyncService_SyncAttempted;
    }

    private void AutoSyncService_SyncAttempted()
    {
        Dispatcher.BeginInvoke(() =>
        {
            RefreshConnectionUi();
            RefreshLocalBackupUi();
        });
    }

    private void RefreshConnectionUi()
    {
        bool connected = _backupService.IsConnected;

        ConnectPanel.Visibility = connected ? Visibility.Collapsed : Visibility.Visible;
        ConnectedPanel.Visibility = connected ? Visibility.Visible : Visibility.Collapsed;

        if (connected)
        {
            string lastSync = _backupService.LastSyncUtc is { } utc
                ? utc.ToLocalTime().ToString("dd/MM/yyyy HH:mm")
                : "ainda não sincronizado";

            ConnectedStatusText.Text = $"Conectado como @{_backupService.ConnectedLogin} · Última sincronização: {lastSync}";
        }
    }

    /// <summary>
    /// Backup local independe de login — atualiza o cartão correspondente sempre,
    /// mesmo com o GitHub desconectado (ver adendo: "login não é obrigatório").
    /// </summary>
    private void RefreshLocalBackupUi()
    {
        var lastLocal = _localBackupService.LastBackupUtc;

        LocalBackupStatusText.Text = lastLocal is { } utc
            ? $"Último backup local: {utc.ToLocalTime():dd/MM/yyyy HH:mm}"
            : "Nenhum backup local ainda — será criado automaticamente ao instalar ou remover um pacote.";

        RestoreLocalButton.IsEnabled = lastLocal is not null;
    }

    private async void ConnectButton_Click(object sender, RoutedEventArgs e)
    {
        string token = TokenPasswordBox.Password;

        ConnectButton.IsEnabled = false;
        StatusText.Text = "Validando token com o GitHub...";

        try
        {
            var result = await _backupService.ConnectAsync(token);

            if (result.Success)
            {
                TokenPasswordBox.Clear();
                RefreshConnectionUi();
                StatusText.Text = $"Conectado como @{_backupService.ConnectedLogin}.";
            }
            else
            {
                StatusText.Text = result.ErrorMessage ?? "Não foi possível conectar.";
            }
        }
        finally
        {
            ConnectButton.IsEnabled = true;
        }
    }

    private void DisconnectButton_Click(object sender, RoutedEventArgs e)
    {
        _backupService.Disconnect();
        RefreshConnectionUi();
        StatusText.Text = "Desconectado. O backup na nuvem não é mais atualizado automaticamente — o backup local continua funcionando normalmente.";
    }

    /// <summary>
    /// Gera um único .json "completo": todos os apps de TODAS as guias abertas (não só a
    /// ativa, e sem duplicar Ids repetidos entre guias) + o provisionamento atual (se
    /// houver) — o mesmo formato que /auto e /Provision esperam. Diferente de
    /// "Sincronizar agora" (que só sobe pro backup local/Gist, sem gerar um arquivo pra
    /// baixar), este botão sempre abre o SaveFileDialog.
    /// </summary>
    private async void ExportAllButton_Click(object sender, RoutedEventArgs e)
    {
        var nonEmptyTabs = _collectionService.Tabs.Where(t => t.Items.Count > 0).ToList();
        var provisioning = _provisioningService.Current;

        if (nonEmptyTabs.Count == 0 && provisioning is null)
        {
            StatusText.Text = "Nada para exportar — nenhuma guia de pacotes tem itens e nenhum provisionamento foi configurado ainda.";
            return;
        }

        var saveFileDialog = new Microsoft.Win32.SaveFileDialog
        {
            Filter = "JSON Profile (*.json)|*.json",
            FileName = "perfil-completo.json",
            Title = "Salvar Perfil Completo (Pacotes + Provisionamento)"
        };

        if (saveFileDialog.ShowDialog() != true) return;

        // Uma única lista de Apps (não o formato multi-guia do ProfileBackupSet) porque é
        // isto que /auto e /Provision (ProfileService.ImportAsync) sabem ler — Ids
        // repetidos entre guias são mesclados, mantendo a primeira ocorrência.
        var mergedApps = nonEmptyTabs
            .SelectMany(t => t.Items)
            .GroupBy(app => app.Id, StringComparer.OrdinalIgnoreCase)
            .Select(g => g.First());

        var manifest = _profileService.BuildFromSelection(mergedApps, "Perfil completo", provisioning);
        await _profileService.ExportAsync(manifest, saveFileDialog.FileName);

        StatusText.Text = provisioning is not null
            ? $"Perfil completo exportado: {manifest.Apps.Count} pacote(s) + provisionamento, em '{saveFileDialog.FileName}'."
            : $"Perfil completo exportado: {manifest.Apps.Count} pacote(s) (sem provisionamento — nada configurado na tela Provisionamento ainda), em '{saveFileDialog.FileName}'.";
    }

    /// <summary>
    /// Salva o backup local (sempre) e, se conectado, também sincroniza com o Gist —
    /// mesma rotina que roda sozinha após cada instalar/remover (ver BackupAutoSyncService),
    /// só que sem esperar o debounce.
    /// </summary>
    private async void SyncNowButton_Click(object sender, RoutedEventArgs e)
    {
        var activeTab = _collectionService.ActiveTab;
        bool hasPackages = activeTab is not null && activeTab.Items.Count > 0;
        bool hasProvisioning = _provisioningService.Current is not null;

        if (!hasPackages && !hasProvisioning)
        {
            StatusText.Text = "A guia ativa da tela Pacotes está vazia e nenhum provisionamento foi aplicado/exportado ainda — nada para sincronizar.";
            return;
        }

        SyncNowButton.IsEnabled = false;
        StatusText.Text = hasPackages
            ? $"Salvando '{activeTab!.Title}'..."
            : "Salvando ajustes de provisionamento...";

        try
        {
            await _autoSyncService.RunSyncAsync();

            RefreshConnectionUi();
            RefreshLocalBackupUi();

            string label = hasPackages ? $"'{activeTab!.Title}'" : "Provisionamento";
            StatusText.Text = _backupService.IsConnected
                ? $"{label} sincronizado com sucesso (local + nuvem)."
                : $"{label} salvo no backup local. Conecte-se ao GitHub acima para sincronizar também na nuvem.";
        }
        finally
        {
            SyncNowButton.IsEnabled = true;
        }
    }

    /// <summary>
    /// Baixa o perfil salvo no Gist e importa numa guia nova — mesmo padrão do
    /// ImportProfileButton_Click da PackagesPage (reconcilia apps winget contra o
    /// catálogo remoto vivo; planos de Office vêm autocontidos no próprio perfil).
    /// </summary>
    private async void RestoreButton_Click(object sender, RoutedEventArgs e)
    {
        RestoreButton.IsEnabled = false;
        StatusText.Text = "Baixando backup do Gist...";

        try
        {
            var backupSet = await _backupService.DownloadProfileAsync();

            if (backupSet is null || (backupSet.Tabs.Count == 0 && backupSet.Provisioning is null))
            {
                StatusText.Text = "Nenhum backup encontrado nessa conta GitHub.";
                return;
            }

            StatusText.Text = await RestoreBackupSetAsync(backupSet, "nuvem");
        }
        catch (Exception ex)
        {
            StatusText.Text = $"Erro ao restaurar: {ex.Message}";
        }
        finally
        {
            RestoreButton.IsEnabled = true;
        }
    }

    /// <summary>
    /// Mesma restauração do RestoreButton_Click, só que a partir do arquivo local em
    /// vez do Gist — funciona mesmo sem nunca ter feito login (ver LocalBackupService).
    /// </summary>
    private async void RestoreLocalButton_Click(object sender, RoutedEventArgs e)
    {
        RestoreLocalButton.IsEnabled = false;
        StatusText.Text = "Lendo backup local...";

        try
        {
            var backupSet = await _localBackupService.TryLoadLatestAsync();

            if (backupSet is null || (backupSet.Tabs.Count == 0 && backupSet.Provisioning is null))
            {
                StatusText.Text = "Nenhum backup local encontrado nesta máquina.";
                return;
            }

            StatusText.Text = await RestoreBackupSetAsync(backupSet, "local");
        }
        catch (Exception ex)
        {
            StatusText.Text = $"Erro ao restaurar do backup local: {ex.Message}";
        }
        finally
        {
            RefreshLocalBackupUi();
        }
    }

    /// <summary>
    /// Recria TODAS as guias do backup (uma guia nova por entrada em ProfileBackupSet.Tabs),
    /// na mesma ordem em que foram salvas, e — se o backup também trouxer uma seção de
    /// provisionamento — aplica esses ajustes de sistema na máquina atual (mesmo caminho do
    /// botão "Aplicar agora" da tela Provisionamento). Devolve uma mensagem de status
    /// resumindo o resultado pra exibir em StatusText.
    /// </summary>
    private async Task<string> RestoreBackupSetAsync(ProfileBackupSet backupSet, string origemLabel)
    {
        var restoredTabs = backupSet.Tabs.Select(ImportManifestAsNewTab).ToList();
        int totalPackages = restoredTabs.Sum(t => t.Items.Count);

        string? packagesSummary = restoredTabs.Count switch
        {
            0 => null,
            1 => $"nova guia '{restoredTabs[0].Title}' ({totalPackages} pacote(s))",
            _ => $"{restoredTabs.Count} guia(s), {totalPackages} pacote(s) no total"
        };

        string? provisioningSummary = null;
        if (backupSet.Provisioning is { } provisioning)
        {
            var result = await _provisioningService.ApplyAsync(provisioning);
            int ok = result.Steps.Count(s => s.Success);
            int failed = result.Steps.Count - ok;

            provisioningSummary = failed == 0
                ? $"provisionamento aplicado ({ok} ajuste(s))"
                : $"provisionamento aplicado com falhas ({ok} sucesso(s), {failed} falha(s))";

            if (result.RestartRequired)
                provisioningSummary += " — reinicie o Windows para concluir";
        }

        string body = (packagesSummary, provisioningSummary) switch
        {
            (not null, not null) => $"{packagesSummary}; {provisioningSummary}",
            (not null, null) => packagesSummary,
            (null, not null) => provisioningSummary,
            (null, null) => "nada a restaurar"
        };

        return $"Backup {origemLabel} restaurado: {body}. Veja a tela Pacotes/Provisionamento.";
    }

    /// <summary>
    /// Reconstrói uma guia de Pacotes a partir de um ProfileManifest (uma entrada dentro
    /// do ProfileBackupSet): apps winget comuns são reconciliados contra o catálogo
    /// remoto vivo (StoreService); planos de Office vêm autocontidos no próprio
    /// manifesto, sem depender do catálogo.
    /// </summary>
    private PackageProfileTab ImportManifestAsNewTab(ProfileManifest manifest)
    {
        var pendingWingetIds = manifest.Apps
            .Where(a => a.OfficeOptions is null)
            .Select(a => a.Id)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var matchedWingetApps = _storeService.GetAll().Where(app => pendingWingetIds.Contains(app.Id));

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

        var restoredTab = _collectionService.CreateNewTab(manifest.Name ?? "Backup restaurado");
        foreach (var app in matchedWingetApps.Concat(officeEntries))
        {
            restoredTab.Items.Add(app);
        }

        return restoredTab;
    }
}
