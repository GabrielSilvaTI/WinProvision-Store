using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Navigation;
using Microsoft.Extensions.DependencyInjection;
using WinProvision.Core.Models;
using WinProvision.Core.Services;
using WinProvision.Core.Services.Backup;
using WinProvision.Core.Services.Profile;
using WinProvision.Core.Services.Provisioning;

namespace WinProvision.Store;

public partial class AccountSyncPage : Page
{
    private readonly GitHubBackupService _backupService;
    private readonly LocalBackupService _localBackupService;
    private readonly BackupAutoSyncService _autoSyncService;
    private readonly PackageCollectionService _collectionService;
    private readonly ProfileService _profileService;
    private readonly StoreService _storeService;
    private readonly ProvisioningService _provisioningService;
    private readonly CliPresetsService _cliPresetsService;

    public AccountSyncPage()
    {
        InitializeComponent();

        _backupService = App.Services.GetRequiredService<GitHubBackupService>();
        _localBackupService = App.Services.GetRequiredService<LocalBackupService>();
        _autoSyncService = App.Services.GetRequiredService<BackupAutoSyncService>();
        _collectionService = App.Services.GetRequiredService<PackageCollectionService>();
        _profileService = App.Services.GetRequiredService<ProfileService>();
        _storeService = App.Services.GetRequiredService<StoreService>();
        _provisioningService = App.Services.GetRequiredService<ProvisioningService>();
        _cliPresetsService = App.Services.GetRequiredService<CliPresetsService>();

        RefreshConnectionUi();
        RefreshLocalBackupUi();
        UpdateCliCommandPreview();

        _autoSyncService.SyncAttempted += AutoSyncService_SyncAttempted;
        Unloaded += (_, _) =>
        {
            _autoSyncService.SyncAttempted -= AutoSyncService_SyncAttempted;
            _oauthCts?.Cancel();
            _oauthCts?.Dispose();
            _oauthCts = null;
        };
    }

    private void AutoSyncService_SyncAttempted()
    {
        Dispatcher.BeginInvoke(() =>
        {
            RefreshConnectionUi();
            RefreshLocalBackupUi();
            UpdateCliCommandPreview();
        });
    }

    // -------------------------------------------------------------
    // GITHUB GIST — Vínculo de conta
    // -------------------------------------------------------------

    private void RefreshConnectionUi()
    {
        bool connected = _backupService.IsConnected;

        // ── Painel lateral ──
        SidebarDisconnectedPanel.Visibility = connected ? Visibility.Collapsed : Visibility.Visible;
        SidebarConnectedPanel.Visibility = connected ? Visibility.Visible : Visibility.Collapsed;

        if (connected)
        {
            string lastSync = _backupService.LastSyncUtc is { } utc
                ? utc.ToLocalTime().ToString("dd/MM/yyyy HH:mm")
                : "—";

            // Sidebar
            SidebarLoginText.Text = $"@{_backupService.ConnectedLogin}";
            SidebarLastSyncText.Text = lastSync;
            SidebarSyncStatusText.Text = "Ativa ●";

            string? avatarUrl = _backupService.ConnectedAvatarUrl;
            Converters.AsyncImage.SetSourceUrl(SidebarAvatarImage, avatarUrl);
        }
        else
        {
            Converters.AsyncImage.SetSourceUrl(SidebarAvatarImage, null);
        }
    }


    private const string GitHubClientId = "Ov23lii0QqRFfDSt5UA8";
    private const string GitHubDeviceCodeEndpoint = "https://github.com/login/device/code";
    private const string GitHubAccessTokenEndpoint = "https://github.com/login/oauth/access_token";

    private CancellationTokenSource? _oauthCts;

    private void ResetOAuthUi()
    {
        _oauthCts?.Cancel();
        _oauthCts?.Dispose();
        _oauthCts = null;

        OAuthInitialPanel.Visibility = Visibility.Visible;
        OAuthWaitingPanel.Visibility = Visibility.Collapsed;
        OAuthUserCodeText.Text = string.Empty;
        ConnectButton.IsEnabled = true;
    }

