using System;
using System.Linq;
using System.Reflection;
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
        VersionText.Text = $"Versão {GetApplicationVersion()}";

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

    private static string GetApplicationVersion()
    {
        var version = Assembly.GetEntryAssembly()?.GetName().Version;
        return version is null ? "desconhecida" : version.ToString(3);
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
    // -------------------------------------------------------------
    // CONTA — perfil e webhook padrão do gerador de CLI (Provisionamento)
    // -------------------------------------------------------------

    private bool _isWebhookVisible;
    private bool _isUpdatingWebhookText;

    private void RefreshCliDefaultsUi()
    {
        DefaultProfilePathTextBox.Text = _cliPresetsService.ProfilePathOrUrl ?? string.Empty;

        string savedWebhook = _cliPresetsService.WebhookUrl ?? string.Empty;
        SetWebhookText(savedWebhook);
        UpdateWebhookConfiguredStatus();
    }

    private void UpdateWebhookConfiguredStatus()
    {
        string current = GetWebhookText();
        if (current.Length > 0)
        {
            WebhookConfiguredStatusText.Text = $"Webhook configurado ({WebhookHostLabel(current)}).";
            RemoveWebhookButton.Visibility = Visibility.Visible;
        }
        else
        {
            WebhookConfiguredStatusText.Text = "Nenhum webhook salvo ainda.";
            RemoveWebhookButton.Visibility = Visibility.Collapsed;
        }
    }

    private string GetWebhookText() =>
        _isWebhookVisible ? DefaultWebhookTextBox.Text.Trim() : DefaultWebhookPasswordBox.Password.Trim();

    private void SetWebhookText(string value)
    {
        _isUpdatingWebhookText = true;
        try
        {
            DefaultWebhookPasswordBox.Password = value;
            DefaultWebhookTextBox.Text = value;
        }
        finally
        {
            _isUpdatingWebhookText = false;
        }
    }

    private void ToggleWebhookVisibilityButton_Click(object sender, RoutedEventArgs e)
    {
        _isWebhookVisible = !_isWebhookVisible;
        if (_isWebhookVisible)
        {
            DefaultWebhookTextBox.Text = DefaultWebhookPasswordBox.Password;
            DefaultWebhookPasswordBox.Visibility = Visibility.Collapsed;
            DefaultWebhookTextBox.Visibility = Visibility.Visible;
            ToggleWebhookVisibilityIcon.Symbol = Wpf.Ui.Controls.SymbolRegular.EyeOff24;
            ToggleWebhookVisibilityButton.ToolTip = "Ocultar URL";
            DefaultWebhookTextBox.Focus();
            DefaultWebhookTextBox.CaretIndex = DefaultWebhookTextBox.Text.Length;
        }
        else
        {
            DefaultWebhookPasswordBox.Password = DefaultWebhookTextBox.Text;
            DefaultWebhookTextBox.Visibility = Visibility.Collapsed;
            DefaultWebhookPasswordBox.Visibility = Visibility.Visible;
            ToggleWebhookVisibilityIcon.Symbol = Wpf.Ui.Controls.SymbolRegular.Eye24;
            ToggleWebhookVisibilityButton.ToolTip = "Mostrar URL";
            DefaultWebhookPasswordBox.Focus();
        }
    }

    private void DefaultWebhookPasswordBox_PasswordChanged(object sender, RoutedEventArgs e)
    {
        if (_isUpdatingWebhookText) return;
        _isUpdatingWebhookText = true;
        try
        {
            DefaultWebhookTextBox.Text = DefaultWebhookPasswordBox.Password;
        }
        finally
        {
            _isUpdatingWebhookText = false;
        }
        UpdateWebhookConfiguredStatus();
    }

    private void DefaultWebhookTextBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (_isUpdatingWebhookText) return;
        _isUpdatingWebhookText = true;
        try
        {
            DefaultWebhookPasswordBox.Password = DefaultWebhookTextBox.Text;
        }
        finally
        {
            _isUpdatingWebhookText = false;
        }
        UpdateWebhookConfiguredStatus();
    }

    private static string WebhookHostLabel(string url) =>
        Uri.TryCreate(url, UriKind.Absolute, out var uri) ? uri.Host : "URL salva";

    private void SaveDefaultsButton_Click(object sender, RoutedEventArgs e)
    {
        string path = DefaultProfilePathTextBox.Text.Trim();
        _cliPresetsService.SaveProfilePathOrUrl(path.Length > 0 ? path : null);

        string webhook = GetWebhookText();
        _cliPresetsService.SaveWebhookUrl(webhook.Length > 0 ? webhook : null);

        RefreshCliDefaultsUi();
        StatusText.Text = "Perfil e Webhook padrão salvos e sincronizados com o gerador de CLI.";
    }

    private void RemoveWebhookButton_Click(object sender, RoutedEventArgs e)
    {
        _cliPresetsService.SaveWebhookUrl(null);
        SetWebhookText(string.Empty);
        UpdateWebhookConfiguredStatus();
        StatusText.Text = "Webhook padrão removido.";
    }

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
            SetWebhookText(webhookUrl);
            UpdateWebhookConfiguredStatus();
        }

        StatusText.Text = "Valores copiados da tela de Provisionamento — clique Salvar Padrões para confirmar.";
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
        SetThemeButtonActive(LightThemeButton, !dark);
        SetThemeButtonActive(DarkThemeButton, dark);
    }

    // O tema ativo usa o mesmo azul fixo (InstallActionBrush) dos outros botões de
    // ação do app, em vez do azul padrão do tema que vem só com Appearance="Primary"
    // (ver Styles/InstallActionBrushes.xaml). O inativo volta ao Secondary padrão via
    // ClearValue, sem precisar guardar o estado anterior.
    private static void SetThemeButtonActive(Wpf.Ui.Controls.Button button, bool active)
    {
        if (active)
        {
            button.Appearance = ControlAppearance.Primary;
            button.Background = (System.Windows.Media.Brush)Application.Current.Resources["InstallActionBrush"];
            button.MouseOverBackground = (System.Windows.Media.Brush)Application.Current.Resources["InstallActionHoverBrush"];
            button.PressedBackground = (System.Windows.Media.Brush)Application.Current.Resources["InstallActionPressedBrush"];
            button.Foreground = System.Windows.Media.Brushes.White;
            button.PressedForeground = System.Windows.Media.Brushes.White;
        }
        else
        {
            button.Appearance = ControlAppearance.Secondary;
            button.ClearValue(Wpf.Ui.Controls.Button.BackgroundProperty);
            button.ClearValue(Wpf.Ui.Controls.Button.MouseOverBackgroundProperty);
            button.ClearValue(Wpf.Ui.Controls.Button.PressedBackgroundProperty);
            button.ClearValue(Wpf.Ui.Controls.Button.ForegroundProperty);
            button.ClearValue(Wpf.Ui.Controls.Button.PressedForegroundProperty);
        }
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

    private async void ImportAllButton_Click(object sender, RoutedEventArgs e)
    {
        var openFileDialog = new Microsoft.Win32.OpenFileDialog
        {
            Filter = "Perfil JSON (*.json)|*.json",
            Title = "Importar perfil completo"
        };

        if (openFileDialog.ShowDialog() != true) return;

        ImportAllButton.IsEnabled = false;
        StatusText.Text = "Importando perfil completo...";

        try
        {
            var manifest = await _profileService.ImportAsync(openFileDialog.FileName);
            var catalogById = _storeService.GetAll()
                .ToDictionary(app => app.Id, StringComparer.OrdinalIgnoreCase);
            var importedApps = manifest.Apps.Select(app =>
            {
                if (catalogById.TryGetValue(app.Id, out var catalogApp))
                {
                    catalogApp.Office = app.OfficeOptions;
                    return catalogApp;
                }

                return new AppEntry
                {
                    Id = app.Id,
                    Name = app.Name ?? app.Id,
                    Publisher = app.Publisher ?? string.Empty,
                    IconUrl = app.IconUrl ?? string.Empty,
                    Description = app.Description,
                    Office = app.OfficeOptions
                };
            }).ToList();

            var importedTab = _collectionService.CreateNewTab(
                System.IO.Path.GetFileNameWithoutExtension(openFileDialog.FileName));
            foreach (var app in importedApps)
            {
                importedTab.Items.Add(app);
            }

            if (manifest.Provisioning is { } provisioning)
            {
                _provisioningService.SetCurrent(provisioning);
            }

            StatusText.Text = $"Perfil importado: {importedApps.Count} aplicativo(s)"
                + (manifest.Provisioning is not null ? " e configurações de provisionamento." : ".");
        }
        catch (Exception ex)
        {
            StatusText.Text = $"Erro ao importar o perfil: {ex.Message}";
        }
        finally
        {
            ImportAllButton.IsEnabled = true;
        }
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
    /// Baixa o perfil salvo no Gist e importa numa guia nova, mantendo os planos
    /// de Office autocontidos no próprio backup.
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