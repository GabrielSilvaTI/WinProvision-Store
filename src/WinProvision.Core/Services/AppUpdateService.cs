using System.Diagnostics;
using System.Linq;
using System.Net.Http.Headers;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using WinProvision.Core.Models;

namespace WinProvision.Core.Services;

/// <summary>
/// Autoatualização do WinProvision Store — inteiramente separada do provisionamento de
/// pacotes (WingetBootstrapper/WingetExecutor/WinProvisionApiService): não toca em winget,
/// COM nem na API própria. Fluxo: 1) consulta a release mais recente no GitHub (estável, e se
/// não houver nenhuma ainda, o canal nightly); 2) baixa o instalador
/// (WinProvision.Store-Setup.exe, gerado por installer/WinProvision.Store.iss) e confere o
/// SHA-256 que o GitHub calcula por upload; 3) agenda a instalação silenciosa
/// (/VERYSILENT /SUPPRESSMSGBOXES /NORESTART) e o relançamento do app via um script separado,
/// já que o próprio processo precisa sair para o instalador poder substituir seus arquivos.
/// </summary>
public sealed class AppUpdateService
{
    private const string RepoSlug = "GabrielSilvaTI/WinProvision-Store";
    private const string AssetName = "WinProvision.Store-Setup.exe";

    // O repositório hoje só publica esta release, de tag fixa: rebuild automático a cada
    // commit na main, sem versão semântica (ver releases/tag/nightly — "Não é a versão
    // estável, serve para testar antes de criar uma tag vX.Y.Z"). Quando existir uma tag
    // vX.Y.Z, CheckForUpdateAsync a usa primeiro; o nightly é o fallback (e, por ora, o único
    // canal publicado).
    private const string NightlyTag = "nightly";

    // Publicação da release e timestamp de build do .exe local vêm de relógios diferentes
    // (servidor do GitHub Actions vs. o file system do disco); essa margem evita marcar
    // "atualização disponível" por causa de alguns segundos de diferença entre os dois,
    // já que o próprio build recém-instalado tende a ficar bem perto do published_at.
    private static readonly TimeSpan NightlyFreshnessMargin = TimeSpan.FromMinutes(3);

    private static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(15);
    private static readonly TimeSpan DownloadTimeout = TimeSpan.FromMinutes(5);

    // Tempo máximo esperando o processo atual encerrar antes do script de atualização
    // desistir e apagar o instalador baixado sem aplicá-lo.
    private static readonly TimeSpan MaxWaitForExit = TimeSpan.FromMinutes(1);

    private readonly HttpClient _http;

    public AppUpdateService() : this(null) { }

