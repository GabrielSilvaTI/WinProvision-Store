using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Runtime.Versioning;
using System.Text.Json;
using WinProvision.Core.Models;
using WinProvision.Core.Services;

namespace WinProvision.Core.Services.Backup;

/// <summary>
/// Sincroniza o conjunto de perfil com o Cloudflare Worker pessoal.
/// O Worker mantém uma única versão do JSON e usa ETag/If-Match para impedir
/// que uma máquina sobrescreva uma atualização feita por outra.
/// </summary>
public sealed class CloudBackupService
{
    private const string EndpointEnvironmentVariable = "WINPROVISION_BACKUP_ENDPOINT";
    // Replace once with the deployed production Worker URL. Users should never
    // need to configure infrastructure details in the application.
    private const string DefaultEndpointUrl = "https://winprovision-backup.workers.dev";
    private static readonly JsonSerializerOptions JsonOptions = WinProvisionJsonOptions.Profile;

    private readonly HttpClient _http;
    private readonly string _accountInfoPath;
    private readonly string _apiKeyPath;
    private readonly string _endpointPath;
    private readonly SemaphoreSlim _syncLock = new(1, 1);
    private CloudBackupAccountInfo _account = new();
    private string? _apiKey;

    public CloudBackupService() : this(DefaultBackupDir())
    {
    }

    internal CloudBackupService(string backupDir)
    {
        _accountInfoPath = Path.Combine(backupDir, "cloud-account.json");
        _apiKeyPath = Path.Combine(backupDir, "cloud-api-key.dat");
        _endpointPath = Path.Combine(backupDir, "cloud-endpoint.dat");

        _http = new HttpClient { Timeout = TimeSpan.FromSeconds(15) };
        _http.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("WinProvision-Store", "1.0"));

