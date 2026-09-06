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
    // (Anteriormente este serviço tinha sua própria referência a ManifestJsonOptions —
    //  agora usa a WinProvisionJsonOptions centralizada.)
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
    /// URL "raw" pública e estável do Gist de backup automático — o mesmo arquivo (<see cref="ProfileBackupSet"/>)
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
            ? $"https://gist.githubusercontent.com/{_account.Login}/{_account.GistId}/raw"
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
                _account = JsonSerializer.Deserialize<BackupAccountInfo>(json, WinProvisionJsonOptions.Compact) ?? new BackupAccountInfo();
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
        _account.GistId = await TryFindGistIdAsync(GistDescription, BackupFileName, ct) ?? _account.GistId;
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

            if (string.IsNullOrEmpty(_account.GistId))
            {
                // O ID é a identidade permanente do Gist. Sempre tenta reencontrar o
                // backup antes de criar outro, inclusive após limpar o estado local.
                _account.GistId = await TryFindGistIdAsync(GistDescription, BackupFileName, ct);
            }

            bool success = await UpsertGistAsync(
                BackupFileName, GistDescription, _account.GistId, json,
                onIdChanged: id => _account.GistId = id,
                onRecreateNeeded: () => _account.GistId = null,
                ct);

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
        _account.GistId ??= await TryFindGistIdAsync(GistDescription, BackupFileName, ct);
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

    // Helper genérico para localizar e atualizar o Gist de backup do perfil.

    /// <summary>Varre todos os gists da conta procurando um com a descrição ou o nome de arquivo dados.</summary>
    private async Task<string?> TryFindGistIdAsync(string description, string fileName, CancellationToken ct)
    {
        try
        {
            for (int page = 1; page <= 10; page++)
            {
                var response = await _http.GetAsync($"{ApiBase}/gists?per_page=100&page={page}", ct);
                if (!response.IsSuccessStatusCode)
                    return null;

                var gists = await response.Content.ReadFromJsonAsync<List<GitHubGistResponse>>(cancellationToken: ct);
                if (gists is null || gists.Count == 0)
                    return null;

                var match = gists.FirstOrDefault(g =>
                    string.Equals(g.Description, description, StringComparison.Ordinal) ||
                    (g.Files != null && g.Files.ContainsKey(fileName)));

                if (match?.Id is not null)
                    return match.Id;

                if (gists.Count < 100)
                    return null;
            }

            return null;
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

    private async Task<string?> CreateGistAsync(string fileName, string description, string content, CancellationToken ct)
    {
        var payload = new
        {
            description,
            @public = false,
            files = new Dictionary<string, object>
            {
                [fileName] = new { content }
            }
        };

        var response = await _http.PostAsJsonAsync($"{ApiBase}/gists", payload, ct);
        if (!response.IsSuccessStatusCode)
            return null;

        var created = await response.Content.ReadFromJsonAsync<GitHubGistResponse>(cancellationToken: ct);
        return created?.Id;
    }

    /// <summary>
    /// Cria (se <paramref name="existingGistId"/> for nulo/vazio) ou atualiza o Gist com o
    /// conteúdo dado. Em caso de 404 no update (Gist apagado/perdeu acesso desde a última
    /// vez), reencontra por descrição/nome de arquivo e tenta de novo antes de desistir.
    /// Único chamador hoje é <see cref="UploadProfileAsync"/>.
    /// </summary>
    private async Task<bool> UpsertGistAsync(
        string fileName, string description, string? existingGistId, string content,
        Action<string?> onIdChanged, Action onRecreateNeeded, CancellationToken ct)
    {
        try
        {
            if (string.IsNullOrEmpty(existingGistId))
            {
                string? createdId = await CreateGistAsync(fileName, description, content, ct);
                if (createdId is null)
                    return false;

                onIdChanged(createdId);
                return true;
            }

            var payload = new { files = new Dictionary<string, object> { [fileName] = new { content } } };
            using var request = new HttpRequestMessage(HttpMethod.Patch, $"{ApiBase}/gists/{existingGistId}")
            {
                Content = JsonContent.Create(payload)
            };

            var response = await _http.SendAsync(request, ct);
            if (response.StatusCode != HttpStatusCode.NotFound)
                return response.IsSuccessStatusCode;

            // Gist foi apagado/perdeu acesso desde a última vez — reencontra por
            // descrição/nome de arquivo antes de desistir, em vez de falhar direto.
            onRecreateNeeded();
            string? recheckedId = await TryFindGistIdAsync(description, fileName, ct);
            onIdChanged(recheckedId);
            return await UpsertGistAsync(fileName, description, recheckedId, content, onIdChanged, onRecreateNeeded, ct);
        }
        catch (HttpRequestException)
        {
            return false;
        }
        catch (JsonException)
        {
            return false;
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
