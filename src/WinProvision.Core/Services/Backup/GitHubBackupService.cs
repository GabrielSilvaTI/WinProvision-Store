using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using WinProvision.Core.Models;
using WinProvision.Core.Services;

namespace WinProvision.Core.Services.Backup;

/// <summary>
/// Backup em nuvem de TODAS as guias de pacotes (ver <see cref="ProfileBackupSet"/>),
/// num Gist SECRETO do GitHub do próprio usuário — usando
/// Personal Access Token colado direto (sem precisar registrar um GitHub OAuth App
/// para o Device Flow).
///
/// Login é sempre OPCIONAL: sem token conectado, o app funciona normalmente e só o
/// <see cref="LocalBackupService"/> (sempre ativo, ver essa classe) mantém o backup.
/// Conectar aqui soma a cópia em nuvem por cima, sem substituir a local.
///
/// O Gist é localizado por DESCRIÇÃO fixa (<see cref="GistDescription"/>) entre os
/// gists da própria conta, não só pelo Id salvo localmente — assim, se o usuário
/// desinstalar o app, formatar a máquina ou trocar de PC e logar de novo com o mesmo
/// token/conta, o backup existente é reencontrado e reaproveitado automaticamente em
/// vez de duplicado.
/// </summary>
public class GitHubBackupService
{
    private const string ApiBase = "https://api.github.com";
    private const string BackupFileName = "profile.json";
    private const string LegacyBackupFileName = "winprovision-profile.json";
    private const string GistDescription = "WinProvision Store — backup automático de perfil (não editar manualmente)";
    private const string BootstrapGistDescription = "WinProvision Store — bootstrap de instalação e perfil";
    private const string BootstrapScriptFileName = "WinProvision-Bootstrap.ps1";
    private const int MaxTransientRetries = 2;
    private static readonly TimeSpan RetryDelay = TimeSpan.FromSeconds(1);

    // Mesmas opções usadas em todo o resto do app (ProfileService/ProvisioningService) — um
    // arquivo salvo por um lado sempre bate com o que o outro espera ao ler de volta.
    // (Anteriormente este serviço tinha sua própria referência a ManifestJsonOptions —
    //  agora usa a WinProvisionJsonOptions centralizada.)
    private static readonly JsonSerializerOptions ManifestJsonOptions = WinProvisionJsonOptions.Profile;

    private readonly HttpClient _http;
    private readonly string _accountInfoPath;
    private readonly string _tokenPath;
    private readonly SemaphoreSlim _syncLock = new(1, 1);

    private BackupAccountInfo _account = new();

    public GitHubBackupService() : this(DefaultBackupDir())
    {
    }

    internal GitHubBackupService(string backupDir)
    {
        _accountInfoPath = Path.Combine(backupDir, "github-account.json");
        _tokenPath = Path.Combine(backupDir, "github-token.dat");

        _http = new HttpClient { Timeout = TimeSpan.FromSeconds(60) };
        _http.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("WinProvision-Store", "1.0"));
        _http.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
        _http.DefaultRequestHeaders.Add("X-GitHub-Api-Version", "2022-11-28");