    internal AppUpdateService(HttpClient? http)
    {
        _http = http ?? new HttpClient { Timeout = RequestTimeout };
        if (_http.DefaultRequestHeaders.UserAgent.Count == 0)
        {
            _http.DefaultRequestHeaders.UserAgent.Add(
                new ProductInfoHeaderValue("WinProvision-Store-Updater", "1.0"));
        }
        if (!_http.DefaultRequestHeaders.Accept.Any(h => h.MediaType == "application/vnd.github+json"))
        {
            _http.DefaultRequestHeaders.Accept.Add(
                new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
        }
    }

    /// <summary>Versão do próprio executável em disco (o mesmo número mostrado em Configurações).</summary>
    public static Version GetCurrentVersion() =>
        Assembly.GetEntryAssembly()?.GetName().Version ?? new Version(0, 0, 0, 0);

    /// <summary>
    /// Data de modificação do executável atual — o mesmo dado que a Store já loga como
    /// "buildDate" na abertura (App.xaml.cs). O publish self-contained (ver csproj: single-file
    /// desligado de propósito) e o instalador Inno preservam esse timestamp do build original,
    /// então ele fica bem próximo do published_at da release no GitHub Actions que o gerou —
    /// é o que permite comparar "meu build é mais velho que o nightly publicado?" sem precisar
    /// embutir um número de versão no canal nightly.
    /// </summary>
    private static DateTimeOffset GetCurrentBuildTime()
    {
        string? exePath = Environment.ProcessPath ?? Assembly.GetEntryAssembly()?.Location;
        return exePath is not null && File.Exists(exePath)
            ? new DateTimeOffset(File.GetLastWriteTimeUtc(exePath), TimeSpan.Zero)
            : DateTimeOffset.MinValue;
    }

    /// <summary>
    /// Consulta a release mais recente no GitHub e compara com o build atual: primeiro o
    /// canal estável ("/releases/latest", por versão semântica da tag); se o repositório
    /// ainda não tiver nenhuma release estável publicada, cai para o canal nightly (tag fixa,
    /// comparado por data de publicação). Nunca lança: qualquer falha em ambos os canais vira
    /// um <see cref="AppUpdateCheckResult.Failed"/> com a mensagem do canal estável.
    /// </summary>
    public async Task<AppUpdateCheckResult> CheckForUpdateAsync(CancellationToken ct = default)
    {
        var stable = await CheckStableChannelAsync(ct).ConfigureAwait(false);
        if (stable.Success)
            return stable;

        var nightly = await CheckNightlyChannelAsync(ct).ConfigureAwait(false);
        return nightly.Success ? nightly : stable;
    }

    private async Task<AppUpdateCheckResult> CheckStableChannelAsync(CancellationToken ct)
    {
        try
        {
            using var response = await _http.GetAsync(
                $"https://api.github.com/repos/{RepoSlug}/releases/latest", ct).ConfigureAwait(false);

            if (!response.IsSuccessStatusCode)
            {
                // 404 é o caso normal enquanto só existir a release nightly (sem versão
                // estável publicada ainda) — CheckForUpdateAsync cai pro outro canal sozinho.
                return AppUpdateCheckResult.Failed(
                    $"O GitHub respondeu {(int)response.StatusCode} ao consultar a última versão estável.");
            }

            await using var stream = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
            using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: ct).ConfigureAwait(false);
            var root = doc.RootElement;

            string tag = root.TryGetProperty("tag_name", out var tagProp) ? tagProp.GetString() ?? "" : "";
            string versionText = tag.TrimStart('v', 'V');
            if (!Version.TryParse(versionText, out var latestVersion))
            {
                return AppUpdateCheckResult.Failed(
                    $"Não reconheci o formato de versão da última release estável (\"{tag}\").");
            }

            var (downloadUrl, sha256, assetError) = FindInstallerAsset(root, latestVersion.ToString());
            if (assetError is not null)
                return AppUpdateCheckResult.Failed(assetError);

            string? releaseUrl = root.TryGetProperty("html_url", out var htmlUrlProp) ? htmlUrlProp.GetString() : null;
            var current = GetCurrentVersion();

            return new AppUpdateCheckResult(
                Success: true,
                UpdateAvailable: latestVersion > current,
                CurrentVersion: current,
                LatestVersion: latestVersion,
                DownloadUrl: downloadUrl,
                Sha256: sha256,
                ReleaseUrl: releaseUrl);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            return AppUpdateCheckResult.Failed($"Falha ao verificar atualizações: {ex.Message}");
        }
    }

