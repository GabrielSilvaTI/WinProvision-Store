using System;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using Microsoft.Extensions.DependencyInjection;
using WinProvision.Core.Models;
using WinProvision.Core.Services;
using WinProvision.Core.Services.Backup;
using WinProvision.Core.Services.Profile;

namespace WinProvision.Store;

public partial class SettingsPage : Page
{
    private readonly GitHubBackupService _backupService;
    private readonly LocalBackupService _localBackupService;
    private readonly BackupAutoSyncService _autoSyncService;
    private readonly PackageCollectionService _collectionService;
    private readonly ProfileService _profileService;
    private readonly StoreService _storeService;

    public SettingsPage()
    {
        InitializeComponent();

        _backupService = App.Services.GetRequiredService<GitHubBackupService>();
        _localBackupService = App.Services.GetRequiredService<LocalBackupService>();
        _autoSyncService = App.Services.GetRequiredService<BackupAutoSyncService>();
        _collectionService = App.Services.GetRequiredService<PackageCollectionService>();
        _profileService = App.Services.GetRequiredService<ProfileService>();
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
    /// Salva o backup local (sempre) e, se conectado, também sincroniza com o Gist —
    /// mesma rotina que roda sozinha após cada instalar/remover (ver BackupAutoSyncService),
    /// só que sem esperar o debounce.
    /// </summary>
    private async void SyncNowButton_Click(object sender, RoutedEventArgs e)
    {
        var activeTab = _collectionService.ActiveTab;
        if (activeTab is null || activeTab.Items.Count == 0)
        {
            StatusText.Text = "A guia ativa da tela Pacotes está vazia — nada para sincronizar.";
            return;
        }

        SyncNowButton.IsEnabled = false;
        StatusText.Text = $"Salvando '{activeTab.Title}'...";

        try
        {
            await _autoSyncService.RunSyncAsync();

            RefreshConnectionUi();
            RefreshLocalBackupUi();

            StatusText.Text = _backupService.IsConnected
                ? $"'{activeTab.Title}' sincronizado com sucesso (local + nuvem)."
                : $"'{activeTab.Title}' salvo no backup local. Conecte-se ao GitHub acima para sincronizar também na nuvem.";
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

            if (backupSet is null || backupSet.Tabs.Count == 0)
            {
                StatusText.Text = "Nenhum backup encontrado nessa conta GitHub.";
                return;
            }

            StatusText.Text = ImportBackupSetAsNewTabs(backupSet, "nuvem");
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

            if (backupSet is null || backupSet.Tabs.Count == 0)
            {
                StatusText.Text = "Nenhum backup local encontrado nesta máquina.";
                return;
            }

            StatusText.Text = ImportBackupSetAsNewTabs(backupSet, "local");
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
    /// na mesma ordem em que foram salvas, e devolve uma mensagem de status resumindo o
    /// resultado pra exibir em StatusText.
    /// </summary>
    private string ImportBackupSetAsNewTabs(ProfileBackupSet backupSet, string origemLabel)
    {
        var restoredTabs = backupSet.Tabs.Select(ImportManifestAsNewTab).ToList();
        int totalPackages = restoredTabs.Sum(t => t.Items.Count);

        return restoredTabs.Count == 1
            ? $"Backup {origemLabel} restaurado em nova guia '{restoredTabs[0].Title}': {totalPackages} pacote(s). Veja a tela Pacotes."
            : $"Backup {origemLabel} restaurado: {restoredTabs.Count} guia(s), {totalPackages} pacote(s) no total. Veja a tela Pacotes.";
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
