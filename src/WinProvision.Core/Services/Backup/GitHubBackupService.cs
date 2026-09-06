using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Runtime.Versioning;
using System.Text.Json;
using System.Text.Json.Serialization;
using WinProvision.Core.Models;
using WinProvision.Core.Services;

namespace WinProvision.Core.Services.Backup;

/// <summary>
/// Backup em nuvem de TODAS as guias de pacotes (ver <see cref="ProfileBackupSet"/>),
/// num Gist SECRETO do GitHub do próprio usuário — mesma ideia do UnigetUI, mas via
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
    private const string BackupFileName = "winprovision-profile.json";
    private const string GistDescription = "WinProvision Store — backup automático de perfil (não editar manualmente)";

    // Mesmas opções usadas em todo o resto do app (ProfileService/ProvisioningService) — um
    // arquivo salvo por um lado sempre bate com o que o outro espera ao ler de volta.
    private static readonly JsonSerializerOptions ManifestJsonOptions = WinProvisionJsonOptions.Default;

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

        _http = new HttpClient { Timeout = TimeSpan.FromSeconds(15) };
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

    public DateTime? LastSyncUtc => _account.LastSyncUtc;

    /// <summary>
    /// URL "raw" pública do Gist de backup automático — o mesmo arquivo (<see cref="ProfileBackupSet"/>)
    /// que <see cref="UploadProfileAsync"/> mantém atualizado quase em tempo real a cada
    /// instalação/remoção ou ajuste de provisionamento. Não exige token pra ler: mesmo o Gist
    /// sendo criado como "secreto" (não listado no perfil público da conta), quem tiver o link
    /// exato consegue baixar o conteúdo — é assim que o GitHub trata Gists secretos. Null se a
    /// conta não estiver conectada ou se nenhum Gist tiver sido criado ainda nesta conta
    /// (primeiro <see cref="UploadProfileAsync"/> bem-sucedido ainda não rodou). Usado pelo botão
    /// "Sincronizar" do gerador de comando CLI (tela Provisionamento) — desde que
    /// <see cref="ProfileManifestParser"/> trate esse formato como equivalente a um perfil único,
    /// esta URL serve tanto para <c>/auto</c> quanto para <c>/Provision</c>.
    /// </summary>
    public string? BackupRawUrl =>
        IsConnected && !string.IsNullOrEmpty(_account.GistId) && !string.IsNullOrEmpty(_account.Login)
            ? $"https://gist.githubusercontent.com/{_account.Login}/{_account.GistId}/raw/{BackupFileName}"
            : null;

    /// <summary>Carrega token (se existir e for descriptografável) e metadados salvos, sem chamar a rede.</summary>
    [SupportedOSPlatform("windows")]
    private void LoadPersistedState()
    {
        if (File.Exists(_accountInfoPath))
        {
            try
            {
                string json = File.ReadAllText(_accountInfoPath);
                _account = JsonSerializer.Deserialize<BackupAccountInfo>(json) ?? new BackupAccountInfo();
            }
            catch (JsonException)
            {
                _account = new BackupAccountInfo();
            }
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

        _http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        SecureTokenStore.Save(_tokenPath, token);

        _account.Login = user.Login;
        _account.GistId = await TryFindExistingBackupGistIdAsync(ct) ?? _account.GistId;
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

        _account = new BackupAccountInfo();
        _http.DefaultRequestHeaders.Authorization = null;
    }

    /// <summary>Cria ou atualiza o Gist secreto com TODAS as guias atuais (ver <see cref="ProfileBackupSet"/>). Retorna false em qualquer falha (sem lançar) — chamado de rotinas automáticas em segundo plano.</summary>
    public async Task<bool> UploadProfileAsync(ProfileBackupSet backupSet, CancellationToken ct = default)
    {
        if (!IsConnected)
            return false;

        await _syncLock.WaitAsync(ct);
        try
        {
            string json = JsonSerializer.Serialize(backupSet, ManifestJsonOptions);

            bool success = string.IsNullOrEmpty(_account.GistId)
                ? await CreateGistAsync(json, ct)
                : await UpdateGistAsync(_account.GistId!, json, ct);

            if (!success)
                return false;

            _account.LastSyncUtc = DateTime.UtcNow;
            PersistAccountInfo();
            return true;
        }
        catch (HttpRequestException)
        {
            return false;
        }
        catch (TaskCanceledException)
        {
            return false;
        }
        finally
        {
            _syncLock.Release();
        }
    }

    /// <summary>Baixa o backup (todas as guias) salvo no Gist da conta conectada. Retorna null se não houver backup ou em caso de falha.</summary>
    public async Task<ProfileBackupSet?> DownloadProfileAsync(CancellationToken ct = default)
    {
        if (!IsConnected)
            return null;

        // Perfil pode ter sido criado por outra instalação do app (outra máquina) que
        // nunca sincronizou por aqui — sempre reconfirma o GistId em vez de confiar só
        // no cache local, que pode estar vazio ou desatualizado.
        _account.GistId ??= await TryFindExistingBackupGistIdAsync(ct);
        if (string.IsNullOrEmpty(_account.GistId))
            return null;

        try
        {
            var response = await _http.GetAsync($"{ApiBase}/gists/{_account.GistId}", ct);
            if (!response.IsSuccessStatusCode)
                return null;

            var gist = await response.Content.ReadFromJsonAsync<GitHubGistResponse>(cancellationToken: ct);
            string? content = gist?.Files?.GetValueOrDefault(BackupFileName)?.Content;
            if (string.IsNullOrEmpty(content))
                return null;

            PersistAccountInfo();
            return JsonSerializer.Deserialize<ProfileBackupSet>(content, ManifestJsonOptions);
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

    private async Task<bool> CreateGistAsync(string profileJson, CancellationToken ct)
    {
        var payload = new
        {
            description = GistDescription,
            @public = false,
            files = new Dictionary<string, object>
            {
                [BackupFileName] = new { content = profileJson }
            }
        };

        var response = await _http.PostAsJsonAsync($"{ApiBase}/gists", payload, ct);
        if (!response.IsSuccessStatusCode)
            return false;

        var created = await response.Content.ReadFromJsonAsync<GitHubGistResponse>(cancellationToken: ct);
        if (created?.Id is null)
            return false;

        _account.GistId = created.Id;
        return true;
    }

    private async Task<bool> UpdateGistAsync(string gistId, string profileJson, CancellationToken ct)
    {
        var payload = new
        {
            files = new Dictionary<string, object>
            {
                [BackupFileName] = new { content = profileJson }
            }
        };

        using var request = new HttpRequestMessage(HttpMethod.Patch, $"{ApiBase}/gists/{gistId}")
        {
            Content = JsonContent.Create(payload)
        };

        var response = await _http.SendAsync(request, ct);

        // Gist foi apagado/perdeu acesso desde a última vez — tenta recriar em vez de
        // falhar silenciosamente pra sempre a partir daqui.
        if (response.StatusCode == HttpStatusCode.NotFound)
        {
            _account.GistId = null;
            return await CreateGistAsync(profileJson, ct);
        }

        return response.IsSuccessStatusCode;
    }

    /// <summary>Varre os gists da conta procurando um com a descrição/arquivo de backup do WinProvision Store.</summary>
    private async Task<string?> TryFindExistingBackupGistIdAsync(CancellationToken ct)
    {
        try
        {
            var response = await _http.GetAsync($"{ApiBase}/gists?per_page=100", ct);
            if (!response.IsSuccessStatusCode)
                return null;

            var gists = await response.Content.ReadFromJsonAsync<List<GitHubGistResponse>>(cancellationToken: ct);
            var match = gists?.FirstOrDefault(g =>
                (g.Description == GistDescription) ||
                (g.Files != null && g.Files.ContainsKey(BackupFileName)));

            return match?.Id;
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

    [SupportedOSPlatform("windows")]
    private void PersistAccountInfo()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_accountInfoPath)!);
        string json = JsonSerializer.Serialize(_account);
        File.WriteAllText(_accountInfoPath, json);
    }

    private class GitHubUserResponse
    {
        [JsonPropertyName("login")]
        public string? Login { get; set; }
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
    }
}