    private void OAuthLink_RequestNavigate(object sender, RequestNavigateEventArgs e)
    {
        try
        {
            Process.Start(new ProcessStartInfo(e.Uri.AbsoluteUri) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            StatusText.Text = $"Não foi possível abrir o navegador: {ex.Message}";
        }
        e.Handled = true;
    }

    private void CopyOAuthCodeButton_Click(object sender, RoutedEventArgs e)
    {
        if (!string.IsNullOrWhiteSpace(OAuthUserCodeText.Text))
        {
            Clipboard.SetText(OAuthUserCodeText.Text);
            StatusText.Text = "Código copiado para a área de transferência.";
        }
    }

    private void CancelOAuthButton_Click(object sender, RoutedEventArgs e)
    {
        ResetOAuthUi();
        StatusText.Text = "Autenticação via GitHub cancelada.";
    }

    private async void ConnectButton_Click(object sender, RoutedEventArgs e)
    {
        ConnectButton.IsEnabled = false;
        StatusText.Text = "Iniciando autenticação OAuth com o GitHub...";

        _oauthCts?.Cancel();
        _oauthCts?.Dispose();
        _oauthCts = new CancellationTokenSource();
        var ct = _oauthCts.Token;

        try
        {
            using var http = new HttpClient();
            http.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("WinProvision-Store", "1.0"));
            http.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));