        LoadPersistedState();
    }

    public bool IsConnected => !string.IsNullOrWhiteSpace(_apiKey);

    public string? ConnectedLogin => IsConnected ? new Uri(_account.Endpoint).Host : null;

    public string Endpoint => _account.Endpoint;

    public DateTime? LastSyncUtc => _account.LastSyncUtc;

    /// <summary>
    /// URL usada pelo modo /auto. O Worker aceita a API key na query string para
    /// permitir que o comando leia o perfil sem headers customizados.
    /// </summary>
    public string? BackupRawUrl =>
        IsConnected && _account.Version > 0
            ? $"{ProfileEndpoint}?key={Uri.EscapeDataString(_apiKey!)}"
            : null;

    private string ProfileEndpoint => $"{_account.Endpoint.TrimEnd('/')}/profile";

    private static string DefaultBackupDir() => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "WinProvision", "Backup");

    private static string DefaultEndpoint() =>
        Environment.GetEnvironmentVariable(EndpointEnvironmentVariable)?.Trim() is { Length: > 0 } configured
            ? configured.TrimEnd('/')
            : DefaultEndpointUrl;

    [SupportedOSPlatform("windows")]
    private void LoadPersistedState()
    {
        _account.Endpoint = SecureTokenStore.TryLoad(_endpointPath) ?? DefaultEndpoint();

        if (File.Exists(_accountInfoPath))
        {
            try
            {
                string json = File.ReadAllText(_accountInfoPath);
                _account = JsonSerializer.Deserialize<CloudBackupAccountInfo>(json, WinProvisionJsonOptions.Compact)
                    ?? new CloudBackupAccountInfo { Endpoint = DefaultEndpoint() };
            }
            catch (JsonException)
            {
                _account = new CloudBackupAccountInfo { Endpoint = DefaultEndpoint() };
            }
        }

        _account.Endpoint = SecureTokenStore.TryLoad(_endpointPath)
            ?? (string.IsNullOrWhiteSpace(_account.Endpoint) ? DefaultEndpoint() : _account.Endpoint.TrimEnd('/'));
        _apiKey = SecureTokenStore.TryLoad(_apiKeyPath);
        if (string.IsNullOrWhiteSpace(_apiKey))
            _apiKey = null;
    }

    [SupportedOSPlatform("windows")]
    public async Task<CloudConnectResult> ConnectAsync(string apiKey, CancellationToken ct = default)
        => await ConnectAsync(DefaultEndpoint(), apiKey, ct);

    [SupportedOSPlatform("windows")]
    public async Task<CloudConnectResult> ConnectAsync(string endpoint, string apiKey, CancellationToken ct = default)
    {
        endpoint = endpoint?.Trim().TrimEnd('/') ?? string.Empty;
        apiKey = apiKey?.Trim() ?? string.Empty;
        if (apiKey.Length == 0)
            return CloudConnectResult.Fail("Informe a API Key do Cloudflare Worker antes de conectar.");

        if (!Uri.TryCreate(endpoint, UriKind.Absolute, out var endpointUri)
            || endpointUri.Scheme != Uri.UriSchemeHttps)
        {
            return CloudConnectResult.Fail("O endpoint de backup precisa usar HTTPS para proteger a API Key.");
        }

        using var request = new HttpRequestMessage(HttpMethod.Get, $"{endpoint.TrimEnd('/')}/profile");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);

        try
        {
            using HttpResponseMessage response = await _http.SendAsync(request, ct);
            if (response.StatusCode == HttpStatusCode.Unauthorized)
                return CloudConnectResult.Fail("API Key inválida ou rejeitada pelo Worker.");

            if (response.StatusCode is not (HttpStatusCode.OK or HttpStatusCode.NotFound))
                return CloudConnectResult.Fail($"Worker retornou erro ao validar a API Key ({(int)response.StatusCode}).");

            _account.Endpoint = endpoint.TrimEnd('/');
            _account.Version = response.StatusCode == HttpStatusCode.OK
                ? ParseVersion(response.Headers.ETag?.Tag)
                : 0;
            _apiKey = apiKey;
            SecureTokenStore.Save(_apiKeyPath, apiKey);
            SecureTokenStore.Save(_endpointPath, _account.Endpoint);
            PersistAccountInfo();

            return CloudConnectResult.Ok(new Uri(_account.Endpoint).Host);
        }
        catch (HttpRequestException ex)
        {
            return CloudConnectResult.Fail($"Não foi possível contatar o Worker: {ex.Message}");
        }
        catch (TaskCanceledException)
        {
            return CloudConnectResult.Fail("Tempo esgotado ao contatar o Worker. Verifique sua conexão.");
        }
    }

    public void Disconnect()
    {
        SecureTokenStore.Delete(_apiKeyPath);
        SecureTokenStore.Delete(_endpointPath);
        try
        {
            if (File.Exists(_accountInfoPath))
                File.Delete(_accountInfoPath);
        }
        catch (IOException)
        {
            // A desconexão continua válida mesmo se o metadado local não puder ser removido.
        }

        _apiKey = null;
        _account = new CloudBackupAccountInfo { Endpoint = DefaultEndpoint() };
    }

    public async Task<bool> UploadProfileAsync(ProfileBackupSet backupSet, CancellationToken ct = default)
    {
        if (!IsConnected)
            return false;

        await _syncLock.WaitAsync(ct);
        try
        {
            int currentVersion = await GetCurrentVersionAsync(ct);
            string json = JsonSerializer.Serialize(backupSet, JsonOptions);

            using var request = new HttpRequestMessage(HttpMethod.Put, ProfileEndpoint)
            {
                Content = new StringContent(json, System.Text.Encoding.UTF8, "application/json")
            };
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _apiKey);
            request.Headers.TryAddWithoutValidation("If-Match", currentVersion.ToString());

            using HttpResponseMessage response = await _http.SendAsync(request, ct);
            if (response.StatusCode == HttpStatusCode.Conflict)
            {
                _account.Version = await ReadConflictVersionAsync(response, ct);
                PersistAccountInfo();
                return false;
            }

            if (!response.IsSuccessStatusCode)
                return false;

            var result = await response.Content.ReadFromJsonAsync<CloudPutResponse>(cancellationToken: ct);
            _account.Version = result?.Version ?? currentVersion + 1;
            _account.LastSyncUtc = DateTime.UtcNow;
            PersistAccountInfo();
            return true;
        }
        catch (HttpRequestException)
        {
            return false;
        }
        catch (JsonException)
        {
            return false;
        }
        finally
        {
            _syncLock.Release();
        }
    }

    public async Task<ProfileBackupSet?> DownloadProfileAsync(CancellationToken ct = default)
    {
        if (!IsConnected)
            return null;

        try
        {
            using var request = CreateAuthorizedRequest(HttpMethod.Get);
            using HttpResponseMessage response = await _http.SendAsync(request, ct);
            if (response.StatusCode == HttpStatusCode.NotFound)
                return null;
            if (!response.IsSuccessStatusCode)
                return null;

            string json = await response.Content.ReadAsStringAsync(ct);
            var profile = JsonSerializer.Deserialize<ProfileBackupSet>(json, JsonOptions);
            _account.Version = ParseVersion(response.Headers.ETag?.Tag);
            PersistAccountInfo();
            return profile;
        }
        catch (HttpRequestException)
        {
            return null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private async Task<int> GetCurrentVersionAsync(CancellationToken ct)
    {
        using var request = CreateAuthorizedRequest(HttpMethod.Get);
        using HttpResponseMessage response = await _http.SendAsync(request, ct);
        if (response.StatusCode == HttpStatusCode.NotFound)
        {
            _account.Version = 0;
            return 0;
        }

        response.EnsureSuccessStatusCode();
        _account.Version = ParseVersion(response.Headers.ETag?.Tag);
        return _account.Version;
    }

    private static async Task<int> ReadConflictVersionAsync(HttpResponseMessage response, CancellationToken ct)
    {
        try
        {
            var conflict = await response.Content.ReadFromJsonAsync<CloudConflictResponse>(cancellationToken: ct);
            return conflict?.CurrentVersion ?? 0;
        }
        catch (JsonException)
        {
            return 0;
        }
    }

    private HttpRequestMessage CreateAuthorizedRequest(HttpMethod method) =>
        new(method, ProfileEndpoint)
        {
            Headers = { Authorization = new AuthenticationHeaderValue("Bearer", _apiKey) }
        };

    private static int ParseVersion(string? value) =>
        int.TryParse(value?.Trim('"'), out int version) && version >= 0 ? version : 0;

    [SupportedOSPlatform("windows")]
    private void PersistAccountInfo()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_accountInfoPath)!);
        string json = JsonSerializer.Serialize(_account, WinProvisionJsonOptions.Compact);
        File.WriteAllText(_accountInfoPath, json);
    }

    private sealed class CloudPutResponse
    {
        public int Version { get; set; }
    }

    private sealed class CloudConflictResponse
    {
        public int CurrentVersion { get; set; }
    }
}

public record CloudConnectResult(bool Success, string? ErrorMessage = null, string? Login = null)
{
    public static CloudConnectResult Ok(string login) => new(true, null, login);
    public static CloudConnectResult Fail(string message) => new(false, message);
}

internal sealed class CloudBackupAccountInfo
{
    public string Endpoint { get; set; } = "https://winprovision-backup.workers.dev";
    public int Version { get; set; }
    public DateTime? LastSyncUtc { get; set; }
}