        LoadPersistedState();
    }

    private static string DefaultBackupDir() => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "WinProvision", "Backup");

    public bool IsConnected => !string.IsNullOrEmpty(_account.Login) && _http.DefaultRequestHeaders.Authorization is not null;

    public string? ConnectedLogin => _account.Login;

    public string? ConnectedAvatarUrl => _account.AvatarUrl
        ?? (!string.IsNullOrEmpty(_account.Login) ? $"https://github.com/{_account.Login}.png" : null);

    public string? ConnectedGistId => _account.GistId;

    public DateTime? LastSyncUtc => _account.LastSyncUtc;

    /// <summary>
    /// URL "raw" estática e reentrante do Gist de backup automático (<see cref="ProfileBackupSet"/>).
    /// Sem hash do commit — sempre aponta diretamente para a versão mais recente do arquivo no container:
    /// https://gist.githubusercontent.com/{usuario}/{GistID}/raw/profile.json
    /// Usada pelo app e pelo comando CLI /auto.
    /// </summary>
    public string? BackupRawUrl =>
        IsConnected && !string.IsNullOrEmpty(_account.GistId) && !string.IsNullOrEmpty(_account.Login)
            ? $"https://gist.githubusercontent.com/{_account.Login}/{_account.GistId}/raw/profile.json"
            : null;

    [SupportedOSPlatform("windows")]
    private static void SaveGistIdToRegistry(string? gistId)
    {
        try
        {
            using var key = Microsoft.Win32.Registry.CurrentUser.CreateSubKey(@"Software\WinProvision");
            if (string.IsNullOrEmpty(gistId))
            {
                key?.DeleteValue("GistId", throwOnMissingValue: false);
            }
            else
            {
                key?.SetValue("GistId", gistId, Microsoft.Win32.RegistryValueKind.String);
            }
        }
        catch
        {
            // Melhor esforço — permissão restrita ou ambiente sem registro
        }
    }

    [SupportedOSPlatform("windows")]
    private static string? LoadGistIdFromRegistry()
    {
        try
        {
            using var key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(@"Software\WinProvision");
            return key?.GetValue("GistId") as string;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>Carrega token (se existir e for descriptografável) e metadados salvos, sem chamar a rede.</summary>
    [SupportedOSPlatform("windows")]
    private void LoadPersistedState()
    {
        if (File.Exists(_accountInfoPath))
        {
            try
            {
                string json = File.ReadAllText(_accountInfoPath);
                _account = JsonSerializer.Deserialize<BackupAccountInfo>(json, WinProvisionJsonOptions.Compact) ?? new BackupAccountInfo();
            }
            catch (JsonException)
            {
                _account = new BackupAccountInfo();
            }
        }

        if (OperatingSystem.IsWindows() && string.IsNullOrEmpty(_account.GistId))
        {
            _account.GistId = LoadGistIdFromRegistry();
        }

        string? token = SecureTokenStore.TryLoad(_tokenPath);
        if (!string.IsNullOrEmpty(token) && !string.IsNullOrEmpty(_account.Login))
        {
            _http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        }
        else
        {
            // Token ilegível (ex.: outro usuário do Windows) ou login ausente — trata como desconectado.
            _account.Login = null;
        }
    }

    /// <summary>
    /// Valida o token colado pelo usuário, descobre o login e tenta localizar um backup
    /// pré-existente na conta (ver descrição de <see cref="GitHubBackupService"/>).
    /// Não faz upload nenhum aqui — só conecta. O primeiro <see cref="UploadProfileAsync"/>
    /// cria o Gist se ainda não existir um.
    /// </summary>
    [SupportedOSPlatform("windows")]
    public async Task<GitHubConnectResult> ConnectAsync(string token, CancellationToken ct = default)
    {
        token = token?.Trim() ?? string.Empty;
        if (string.IsNullOrEmpty(token))
            return GitHubConnectResult.Fail("Cole um Personal Access Token antes de conectar.");

        using var request = new HttpRequestMessage(HttpMethod.Get, $"{ApiBase}/user");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);

        HttpResponseMessage response;
        try
        {
            response = await _http.SendAsync(request, ct);
        }
        catch (HttpRequestException ex)
        {
            return GitHubConnectResult.Fail($"Não foi possível contatar o GitHub: {ex.Message}");
        }
        catch (TaskCanceledException)
        {
            return GitHubConnectResult.Fail("Tempo esgotado ao contatar o GitHub. Verifique sua conexão.");
        }

        if (response.StatusCode == HttpStatusCode.Unauthorized)
            return GitHubConnectResult.Fail("Token inválido ou expirado.");

        if (!response.IsSuccessStatusCode)
            return GitHubConnectResult.Fail($"GitHub retornou erro ao validar o token ({(int)response.StatusCode}).");

        var user = await response.Content.ReadFromJsonAsync<GitHubUserResponse>(cancellationToken: ct);
        if (user?.Login is null)
            return GitHubConnectResult.Fail("Não foi possível identificar o usuário do token.");

        // Tokens clássicos expõem os escopos concedidos neste header; tokens finos
        // (fine-grained) não expõem, então só bloqueamos quando o header EXISTE e
        // "gist" está claramente ausente — não damos falso-negativo pra fine-grained.
        if (response.Headers.TryGetValues("X-OAuth-Scopes", out var scopeValues))
        {
            string scopes = string.Join(",", scopeValues);
            if (!string.IsNullOrWhiteSpace(scopes) && !scopes.Contains("gist", StringComparison.OrdinalIgnoreCase))
                return GitHubConnectResult.Fail("Esse token não tem a permissão \"gist\". Gere um novo token com esse escopo marcado.");
        }

        bool accountChanged = !string.Equals(_account.Login, user.Login, StringComparison.OrdinalIgnoreCase);
        try
        {
            SecureTokenStore.Save(_tokenPath, token);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or CryptographicException)
        {
            return GitHubConnectResult.Fail($"Não foi possível salvar o token com segurança: {ex.Message}");
        }

        _http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        _account.Login = user.Login;
        _account.AvatarUrl = user.AvatarUrl;
        if (accountChanged)
        {
            _account.GistId = null;
            _account.BootstrapGistId = null;
        }
        string? previousGistId = accountChanged ? null : _account.GistId ?? LoadGistIdFromRegistry();
        if (accountChanged)
            SaveGistIdToRegistry(null);
        var gistLookup = await ResolveOwnedGistIdAsync(previousGistId, ct);
        _account.GistId = gistLookup.Id;
        PersistAccountInfo();

        return GitHubConnectResult.Ok(user.Login);
    }

    /// <summary>Desconecta localmente (apaga token + metadados desta máquina). O Gist na nuvem NÃO é apagado.</summary>
    public void Disconnect()
    {
        SecureTokenStore.Delete(_tokenPath);
        if (File.Exists(_accountInfoPath))
        {
            try { File.Delete(_accountInfoPath); } catch (IOException) { /* melhor esforço */ }
        }

        SaveGistIdToRegistry(null);
        _account = new BackupAccountInfo();
        _http.DefaultRequestHeaders.Authorization = null;
    }

    /// <summary>Cria ou atualiza o Gist secreto com TODAS as guias atuais (ver <see cref="ProfileBackupSet"/>). Retorna false em qualquer falha (sem lançar) — chamado de rotinas automáticas em segundo plano.</summary>
    public async Task<GitHubBackupUploadResult> UploadProfileAsync(ProfileBackupSet backupSet, CancellationToken ct = default)
    {
        if (!IsConnected)
            return GitHubBackupUploadResult.Fail("Conecte o GitHub antes de sincronizar o backup.");

        await _syncLock.WaitAsync(ct);
        try
        {
            string json = JsonSerializer.Serialize(backupSet, ManifestJsonOptions);
            var backupFiles = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                [BackupFileName] = json,
                [BuildDeviceBackupFileName()] = json
            };

            // Confirma o marcador WinProvision antes de modificar o Gist salvo.
            var lookup = await ResolveOwnedGistIdAsync(_account.GistId ?? LoadGistIdFromRegistry(), ct);
            if (lookup.Error is not null)
                return GitHubBackupUploadResult.Fail(lookup.Error);
            _account.GistId = lookup.Id;

            var upsert = await UpsertGistAsync(
                backupFiles, GistDescription, _account.GistId,
                onIdChanged: id =>
                {
                    _account.GistId = id;
                    SaveGistIdToRegistry(id);
                },
                onRecreateNeeded: () =>
                {
                    _account.GistId = null;
                    SaveGistIdToRegistry(null);
                },
                ct);

            if (!upsert.Success)
                return GitHubBackupUploadResult.Fail(upsert.Error ?? "O GitHub não confirmou o salvamento do backup.");

            _account.LastSyncUtc = DateTime.UtcNow;
            PersistAccountInfo();
            return GitHubBackupUploadResult.Ok();
        }
        catch (HttpRequestException ex)
        {
            return GitHubBackupUploadResult.Fail($"Falha de rede ao sincronizar o backup: {ex.Message}");
        }
        catch (TaskCanceledException ex)
        {
            return GitHubBackupUploadResult.Fail(ct.IsCancellationRequested ? "A sincronização foi cancelada." : $"Tempo esgotado ao sincronizar com o GitHub: {ex.Message}");
        }
        catch (JsonException ex)
        {
            return GitHubBackupUploadResult.Fail($"O GitHub retornou uma resposta inválida: {ex.Message}");
        }
        catch (IOException ex)
        {
            return GitHubBackupUploadResult.Fail($"Não foi possível salvar o estado do backup: {ex.Message}");
        }
        finally
        {
            _syncLock.Release();
        }
    }

    /// <summary>
    /// Publica o perfil e um script de instalação em um Gist secreto dedicado. O link raw do
    /// script permanece estável entre publicações e o comando do usuário precisa conter só esse link.
    /// </summary>
    public async Task<GitHubBootstrapPublishResult> PublishBootstrapAsync(
        string profileJson,
        BootstrapDisplayMode displayMode = BootstrapDisplayMode.UserInterface,
        BootstrapLogMode logMode = BootstrapLogMode.Local,
        string? logFilePath = null,
        CancellationToken ct = default)
    {
        if (!IsConnected)
            return GitHubBootstrapPublishResult.Fail("Conecte o GitHub em Conta e Sincronização antes de publicar o Bootstrap.");

        await _syncLock.WaitAsync(ct);
        try
        {
            var gistLookup = await FindBootstrapGistIdAsync(_account.BootstrapGistId, ct);
            string? gistId = gistLookup.Id;
            if (gistLookup.Error is not null)
                return GitHubBootstrapPublishResult.Fail(gistLookup.Error);

            if (gistId is null)
            {
                var createPayload = new
                {
                    description = BootstrapGistDescription,
                    @public = false,
                    files = new Dictionary<string, object>(StringComparer.Ordinal)
                    {
                        ["profile.json"] = new { content = profileJson }
                    }
                };

                using var createRequest = new HttpRequestMessage(HttpMethod.Post, $"{ApiBase}/gists")
                {
                    Content = JsonContent.Create(createPayload)
                };
                using var createResponse = await _http.SendAsync(createRequest, ct);
                if (!createResponse.IsSuccessStatusCode)
                    return GitHubBootstrapPublishResult.Fail(await FormatGitHubErrorAsync(createResponse, "criar o Gist", ct));

                var created = await createResponse.Content.ReadFromJsonAsync<GitHubGistResponse>(cancellationToken: ct);
                gistId = created?.Id;
                if (string.IsNullOrWhiteSpace(gistId))
                    return GitHubBootstrapPublishResult.Fail("O GitHub criou o Gist, mas não retornou seu identificador.");

                _account.BootstrapGistId = gistId;
                PersistAccountInfo();
            }

            string profileRawUrl = $"https://gist.githubusercontent.com/{_account.Login}/{gistId}/raw/profile.json";
            string rawUrl = $"https://gist.githubusercontent.com/{_account.Login}/{gistId}/raw/{BootstrapScriptFileName}";
            string bootstrapScript = BuildBootstrapScript(profileRawUrl, rawUrl, displayMode, logMode, logFilePath);
            var updatePayload = new
            {
                files = new Dictionary<string, object>(StringComparer.Ordinal)
                {
                    ["profile.json"] = new { content = profileJson },
                    [BootstrapScriptFileName] = new { content = bootstrapScript }
                }
            };

            using var updateRequest = new HttpRequestMessage(HttpMethod.Patch, $"{ApiBase}/gists/{Uri.EscapeDataString(gistId)}")
            {
                Content = JsonContent.Create(updatePayload)
            };
            using var updateResponse = await _http.SendAsync(updateRequest, ct);
            if (!updateResponse.IsSuccessStatusCode)
                return GitHubBootstrapPublishResult.Fail(await FormatGitHubErrorAsync(updateResponse, "atualizar os arquivos do Gist", ct));

            _account.BootstrapGistId = gistId;
            PersistAccountInfo();
            return GitHubBootstrapPublishResult.Ok(rawUrl);
        }
        catch (HttpRequestException ex)
        {
            return GitHubBootstrapPublishResult.Fail($"Falha de rede ao publicar no GitHub: {ex.Message}");
        }
        catch (TaskCanceledException)
        {
            return GitHubBootstrapPublishResult.Fail("O GitHub demorou demais para publicar o Bootstrap. Tente novamente.");
        }
        catch (JsonException ex)
        {
            return GitHubBootstrapPublishResult.Fail($"O GitHub retornou uma resposta inválida: {ex.Message}");
        }
        catch (IOException ex)
        {
            return GitHubBootstrapPublishResult.Fail($"Não foi possível salvar os dados da publicação: {ex.Message}");
        }
        finally
        {
            _syncLock.Release();
        }
    }

    private async Task<(string? Id, string? Error)> FindBootstrapGistIdAsync(string? candidateId, CancellationToken ct)
    {
        if (!string.IsNullOrWhiteSpace(candidateId))
        {
            using var response = await _http.GetAsync($"{ApiBase}/gists/{Uri.EscapeDataString(candidateId)}", ct);
            if (response.IsSuccessStatusCode)
            {
                var gist = await response.Content.ReadFromJsonAsync<GitHubGistResponse>(cancellationToken: ct);
                if (string.Equals(gist?.Description, BootstrapGistDescription, StringComparison.Ordinal))
                    return (gist!.Id, null);
            }
            else if (response.StatusCode != HttpStatusCode.NotFound)
            {
                return (null, await FormatGitHubErrorAsync(response, "consultar o Gist de Bootstrap", ct));
            }
        }

        for (int page = 1; page <= 10; page++)
        {
            using var response = await _http.GetAsync($"{ApiBase}/gists?per_page=100&page={page}", ct);
            if (!response.IsSuccessStatusCode)
                return (null, await FormatGitHubErrorAsync(response, "localizar o Gist de Bootstrap", ct));

            var gists = await response.Content.ReadFromJsonAsync<List<GitHubGistResponse>>(cancellationToken: ct);
            if (gists is null || gists.Count == 0)
                return (null, null);

            var match = gists.FirstOrDefault(g =>
                string.Equals(g.Description, BootstrapGistDescription, StringComparison.Ordinal)
                && g.Files?.ContainsKey(BootstrapScriptFileName) == true);
            if (match?.Id is not null)
                return (match.Id, null);
            if (gists.Count < 100)
                return (null, null);
        }

        return (null, null);
    }

    private static async Task<string> FormatGitHubErrorAsync(HttpResponseMessage response, string action, CancellationToken ct)
    {
        string body = await response.Content.ReadAsStringAsync(ct);
        string? message = null;
        try
        {
            using var document = JsonDocument.Parse(body);
            if (document.RootElement.TryGetProperty("message", out var messageElement))
                message = messageElement.GetString();
        }
        catch (JsonException)
        {
            // Usa o status HTTP abaixo quando o corpo não for JSON.
        }

        string detail = string.IsNullOrWhiteSpace(message) ? response.ReasonPhrase ?? "sem detalhes" : message;
        string hint = response.StatusCode switch
        {
            HttpStatusCode.Unauthorized => " Verifique se o token continua válido.",
            HttpStatusCode.Forbidden => " Verifique se o token tem o escopo gist e se o limite da API não foi atingido.",
            _ => string.Empty
        };
        return $"Não foi possível {action} (HTTP {(int)response.StatusCode}): {detail}.{hint}";
    }

    private static string BuildBootstrapScript(string profileRawUrl, string bootstrapRawUrl, BootstrapDisplayMode displayMode, BootstrapLogMode logMode, string? logFilePath) => $$"""
        #requires -Version 5.1
        $ErrorActionPreference = 'Stop'
        $ProgressPreference = 'SilentlyContinue'
        $ProfileUrl = '{{profileRawUrl.Replace("'", "''", StringComparison.Ordinal)}}'
        $BootstrapScriptUrl = '{{bootstrapRawUrl.Replace("'", "''", StringComparison.Ordinal)}}'
        $BootstrapCommand = "irm '$BootstrapScriptUrl' | iex"
        $DisplayMode = '{{(displayMode == BootstrapDisplayMode.Terminal ? "terminal" : "ui")}}'
        $LogMode = '{{(logMode == BootstrapLogMode.Cloud ? "cloud" : "local")}}'
        $SelectedLogPath = '{{(logFilePath ?? string.Empty).Replace("'", "''", StringComparison.Ordinal)}}'
        $SetupUrl = 'https://github.com/GabrielSilvaTI/WinProvision-Store/releases/latest/download/WinProvision.Store-Setup.exe'
        $ChecksumUrl = $SetupUrl + '.sha256'
        $WorkDirectory = Join-Path $env:TEMP ('WinProvisionBootstrap-' + [guid]::NewGuid().ToString('N'))
        $SetupPath = Join-Path $WorkDirectory 'WinProvision.Store-Setup.exe'
        $ChecksumPath = $SetupPath + '.sha256'
        $ProfilePath = Join-Path $WorkDirectory 'profile.json'

        function Remove-BootstrapRetryTask {
            try {
                $Scheduler = New-Object -ComObject Schedule.Service
                $Scheduler.Connect()
                $Scheduler.GetFolder('\').DeleteTask('WinProvisionBootstrapNetworkRetry', 0)
            }
            catch { }
        }

        function Register-BootstrapRetryTask {
            $Scheduler = New-Object -ComObject Schedule.Service
            $Scheduler.Connect()
            $Root = $Scheduler.GetFolder('\')
            $Task = $Scheduler.NewTask(0)
            $Task.RegistrationInfo.Description = 'Retoma a instalação do WinProvision quando houver conexão de rede.'
            $Task.Principal.UserId = [Security.Principal.WindowsIdentity]::GetCurrent().Name
            $Task.Principal.LogonType = 3
            $Task.Principal.RunLevel = 0

            $LogonTrigger = $Task.Triggers.Create(9)
            $LogonTrigger.Enabled = $true
            $NetworkTrigger = $Task.Triggers.Create(0)
            $NetworkTrigger.Enabled = $true
            $NetworkTrigger.Subscription = '<QueryList><Query Id="0" Path="Microsoft-Windows-NetworkProfile/Operational"><Select Path="Microsoft-Windows-NetworkProfile/Operational">*[System[(EventID=10000)]]</Select></Query></QueryList>'

            $Action = $Task.Actions.Create(0)
            $Action.Path = Join-Path $env:SystemRoot 'System32\WindowsPowerShell\v1.0\powershell.exe'
            $Action.Arguments = '-NoProfile -ExecutionPolicy Bypass -WindowStyle Hidden -Command "' + $BootstrapCommand + '"'

            $Task.Settings.Enabled = $true
            $Task.Settings.StartWhenAvailable = $true
            $Task.Settings.MultipleInstances = 2
            $Task.Settings.DisallowStartIfOnBatteries = $false
            $Task.Settings.StopIfGoingOnBatteries = $false

            $Root.RegisterTaskDefinition('WinProvisionBootstrapNetworkRetry', $Task, 6, $Task.Principal.UserId, $null, 3, $null) | Out-Null
        }

        function Invoke-DownloadWithRetry([string]$Url, [string]$Destination, [string]$Label) {
            for ($Attempt = 1; $Attempt -le 10; $Attempt++) {
                try {
                    Invoke-WebRequest -Uri $Url -OutFile $Destination -UseBasicParsing
                    return $true
                }
                catch {
                    $StatusCode = $null
                    try { $StatusCode = [int]$_.Exception.Response.StatusCode } catch { }
                    if ($StatusCode -ge 400 -and $StatusCode -lt 500 -and $StatusCode -notin @(408, 429)) {
                        throw
                    }

                    $NetworkError = $false
                    $CurrentException = $_.Exception
                    while ($null -ne $CurrentException) {
                        if ($CurrentException -is [System.Net.WebException] -or
                            $CurrentException -is [System.Net.Http.HttpRequestException] -or
                            $CurrentException -is [System.TimeoutException]) {
                            $NetworkError = $true
                            break
                        }
                        $CurrentException = $CurrentException.InnerException
                    }
                    if (-not $NetworkError) { throw }

                    Write-Host "Falha de rede ao baixar $Label (tentativa $Attempt de 10)."
                    if ($Attempt -lt 10) { Start-Sleep -Seconds 3 }
                }
            }
            return $false
        }

        New-Item -ItemType Directory -Path $WorkDirectory -Force | Out-Null
        try {
            Remove-BootstrapRetryTask
            Write-Host 'Baixando o perfil e o instalador...'
            if (-not (Invoke-DownloadWithRetry $ProfileUrl $ProfilePath 'o perfil')) {
                try { Register-BootstrapRetryTask; Write-Host 'Sem conexão após 10 tentativas. O Bootstrap será retomado quando a rede estiver disponível.' }
                catch { Write-Host "Não foi possível agendar a retomada automática: $($_.Exception.Message)" }
                return
            }
            if (-not (Invoke-DownloadWithRetry $SetupUrl $SetupPath 'o instalador')) {
                try { Register-BootstrapRetryTask; Write-Host 'Sem conexão após 10 tentativas. O Bootstrap será retomado quando a rede estiver disponível.' }
                catch { Write-Host "Não foi possível agendar a retomada automática: $($_.Exception.Message)" }
                return
            }
            if (-not (Invoke-DownloadWithRetry $ChecksumUrl $ChecksumPath 'o checksum')) {
                try { Register-BootstrapRetryTask; Write-Host 'Sem conexão após 10 tentativas. O Bootstrap será retomado quando a rede estiver disponível.' }
                catch { Write-Host "Não foi possível agendar a retomada automática: $($_.Exception.Message)" }
                return
            }

            $ChecksumText = Get-Content -LiteralPath $ChecksumPath -Raw
            if ($ChecksumText -notmatch '(?im)^\s*([0-9a-f]{64})\b') {
                throw 'O arquivo de checksum da Release está inválido.'
            }
            $ExpectedHash = $Matches[1].ToLowerInvariant()
            $ActualHash = (Get-FileHash -LiteralPath $SetupPath -Algorithm SHA256).Hash.ToLowerInvariant()
            if ($ActualHash -ne $ExpectedHash) {
                throw 'A verificação SHA-256 do instalador falhou.'
            }

            Write-Host 'Instalando WinProvision Store para o usuário atual...'
            $SetupProcess = Start-Process -FilePath $SetupPath `
                -ArgumentList @('/VERYSILENT', '/SUPPRESSMSGBOXES', '/NORESTART', '/CURRENTUSER') `
                -Wait -PassThru -WindowStyle Hidden
            if ($SetupProcess.ExitCode -ne 0) {
                throw "O instalador terminou com código $($SetupProcess.ExitCode)."
            }

            $UninstallKey = 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Uninstall\{57E31A2E-BBE2-48F4-8902-CB249FE1D38C}_is1'
            $InstallInfo = Get-ItemProperty -LiteralPath $UninstallKey -ErrorAction SilentlyContinue
            $InstallDirectory = $InstallInfo.InstallLocation
            if ([string]::IsNullOrWhiteSpace($InstallDirectory)) {
                $InstallDirectory = Join-Path $env:LOCALAPPDATA 'Programs\WinProvision Store'
            }
            $AppPath = Join-Path $InstallDirectory 'WinProvision.Store.exe'
            if (-not (Test-Path -LiteralPath $AppPath -PathType Leaf)) {
                throw "Não encontrei o app instalado em '$AppPath'."
            }

            if ([string]::IsNullOrWhiteSpace($SelectedLogPath)) {
                $LogDirectory = Join-Path $env:LOCALAPPDATA 'WinProvisionStore\Logs'
                $LogPath = Join-Path $LogDirectory ('orchestrator-' + (Get-Date -Format 'yyyyMMdd-HHmmss') + '.txt')
            }
            else {
                $LogPath = $SelectedLogPath
                $LogDirectory = Split-Path -Parent $LogPath
            }
            if (-not [string]::IsNullOrWhiteSpace($LogDirectory)) {
                New-Item -ItemType Directory -Path $LogDirectory -Force | Out-Null
            }

            $AppArguments = @('/auto', ('"{0}"' -f $ProfilePath), '/log', ('"{0}"' -f $LogPath), '/keepconsole')
            if ($LogMode -eq 'cloud') { $AppArguments += '/cloudlog' }

            if ($DisplayMode -eq 'terminal') {
                Write-Host "Aplicando o perfil no Terminal. Log local: $LogPath"
                $AppArguments += '/silent'
                $AppProcess = Start-Process -FilePath $AppPath `
                    -ArgumentList $AppArguments `
                    -Wait -PassThru -NoNewWindow
            }
            else {
                Write-Host 'Aplicando o perfil com a interface do WinProvision...'
                $AppProcess = Start-Process -FilePath $AppPath `
                    -ArgumentList $AppArguments `
                    -Wait -PassThru -WindowStyle Normal
            }
            if ($AppProcess.ExitCode -ne 0) {
                throw "O modo /auto terminou com código $($AppProcess.ExitCode)."
            }
            Write-Host 'Bootstrap concluído com sucesso.'
        }
        finally {
            Remove-Item -LiteralPath $ProfilePath -Force -ErrorAction SilentlyContinue
            Remove-Item -LiteralPath $SetupPath -Force -ErrorAction SilentlyContinue
            Remove-Item -LiteralPath $ChecksumPath -Force -ErrorAction SilentlyContinue
            Remove-Item -LiteralPath $WorkDirectory -Force -ErrorAction SilentlyContinue
        }
        """;

    /// <summary>Baixa o backup (todas as guias) salvo no Gist da conta conectada. Retorna null se não houver backup ou em caso de falha.</summary>
    public async Task<GitHubBackupDownloadResult> DownloadProfileAsync(CancellationToken ct = default)
    {
        if (!IsConnected)
            return GitHubBackupDownloadResult.Fail("Conecte o GitHub antes de baixar o backup.");

        try
        {
            // Perfil pode ter sido criado por outra instalação do app (outra máquina) que
            // nunca sincronizou por aqui — sempre reconfirma o GistId em vez de confiar só
            // no cache local, que pode estar vazio ou desatualizado.
            var lookup = await ResolveOwnedGistIdAsync(_account.GistId ?? LoadGistIdFromRegistry(), ct);
            if (lookup.Error is not null)
                return GitHubBackupDownloadResult.Fail(lookup.Error);
            _account.GistId = lookup.Id;
            if (string.IsNullOrEmpty(_account.GistId))
                return GitHubBackupDownloadResult.NotFound();

            using var response = await SendWithTransientRetryAsync(
                () => new HttpRequestMessage(HttpMethod.Get, $"{ApiBase}/gists/{Uri.EscapeDataString(_account.GistId)}"), ct);
            if (!response.IsSuccessStatusCode)
                return GitHubBackupDownloadResult.Fail(await FormatGitHubErrorAsync(response, "baixar o backup", ct));

            var gist = await response.Content.ReadFromJsonAsync<GitHubGistResponse>(cancellationToken: ct);
            if (!IsWinProvisionGist(gist))
                return GitHubBackupDownloadResult.Fail("O Gist encontrado não corresponde ao backup do WinProvision Store.");
            var backupFile = gist?.Files?.GetValueOrDefault(BackupFileName)
                ?? gist?.Files?.GetValueOrDefault(LegacyBackupFileName);
            string? content = backupFile?.Content;
            if ((backupFile?.Truncated == true || string.IsNullOrEmpty(content))
                && !string.IsNullOrWhiteSpace(backupFile?.RawUrl))
            {
                using var rawResponse = await SendWithTransientRetryAsync(
                    () => new HttpRequestMessage(HttpMethod.Get, backupFile.RawUrl), ct);
                if (!rawResponse.IsSuccessStatusCode)
                    return GitHubBackupDownloadResult.Fail(await FormatGitHubErrorAsync(rawResponse, "baixar o arquivo completo do backup", ct));
                content = await rawResponse.Content.ReadAsStringAsync(ct);
            }
            if (string.IsNullOrEmpty(content))
                return GitHubBackupDownloadResult.NotFound();

            PersistAccountInfo();
            var backup = JsonSerializer.Deserialize<ProfileBackupSet>(content, ManifestJsonOptions);
            return backup is null
                ? GitHubBackupDownloadResult.Fail("O arquivo do backup está vazio ou em formato inválido.")
                : GitHubBackupDownloadResult.Ok(backup);
        }
        catch (HttpRequestException ex)
        {
            return GitHubBackupDownloadResult.Fail($"Falha de rede ao baixar o backup: {ex.Message}");
        }
        catch (TaskCanceledException ex)
        {
            return GitHubBackupDownloadResult.Fail(ct.IsCancellationRequested ? "O download foi cancelado." : $"Tempo esgotado ao baixar o backup: {ex.Message}");
        }
        catch (JsonException ex)
        {
            return GitHubBackupDownloadResult.Fail($"O arquivo do backup contém JSON inválido: {ex.Message}");
        }
    }

    // Helper genérico para localizar e atualizar o Gist de backup do perfil.

    /// <summary>Varre os Gists da conta procurando exclusivamente a descrição própria do WinProvision.</summary>
    private async Task<(string? Id, string? Error)> TryFindGistIdAsync(string description, string fileName, CancellationToken ct)
    {
        try
        {
            for (int page = 1; page <= 10; page++)
            {
                using var response = await SendWithTransientRetryAsync(
                    () => new HttpRequestMessage(HttpMethod.Get, $"{ApiBase}/gists?per_page=100&page={page}"), ct);
                if (!response.IsSuccessStatusCode)
                    return (null, await FormatGitHubErrorAsync(response, "localizar o Gist de backup", ct));

                var gists = await response.Content.ReadFromJsonAsync<List<GitHubGistResponse>>(cancellationToken: ct);
                if (gists is null || gists.Count == 0)
                    return (null, null);

                var match = gists.FirstOrDefault(g =>
                    string.Equals(g.Description, description, StringComparison.Ordinal)
                    && g.Files?.ContainsKey(fileName) == true);

                if (match?.Id is not null)
                    return (match.Id, null);

                if (gists.Count < 100)
                    return (null, null);
            }

            return (null, null);
        }
        catch (HttpRequestException ex)
        {
            return (null, $"Falha de rede ao localizar o Gist de backup: {ex.Message}");
        }
        catch (JsonException ex)
        {
            return (null, $"O GitHub retornou uma lista de Gists inválida: {ex.Message}");
        }
    }

    private async Task<(string? Id, string? Error)> ResolveOwnedGistIdAsync(string? candidateId, CancellationToken ct)
    {
        if (!string.IsNullOrWhiteSpace(candidateId))
        {
            try
            {
                using var response = await SendWithTransientRetryAsync(
                    () => new HttpRequestMessage(HttpMethod.Get, $"{ApiBase}/gists/{Uri.EscapeDataString(candidateId)}"), ct);
                if (response.IsSuccessStatusCode)
                {
                    var gist = await response.Content.ReadFromJsonAsync<GitHubGistResponse>(cancellationToken: ct);
                    if (IsWinProvisionGist(gist))
                        return (gist!.Id, null);
                }
                else if (response.StatusCode != HttpStatusCode.NotFound)
                {
                    return (null, await FormatGitHubErrorAsync(response, "validar o Gist de backup", ct));
                }
            }
            catch (HttpRequestException ex)
            {
                return (null, $"Falha de rede ao validar o Gist de backup: {ex.Message}");
            }
            catch (JsonException ex)
            {
                return (null, $"O GitHub retornou dados inválidos ao validar o Gist: {ex.Message}");
            }
        }

        return await TryFindGistIdAsync(GistDescription, BackupFileName, ct);
    }

    private static bool IsWinProvisionGist(GitHubGistResponse? gist) =>
        gist is not null
        && string.Equals(gist.Description, GistDescription, StringComparison.Ordinal);

    private static string BuildDeviceBackupFileName()
    {
        string identity = $"{Environment.MachineName}\\{Environment.UserName}";
        byte[] hash = SHA256.HashData(Encoding.UTF8.GetBytes(identity));
        string suffix = Convert.ToHexString(hash.AsSpan(0, 8)).ToLowerInvariant();
        return $"profile-device-{suffix}.json";
    }

    private async Task<(string? Id, string? Error)> CreateGistAsync(
        IReadOnlyDictionary<string, string> files, string description, CancellationToken ct)
    {
        var payload = new
        {
            description,
            @public = false,
            files = files.ToDictionary(file => file.Key, file => (object)new { content = file.Value }, StringComparer.Ordinal)
        };

        using var response = await _http.PostAsJsonAsync($"{ApiBase}/gists", payload, ct);
        if (!response.IsSuccessStatusCode)
            return (null, await FormatGitHubErrorAsync(response, "criar o Gist de backup", ct));

        var created = await response.Content.ReadFromJsonAsync<GitHubGistResponse>(cancellationToken: ct);
        return created?.Id is { Length: > 0 } id
            ? (id, null)
            : (null, "O GitHub aceitou a criação, mas não retornou o identificador do Gist.");
    }

    /// <summary>
    /// Cria (se <paramref name="existingGistId"/> for nulo/vazio) ou atualiza o Gist com o
    /// conjunto de arquivos dado. Em caso de 404 no update (Gist apagado/perdeu acesso desde a última
    /// vez), reencontra por descrição/nome de arquivo e tenta de novo antes de desistir.
    /// Único chamador hoje é <see cref="UploadProfileAsync"/>.
    /// </summary>
    private async Task<(bool Success, string? Error)> UpsertGistAsync(
        IReadOnlyDictionary<string, string> files, string description, string? existingGistId,
        Action<string?> onIdChanged, Action onRecreateNeeded, CancellationToken ct)
    {
        try
        {
            if (string.IsNullOrEmpty(existingGistId))
            {
                var created = await CreateGistAsync(files, description, ct);
                if (created.Error is not null || created.Id is null)
                    return (false, created.Error ?? "O Gist não foi criado.");

                onIdChanged(created.Id);
                return (true, null);
            }

            var payload = new
            {
                files = files.ToDictionary(file => file.Key, file => (object)new { content = file.Value }, StringComparer.Ordinal)
            };
            using var response = await SendWithTransientRetryAsync(
                () => new HttpRequestMessage(HttpMethod.Patch, $"{ApiBase}/gists/{Uri.EscapeDataString(existingGistId)}")
                {
                    Content = JsonContent.Create(payload)
                }, ct);
            if (response.StatusCode != HttpStatusCode.NotFound)
                return response.IsSuccessStatusCode
                    ? (true, null)
                    : (false, await FormatGitHubErrorAsync(response, "atualizar o backup", ct));

            // Gist foi apagado/perdeu acesso desde a última vez — reencontra por
            // descrição/nome de arquivo antes de desistir, em vez de falhar direto.
            onRecreateNeeded();
            var rechecked = await TryFindGistIdAsync(description, BackupFileName, ct);
            if (rechecked.Error is not null)
                return (false, rechecked.Error);
            onIdChanged(rechecked.Id);
            if (rechecked.Id is null)
            {
                var created = await CreateGistAsync(files, description, ct);
                if (created.Error is not null || created.Id is null)
                    return (false, created.Error ?? "O Gist não foi recriado.");
                onIdChanged(created.Id);
                return (true, null);
            }

            // Reenvia uma única vez ao Gist reencontrado; não recursa indefinidamente.
            var retryPayload = new
            {
                files = files.ToDictionary(file => file.Key, file => (object)new { content = file.Value }, StringComparer.Ordinal)
            };
            using var retryResponse = await SendWithTransientRetryAsync(
                () => new HttpRequestMessage(HttpMethod.Patch, $"{ApiBase}/gists/{Uri.EscapeDataString(rechecked.Id)}")
                {
                    Content = JsonContent.Create(retryPayload)
                }, ct);
            return retryResponse.IsSuccessStatusCode
                ? (true, null)
                : (false, await FormatGitHubErrorAsync(retryResponse, "atualizar o backup reencontrado", ct));
        }
        catch (HttpRequestException ex)
        {
            return (false, $"Falha de rede ao salvar o backup: {ex.Message}");
        }
        catch (JsonException ex)
        {
            return (false, $"Não foi possível preparar os dados do backup: {ex.Message}");
        }
    }

    private async Task<HttpResponseMessage> SendWithTransientRetryAsync(
        Func<HttpRequestMessage> createRequest, CancellationToken ct)
    {
        for (int attempt = 0; ; attempt++)
        {
            using var request = createRequest();
            try
            {
                var response = await _http.SendAsync(request, ct);
                bool transient = response.StatusCode == (HttpStatusCode)429 || (int)response.StatusCode >= 500;
                if (!transient || attempt >= MaxTransientRetries)
                    return response;

                TimeSpan delay = response.Headers.RetryAfter?.Delta is { } retryAfter
                    ? TimeSpan.FromSeconds(Math.Clamp(retryAfter.TotalSeconds, 1, 30))
                    : RetryDelay * (attempt + 1);
                response.Dispose();
                await Task.Delay(delay, ct);
            }
            catch (HttpRequestException) when (attempt < MaxTransientRetries)
            {
                await Task.Delay(RetryDelay * (attempt + 1), ct);
            }
        }
    }

    [SupportedOSPlatform("windows")]
    private void PersistAccountInfo()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_accountInfoPath)!);
        // Usa as mesmas opções de JSON do resto do app e UTF-8 sem BOM — evita inconsistências
        // se mais tarde alguém adicionar enums ou campos com nomes case-sensitive neste model.
        string json = JsonSerializer.Serialize(_account, WinProvisionJsonOptions.Compact);
        byte[] bytes = System.Text.Encoding.UTF8.GetBytes(json);
        using var stream = new FileStream(_accountInfoPath, FileMode.Create, FileAccess.Write, FileShare.Read);
        stream.Write(bytes, 0, bytes.Length);
        stream.Flush();

        if (OperatingSystem.IsWindows() && !string.IsNullOrEmpty(_account.GistId))
        {
            SaveGistIdToRegistry(_account.GistId);
        }
    }

    private class GitHubUserResponse
    {
        [JsonPropertyName("login")]
        public string? Login { get; set; }

        [JsonPropertyName("avatar_url")]
        public string? AvatarUrl { get; set; }
    }

    private class GitHubGistResponse
    {
        [JsonPropertyName("id")]
        public string? Id { get; set; }

        [JsonPropertyName("description")]
        public string? Description { get; set; }

        [JsonPropertyName("files")]
        public Dictionary<string, GitHubGistFile>? Files { get; set; }
    }

    private class GitHubGistFile
    {
        [JsonPropertyName("content")]
        public string? Content { get; set; }

        [JsonPropertyName("raw_url")]
        public string? RawUrl { get; set; }

        [JsonPropertyName("truncated")]
        public bool Truncated { get; set; }
    }
}