            // 1. Solicita o device_code e user_code com escopo 'gist'
            var deviceCodeRequest = new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["client_id"] = GitHubClientId,
                ["scope"] = "gist"
            });

            var deviceResponse = await http.PostAsync(GitHubDeviceCodeEndpoint, deviceCodeRequest, ct);
            if (!deviceResponse.IsSuccessStatusCode)
            {
                StatusText.Text = $"Erro ao contatar o GitHub ({(int)deviceResponse.StatusCode}). Verifique sua conexão.";
                ResetOAuthUi();
                return;
            }

            var deviceData = await deviceResponse.Content.ReadFromJsonAsync<GitHubDeviceCodeResponse>(cancellationToken: ct);
            if (deviceData?.DeviceCode is null || deviceData.UserCode is null)
            {
                StatusText.Text = "Resposta inesperada do GitHub ao iniciar OAuth.";
                ResetOAuthUi();
                return;
            }

            // Exibe o código e link na UI
            OAuthUserCodeText.Text = deviceData.UserCode;
            OAuthInitialPanel.Visibility = Visibility.Collapsed;
            OAuthWaitingPanel.Visibility = Visibility.Visible;
            OAuthStatusText.Text = "Aguardando confirmação no navegador...";
            StatusText.Text = $"Código gerado: {deviceData.UserCode}. Confirme no navegador.";

            // Abre a página de autorização automaticamente no navegador padrão
            string verificationUrl = string.IsNullOrWhiteSpace(deviceData.VerificationUri)
                ? "https://github.com/login/device"
                : deviceData.VerificationUri;

            try
            {
                Process.Start(new ProcessStartInfo(verificationUrl) { UseShellExecute = true });
            }
            catch
            {
                // Se não conseguir abrir automaticamente, o hyperlink na tela permite ao usuário clicar
            }

            // 2. Polling até o usuário autorizar ou expirar
            int intervalSeconds = Math.Max(5, deviceData.Interval);
            int expiresInSeconds = deviceData.ExpiresIn > 0 ? deviceData.ExpiresIn : 900;
            var expireTime = DateTime.UtcNow.AddSeconds(expiresInSeconds);

            while (!ct.IsCancellationRequested && DateTime.UtcNow < expireTime)
            {
                await Task.Delay(TimeSpan.FromSeconds(intervalSeconds), ct);

                var pollRequest = new FormUrlEncodedContent(new Dictionary<string, string>
                {
                    ["client_id"] = GitHubClientId,
                    ["device_code"] = deviceData.DeviceCode,
                    ["grant_type"] = "urn:ietf:params:oauth:grant-type:device_code"
                });

                var pollResponse = await http.PostAsync(GitHubAccessTokenEndpoint, pollRequest, ct);
                if (!pollResponse.IsSuccessStatusCode)
                {
                    continue;
                }

                var tokenData = await pollResponse.Content.ReadFromJsonAsync<GitHubTokenResponse>(cancellationToken: ct);
                if (tokenData?.AccessToken is not null)
                {
                    // Conexão bem-sucedida!
                    OAuthStatusText.Text = "Autorizado! Conectando conta...";
                    StatusText.Text = "Token obtido com sucesso. Vinculando perfil...";

                    var connectResult = await _backupService.ConnectAsync(tokenData.AccessToken, ct);
                    if (connectResult.Success)
                    {
                        ResetOAuthUi();
                        RefreshConnectionUi();
                        UpdateCliCommandPreview();
                        StatusText.Text = $"Conta vinculada com sucesso como @{_backupService.ConnectedLogin}.";
                        return;
                    }
                    else
                    {
                        StatusText.Text = connectResult.ErrorMessage ?? "Erro ao validar perfil do GitHub.";
                        ResetOAuthUi();
                        return;
                    }
                }

                if (tokenData?.Error == "authorization_pending")
                {
                    // Continua aguardando o usuário
                    continue;
                }
                else if (tokenData?.Error == "slow_down")
                {
                    intervalSeconds += 5;
                    continue;
                }
                else if (tokenData?.Error == "expired_token")
                {
                    StatusText.Text = "O código expirou. Clique em 'Entrar com GitHub' para gerar um novo.";
                    ResetOAuthUi();
                    return;
                }
                else if (tokenData?.Error == "access_denied")
                {
                    StatusText.Text = "Autorização cancelada pelo usuário no GitHub.";
                    ResetOAuthUi();
                    return;
                }
                else if (!string.IsNullOrEmpty(tokenData?.Error))
                {
                    StatusText.Text = $"Erro na autenticação: {tokenData.ErrorDescription ?? tokenData.Error}";
                    ResetOAuthUi();
                    return;
                }
            }

            if (DateTime.UtcNow >= expireTime)
            {
                StatusText.Text = "Tempo limite esgotado. Tente novamente.";
                ResetOAuthUi();
            }
        }
        catch (OperationCanceledException)
        {
            // Cancelado pelo usuário
        }
        catch (Exception ex)
        {
            StatusText.Text = $"Erro durante autenticação: {ex.Message}";
            ResetOAuthUi();
        }
    }

    private void DisconnectButton_Click(object sender, RoutedEventArgs e)
    {
        _backupService.Disconnect();
        RefreshConnectionUi();
        UpdateCliCommandPreview();
        StatusText.Text = "Conta GitHub desvinculada. O backup local continua funcionando normalmente.";
    }

    // -------------------------------------------------------------
    // BACKUP LOCAL & EXPORTAÇÃO JSON
    // -------------------------------------------------------------

    private void RefreshLocalBackupUi()
    {
        var lastLocal = _localBackupService.LastBackupUtc;

        LocalBackupStatusText.Text = lastLocal is { } utc
            ? $"Último backup: {utc.ToLocalTime():dd/MM/yyyy HH:mm}"
            : "Nenhum backup realizado ainda.";
    }

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

        var mergedApps = nonEmptyTabs
            .SelectMany(t => t.Items)
            .GroupBy(app => app.Id, StringComparer.OrdinalIgnoreCase)
            .Select(g => g.First());

        try
        {
            var manifest = _profileService.BuildFromSelection(mergedApps, "Perfil completo", provisioning);
            await _profileService.ExportAsync(manifest, saveFileDialog.FileName);

            StatusText.Text = provisioning is not null
                ? $"Perfil completo exportado: {manifest.Apps.Count} pacote(s) + provisionamento, em '{saveFileDialog.FileName}'."
                : $"Perfil completo exportado: {manifest.Apps.Count} pacote(s), em '{saveFileDialog.FileName}'.";
        }
        catch (Exception ex)
        {
            StatusText.Text = $"Não foi possível exportar: o perfil é inválido. {ex.Message}";
        }
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
            string importedJson = await File.ReadAllTextAsync(openFileDialog.FileName);
            var validation = ProfileJsonValidator.Validate(importedJson);
            if (!validation.IsValid)
            {
                string detail = validation.Path is { Length: > 0 }
                    ? $"{validation.Message} Campo: {validation.Path}"
                    : validation.Message;
                StatusText.Text = $"Não foi possível importar: o JSON é inválido. {detail}";
                var editor = new ProvisioningJsonEditorWindow(importedJson)
                {
                    Owner = Window.GetWindow(this)
                };
                editor.ShowDialog();
                return;
            }

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

    private async void BackupButton_Click(object sender, RoutedEventArgs e)
    {
        var nonEmptyTabs = _collectionService.Tabs.Where(t => t.Items.Count > 0).ToList();
        var provisioning = _provisioningService.Current;

        if (nonEmptyTabs.Count == 0 && provisioning is null)
        {
            StatusText.Text = "Nada para salvar no backup — nenhuma guia tem pacotes e nenhum provisionamento foi configurado ainda.";
            return;
        }

        BackupButton.IsEnabled = false;
        StatusText.Text = "Realizando backup...";

        try
        {
            await _autoSyncService.RunSyncAsync();

            RefreshConnectionUi();
            RefreshLocalBackupUi();
            UpdateCliCommandPreview();

            StatusText.Text = _backupService.IsConnected
                ? "Backup realizado com sucesso (local e na nuvem)."
                : "Backup local realizado com sucesso.";
        }
        catch (Exception ex)
        {
            StatusText.Text = $"Erro ao realizar backup: {ex.Message}";
        }
        finally
        {
            BackupButton.IsEnabled = true;
        }
    }

    // -------------------------------------------------------------
    // LINHA DE COMANDO (CLI /auto)
    // -------------------------------------------------------------

    private void CliField_Changed(object sender, RoutedEventArgs e) => UpdateCliCommandPreview();

    private void UpdateCliCommandPreview()
    {
        if (CliCommandPreviewTextBox is null) return;

        string? gistUrl = _backupService.BackupRawUrl;

        string path = gistUrl
            ?? _cliPresetsService.ProfilePathOrUrl
            ?? "<conecte-sua-conta-github-em-conta-e-sincronizacao>";

        var command = new StringBuilder(".\\WinProvision.Store.exe /auto \"")
            .Append(path).Append('"');

        if (CliUiModeToggle?.IsChecked != true)
        {
            command.Append(" /silent");
        }

        if (CliLogToggle?.IsChecked == true)
        {
            string logPath = CliLogPathTextBox?.Text.Trim() ?? string.Empty;
            if (logPath.Length > 0)
            {
                command.Append(" /log \"").Append(logPath).Append('"');
            }
        }

        if (CliCloudLogToggle?.IsChecked == true)
        {
            command.Append(" /cloudlog");
        }

        CliCommandPreviewTextBox.Text = command.ToString();
    }

    private void CopyCliCommandButton_Click(object sender, RoutedEventArgs e)
    {
        Clipboard.SetText(CliCommandPreviewTextBox.Text);
        StatusText.Text = "Comando copiado para a área de transferência.";
    }

    private class GitHubDeviceCodeResponse
    {
        [JsonPropertyName("device_code")]
        public string? DeviceCode { get; set; }

        [JsonPropertyName("user_code")]
        public string? UserCode { get; set; }

        [JsonPropertyName("verification_uri")]
        public string? VerificationUri { get; set; }

        [JsonPropertyName("expires_in")]
        public int ExpiresIn { get; set; }

        [JsonPropertyName("interval")]
        public int Interval { get; set; }
    }

    private class GitHubTokenResponse
    {
        [JsonPropertyName("access_token")]
        public string? AccessToken { get; set; }

        [JsonPropertyName("token_type")]
        public string? TokenType { get; set; }

        [JsonPropertyName("scope")]
        public string? Scope { get; set; }

        [JsonPropertyName("error")]
        public string? Error { get; set; }

        [JsonPropertyName("error_description")]
        public string? ErrorDescription { get; set; }
    }
}