    private async Task<AppUpdateCheckResult> CheckNightlyChannelAsync(CancellationToken ct)
    {
        try
        {
            using var response = await _http.GetAsync(
                $"https://api.github.com/repos/{RepoSlug}/releases/tags/{NightlyTag}", ct).ConfigureAwait(false);

            if (!response.IsSuccessStatusCode)
            {
                return AppUpdateCheckResult.Failed(
                    $"O GitHub respondeu {(int)response.StatusCode} ao consultar a release \"{NightlyTag}\".");
            }

            await using var stream = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
            using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: ct).ConfigureAwait(false);
            var root = doc.RootElement;

            if (!root.TryGetProperty("published_at", out var publishedProp) ||
                !DateTimeOffset.TryParse(publishedProp.GetString(), out var publishedAt))
            {
                return AppUpdateCheckResult.Failed($"A release \"{NightlyTag}\" não informou a data de publicação.");
            }

            var (downloadUrl, sha256, assetError) = FindInstallerAsset(root, NightlyTag);
            if (assetError is not null)
                return AppUpdateCheckResult.Failed(assetError);

            string? releaseLabel = root.TryGetProperty("name", out var nameProp) ? nameProp.GetString() : null;
            string? releaseUrl = root.TryGetProperty("html_url", out var htmlUrlProp) ? htmlUrlProp.GetString() : null;

            bool hasUpdate = publishedAt.ToUniversalTime() - GetCurrentBuildTime().ToUniversalTime() > NightlyFreshnessMargin;

            return new AppUpdateCheckResult(
                Success: true,
                UpdateAvailable: hasUpdate,
                CurrentVersion: GetCurrentVersion(),
                LatestVersion: null,
                DownloadUrl: downloadUrl,
                Sha256: sha256,
                ReleaseUrl: releaseUrl,
                ReleaseLabel: releaseLabel,
                PublishedAt: publishedAt);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            return AppUpdateCheckResult.Failed($"Falha ao verificar a release \"{NightlyTag}\": {ex.Message}");
        }
    }

    /// <summary>Acha o asset do instalador (<see cref="AssetName"/>) dentro de "assets" e extrai link + SHA-256.</summary>
    private static (string? DownloadUrl, string? Sha256, string? Error) FindInstallerAsset(JsonElement root, string releaseLabel)
    {
        if (!root.TryGetProperty("assets", out var assetsProp) || assetsProp.ValueKind != JsonValueKind.Array)
        {
            return (null, null, $"A release {releaseLabel} não tem arquivos anexados.");
        }

        JsonElement? asset = assetsProp.EnumerateArray()
            .Cast<JsonElement?>()
            .FirstOrDefault(a => string.Equals(
                a!.Value.TryGetProperty("name", out var n) ? n.GetString() : null,
                AssetName,
                StringComparison.OrdinalIgnoreCase));

        if (asset is null)
        {
            return (null, null, $"A release {releaseLabel} não tem o instalador ({AssetName}) anexado.");
        }

        string? downloadUrl = asset.Value.TryGetProperty("browser_download_url", out var urlProp)
            ? urlProp.GetString()
            : null;
        if (string.IsNullOrEmpty(downloadUrl))
        {
            return (null, null, $"O instalador da release {releaseLabel} não tem link de download.");
        }

        // GitHub calcula e expõe o SHA-256 de todo asset carregado desde jun/2025
        // (formato "sha256:<hex>"); releases mais antigas podem não ter o campo.
        string? sha256 = null;
        if (asset.Value.TryGetProperty("digest", out var digestProp))
        {
            string? digest = digestProp.GetString();
            const string prefix = "sha256:";
            if (digest is not null && digest.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            {
                sha256 = digest[prefix.Length..];
            }
        }

        return (downloadUrl, sha256, null);
    }

    /// <summary>
    /// Baixa o instalador de <paramref name="update"/> para uma pasta temporária e, se o
    /// GitHub informou o SHA-256 do upload, confere o arquivo baixado contra ele — um
    /// instalador com hash divergente é apagado e a chamada lança em vez de seguir adiante.
    /// Sem o campo (releases publicadas antes de o GitHub passar a calculá-lo), a checagem
    /// é pulada; o instalador ainda roda assinado/via HTTPS, só sem essa camada extra.
    /// </summary>
    public async Task<string> DownloadInstallerAsync(
        AppUpdateCheckResult update, IProgress<double>? progress = null, CancellationToken ct = default)
    {
        if (update is not { Success: true, DownloadUrl: not null })
            throw new ArgumentException("Resultado de checagem inválido (sem DownloadUrl).", nameof(update));

        string label = update.LatestVersion?.ToString() ?? update.ReleaseLabel ?? NightlyTag;
        string safeLabel = string.Join("_", label.Split(Path.GetInvalidFileNameChars()));
        string tempPath = Path.Combine(Path.GetTempPath(), $"WinProvision.Store-Setup-{safeLabel}.exe");

        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeoutCts.CancelAfter(DownloadTimeout);

        using (var response = await _http
                   .GetAsync(update.DownloadUrl, HttpCompletionOption.ResponseHeadersRead, timeoutCts.Token)
                   .ConfigureAwait(false))
        {
            response.EnsureSuccessStatusCode();

            long? totalBytes = response.Content.Headers.ContentLength;
            await using var source = await response.Content.ReadAsStreamAsync(timeoutCts.Token).ConfigureAwait(false);
            await using var destination = File.Create(tempPath);

            var buffer = new byte[81920];
            long readTotal = 0;
            int read;
            while ((read = await source.ReadAsync(buffer, timeoutCts.Token).ConfigureAwait(false)) > 0)
            {
                await destination.WriteAsync(buffer.AsMemory(0, read), timeoutCts.Token).ConfigureAwait(false);
                readTotal += read;
                if (totalBytes is > 0)
                {
                    progress?.Report((double)readTotal / totalBytes.Value);
                }
            }
        }

        if (!string.IsNullOrEmpty(update.Sha256))
        {
            string actual = await ComputeSha256Async(tempPath, ct).ConfigureAwait(false);
            if (!string.Equals(actual, update.Sha256, StringComparison.OrdinalIgnoreCase))
            {
                TryDelete(tempPath);
                throw new InvalidOperationException(
                    "O instalador baixado não bate com o SHA-256 publicado pelo GitHub; descartado por segurança.");
            }
        }

        return tempPath;
    }

    private static async Task<string> ComputeSha256Async(string path, CancellationToken ct)
    {
        await using var stream = File.OpenRead(path);
        byte[] hash = await SHA256.HashDataAsync(stream, ct).ConfigureAwait(false);
        return Convert.ToHexStringLower(hash);
    }

    /// <summary>
    /// Escreve e dispara (sem esperar) um script .cmd que: espera o PROCESSO ATUAL terminar,
    /// roda o instalador em modo silencioso, reabre o app no caminho instalado e se
    /// autoapaga junto com o instalador. Não encerra o processo atual — quem chama decide
    /// quando (normalmente logo em seguida, com Application.Shutdown ou Environment.Exit),
    /// já que o instalador precisa que o WinProvision.Store.exe em uso já não esteja rodando
    /// pra poder substituir seus arquivos.
    /// </summary>
    public static void ScheduleSilentInstallAndRestart(string setupPath)
    {
        if (!File.Exists(setupPath))
            throw new FileNotFoundException("Instalador não encontrado.", setupPath);

        string exePath = Environment.ProcessPath
            ?? Path.Combine(AppContext.BaseDirectory, "WinProvision.Store.exe");
        string scriptPath = Path.Combine(Path.GetTempPath(), $"WinProvision-Update-{Guid.NewGuid():N}.cmd");
        int pid = Environment.ProcessId;
        int maxWaitSeconds = (int)MaxWaitForExit.TotalSeconds;

        string script = string.Join("\r\n",
        [
            "@echo off",
            "setlocal enabledelayedexpansion",
            "set count=0",
            ":wait",
            $"tasklist /fi \"PID eq {pid}\" | find \"{pid}\" >nul",
            "if not errorlevel 1 (",
            "  set /a count+=1",
            $"  if !count! GEQ {maxWaitSeconds} goto giveup",
            "  timeout /t 1 /nobreak >nul",
            "  goto wait",
            ")",
            $"\"{setupPath}\" /VERYSILENT /SUPPRESSMSGBOXES /NORESTART",
            $"start \"\" \"{exePath}\"",
            ":giveup",
            $"del \"{setupPath}\" >nul 2>nul",
            "(goto) 2>nul & del \"%~f0\"",
        ]);

        // ASCII de propósito: o instalador (installer/WinProvision.Store.iss, IsDirNameValid)
        // já barra caminhos fora de ASCII, então os caminhos aqui embutidos nunca têm acento.
        File.WriteAllText(scriptPath, script, Encoding.ASCII);

        var startInfo = new ProcessStartInfo
        {
            FileName = "cmd.exe",
            Arguments = $"/c \"{scriptPath}\"",
            UseShellExecute = false,
            CreateNoWindow = true,
            WindowStyle = ProcessWindowStyle.Hidden,
        };
        Process.Start(startInfo);
    }

    private static void TryDelete(string path)
    {
        try { File.Delete(path); }
        catch { /* melhor-esforço */ }
    }
}
