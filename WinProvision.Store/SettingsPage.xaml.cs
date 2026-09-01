using System;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using Microsoft.Extensions.DependencyInjection;
using Wpf.Ui.Appearance;
using Wpf.Ui.Controls;
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
    private readonly CliPresetsService _cliPresetsService;
    private readonly CacheService _cacheService;

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
        _cliPresetsService = App.Services.GetRequiredService<CliPresetsService>();
        _cacheService = App.Services.GetRequiredService<CacheService>();

        RefreshConnectionUi();
        RefreshLocalBackupUi();
        RefreshThemeButtonsUi();
        RefreshCliDefaultsUi();

        // Mantém os botões Claro/Escuro coerentes mesmo quando o tema muda "sozinho"
        // (ex.: o botão sol/lua da barra de título, ou o tema do Windows via SystemThemeWatcher).
        ApplicationThemeManager.Changed += ApplicationThemeManager_Changed;

        // A tela de Provisionamento também pode salvar um novo padrão (botão "Salvar como
        // padrão" no gerador de CLI) enquanto esta página já está aberta — reflete sem
        // precisar reabrir Configurações. Não desliga no Unloaded de propósito: esta
        // página é Singleton (vive o app inteiro), então não há vazamento de memória aqui.
        _cliPresetsService.Changed += () => Dispatcher.BeginInvoke(RefreshCliDefaultsUi);

        // Backup automático pode terminar em segundo plano (após um install/uninstall)
        // enquanto esta página está aberta — reflete o resultado sem precisar reabrir a tela.
        _autoSyncService.SyncAttempted += AutoSyncService_SyncAttempted;
        Unloaded += (_, _) =>
        {
            _autoSyncService.SyncAttempted -= AutoSyncService_SyncAttempted;
            ApplicationThemeManager.Changed -= ApplicationThemeManager_Changed;
        };
    }

    // -------------------------------------------------------------
    // NAVEGAÇÃO ENTRE SEÇÕES (Drill-down: Menu Principal -> Subseção)
    // -------------------------------------------------------------

    private void ShowSection(ScrollViewer sectionPanel, string title)
    {
        // Oculta o menu principal e mostra a área da subseção
        SettingsOverviewPanel.Visibility = Visibility.Collapsed;
        SectionPanel.Visibility = Visibility.Visible;
        SectionTitleText.Text = title;

        // Oculta todas as subseções e mostra apenas a selecionada
        AccountSectionPanel.Visibility = Visibility.Collapsed;
        AppearanceSectionPanel.Visibility = Visibility.Collapsed;
        BackupSectionPanel.Visibility = Visibility.Collapsed;

        sectionPanel.Visibility = Visibility.Visible;
    }

    private void BackToSettingsButton_Click(object sender, RoutedEventArgs e)
    {
        // Oculta a área da subseção e volta para o menu principal
        SectionPanel.Visibility = Visibility.Collapsed;
        SettingsOverviewPanel.Visibility = Visibility.Visible;

        AccountSectionPanel.Visibility = Visibility.Collapsed;
        AppearanceSectionPanel.Visibility = Visibility.Collapsed;
        BackupSectionPanel.Visibility = Visibility.Collapsed;
    }

    private void AccountNavCard_Click(object sender, RoutedEventArgs e) => ShowSection(AccountSectionPanel, "Conta");

    private void AppearanceNavCard_Click(object sender, RoutedEventArgs e) => ShowSection(AppearanceSectionPanel, "Aparência da UI");

    private void BackupNavCard_Click(object sender, RoutedEventArgs e) => ShowSection(BackupSectionPanel, "Backup & Exportação");

    private void GoToAccountButton_Click(object sender, RoutedEventArgs e) => ShowSection(AccountSectionPanel, "Conta");

    private void AutoSyncService_SyncAttempted()
    {
        Dispatcher.BeginInvoke(() =>
        {
            RefreshConnectionUi();
            RefreshLocalBackupUi();
        });
    }

    // -------------------------------------------------------------
    // CONTA — vínculo/desvínculo do GitHub (Gist secreto)
    // -------------------------------------------------------------

    private void RefreshConnectionUi()
    {
        bool connected = _backupService.IsConnected;

        AccountConnectPanel.Visibility = connected ? Visibility.Collapsed : Visibility.Visible;
        AccountConnectedPanel.Visibility = connected ? Visibility.Visible : Visibility.Collapsed;
        CloudBackupDisconnectedPanel.Visibility = connected ? Visibility.Collapsed : Visibility.Visible;
        CloudBackupConnectedPanel.Visibility = connected ? Visibility.Visible : Visibility.Collapsed;

        if (connected)
        {
            string lastSync = _backupService.LastSyncUtc is { } utc
                ? utc.ToLocalTime().ToString("dd/MM/yyyy HH:mm")
                : "ainda não sincronizado";

            ConnectedStatusText.Text = $"Vinculado como @{_backupService.ConnectedLogin}.";
            CloudBackupStatusText.Text = $"Conectado como @{_backupService.ConnectedLogin} · Última sincronização: {lastSync}";
        }
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
                StatusText.Text = $"Conta vinculada como @{_backupService.ConnectedLogin}.";
            }
            else
            {
                StatusText.Text = result.ErrorMessage ?? "Não foi possível vincular a conta.";
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
        StatusText.Text = "Conta desvinculada. O backup na nuvem não é mais atualizado automaticamente — o backup local continua funcionando normalmente.";
    }

    // -------------------------------------------------------------
    // CONTA — perfil e webhook padrão do gerador de CLI (Provisionamento)
    // -------------------------------------------------------------

    private void RefreshCliDefaultsUi()
    {
        DefaultProfilePathTextBox.Text = _cliPresetsService.ProfilePathOrUrl ?? string.Empty;

        if (_cliPresetsService.WebhookUrl is { Length: > 0 } webhook)
        {
            WebhookConfiguredStatusText.Text = $"Webhook salvo ({WebhookHostLabel(webhook)}). Deixe o campo acima em branco e clique Salvar para manter o atual.";
            RemoveWebhookButton.Visibility = Visibility.Visible;
        }
        else
        {
            WebhookConfiguredStatusText.Text = "Nenhum webhook salvo ainda.";
            RemoveWebhookButton.Visibility = Visibility.Collapsed;
        }
    }

    private static string WebhookHostLabel(string url) =>
        Uri.TryCreate(url, UriKind.Absolute, out var uri) ? uri.Host : "URL salva";

    /// <summary>Salva o caminho/URL do perfil sempre; a URL do webhook só é sobrescrita se o
    /// campo (uma PasswordBox, sempre em branco ao abrir a tela) tiver algo digitado —
    /// deixá-lo em branco preserva o webhook já salvo em vez de apagá-lo.</summary>
    private void SaveDefaultsButton_Click(object sender, RoutedEventArgs e)
    {
        string path = DefaultProfilePathTextBox.Text.Trim();
        _cliPresetsService.SaveProfilePathOrUrl(path.Length > 0 ? path : null);

        string webhook = DefaultWebhookPasswordBox.Password.Trim();
        if (webhook.Length > 0)
        {
            _cliPresetsService.SaveWebhookUrl(webhook);
            DefaultWebhookPasswordBox.Clear();
        }

        RefreshCliDefaultsUi();
        StatusText.Text = "Perfil/webhook padrão salvos — vão preencher automaticamente o gerador de CLI na aba Provisionamento.";
    }

    private void RemoveWebhookButton_Click(object sender, RoutedEventArgs e)
    {
        _cliPresetsService.SaveWebhookUrl(null);
        RefreshCliDefaultsUi();
        StatusText.Text = "Webhook padrão removido.";
    }

    /// <summary>Copia o caminho/URL do perfil e (se marcado) a URL do webhook que já estão
    /// preenchidos agora mesmo no gerador de CLI da aba Provisionamento — evita ter que
    /// procurar/colar de novo o mesmo valor aqui.</summary>
    private void UseCurrentCliValuesButton_Click(object sender, RoutedEventArgs e)
    {
        var provisioningPage = App.Services.GetRequiredService<ProvisioningPage>();
        var (profilePathOrUrl, webhookUrl) = provisioningPage.GetCurrentCliFieldValues();

        if (profilePathOrUrl is null && webhookUrl is null)
        {
            StatusText.Text = "O gerador de CLI (aba Provisionamento) ainda não tem caminho/URL de perfil nem webhook preenchidos.";
            return;
        }

        if (profilePathOrUrl is not null)
        {
            DefaultProfilePathTextBox.Text = profilePathOrUrl;
        }

        if (webhookUrl is not null)
        {
            DefaultWebhookPasswordBox.Password = webhookUrl;
        }

        StatusText.Text = "Valores copiados da tela de Provisionamento — clique Salvar para confirmar.";
    }

    private async void ClearCacheButton_Click(object sender, RoutedEventArgs e)
    {
        ClearCacheButton.IsEnabled = false;
        StatusText.Text = "Limpando cache local…";
        try
        {
            await _cacheService.ClearAsync();
            Converters.AsyncImage.ClearCache();
            StatusText.Text = "Cache limpo. O catálogo será carregado novamente quando necessário.";
        }
        catch (Exception ex)
        {
            StatusText.Text = $"Não foi possível limpar todo o cache: {ex.Message}";
        }
        finally
        {
            ClearCacheButton.IsEnabled = true;
        }
    }

    // -------------------------------------------------------------
    // APARÊNCIA DA UI — tema claro/escuro do aplicativo
    // -------------------------------------------------------------

    private void LightThemeButton_Click(object sender, RoutedEventArgs e) =>
        ApplicationThemeManager.Apply(ApplicationTheme.Light, WindowBackdropType.Mica);

    private void DarkThemeButton_Click(object sender, RoutedEventArgs e) =>
        ApplicationThemeManager.Apply(ApplicationTheme.Dark, WindowBackdropType.Mica);

    private void ApplicationThemeManager_Changed(ApplicationTheme currentApplicationTheme, System.Windows.Media.Color systemAccent) =>
        Dispatcher.Invoke(RefreshThemeButtonsUi);

    private void RefreshThemeButtonsUi()
    {
        bool dark = ApplicationThemeManager.GetAppTheme() == ApplicationTheme.Dark;
        LightThemeButton.Appearance = dark ? ControlAppearance.Secondary : ControlAppearance.Primary;
        DarkThemeButton.Appearance = dark ? ControlAppearance.Primary : ControlAppearance.Secondary;
    }

    // -------------------------------------------------------------
    // BACKUP & EXPORTAÇÃO — exportar perfil, backup local e na nuvem
    // -------------------------------------------------------------

    /// <summary>
    /// Backup local independe de login — atualiza o cartão correspondente sempre,
    /// mesmo com o GitHub desvinculado (ver adendo: "login não é obrigatório").
    /// </summary>
    private void RefreshLocalBackupUi()
    {
        var lastLocal = _localBackupService.LastBackupUtc;

        LocalBackupStatusText.Text = lastLocal is { } utc
            ? $"Último backup local: {utc.ToLocalTime():dd/MM/yyyy HH:mm}"
            : "Nenhum backup local ainda — será criado automaticamente ao instalar ou remover um pacote.";

        RestoreLocalButton.IsEnabled = lastLocal is not null;
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
                : $"{label} salvo no backup local. Vincule sua conta em Conta para sincronizar também na nuvem.";
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
    /// vez do Gist — funciona mesmo sem nunca ter vinculado conta (ver LocalBackupService).
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