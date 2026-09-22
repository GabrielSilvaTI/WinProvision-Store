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
/// COM nem na API própria. Fluxo: 1) consulta a release mais recente no GitHub;
/// 2) baixa o instalador (WinProvision.Store-Setup.exe, gerado pelo installer/WinProvision.Store.iss)
/// e confere o SHA-256 que o GitHub calcula por upload; 3) agenda a instalação silenciosa
/// (/VERYSILENT /SUPPRESSMSGBOXES /NORESTART) e o relançamento do app via um script separado,
/// já que o próprio processo precisa sair para o instalador poder substituir seus arquivos.
/// </summary>
public sealed class AppUpdateService
{
    private const string RepoSlug = "GabrielSilvaTI/WinProvision-Store";
    private const string AssetName = "WinProvision.Store-Setup.exe";
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
    /// Consulta "GET /repos/{RepoSlug}/releases/latest" e compara a tag publicada com a
    /// versão atual. Nunca lança: qualquer falha (rede, release sem o asset esperado, tag
    /// num formato inesperado) vira um <see cref="AppUpdateCheckResult.Failed"/>.
    /// </summary>
    public async Task<AppUpdateCheckResult> CheckForUpdateAsync(CancellationToken ct = default)
    {
        try
        {
            using var response = await _http.GetAsync(
                $"https://api.github.com/repos/{RepoSlug}/releases/latest", ct).ConfigureAwait(false);

            if (!response.IsSuccessStatusCode)
            {
                return AppUpdateCheckResult.Failed(
                    $"O GitHub respondeu {(int)response.StatusCode} ao consultar a última versão.");
            }

            await using var stream = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
            using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: ct).ConfigureAwait(false);
            var root = doc.RootElement;

            string tag = root.TryGetProperty("tag_name", out var tagProp) ? tagProp.GetString() ?? "" : "";
            string versionText = tag.TrimStart('v', 'V');
            if (!Version.TryParse(versionText, out var latestVersion))
            {
                return AppUpdateCheckResult.Failed(
                    $"Não reconheci o formato de versão da última release (\"{tag}\").");
            }

            if (!root.TryGetProperty("assets", out var assetsProp) || assetsProp.ValueKind != JsonValueKind.Array)
            {
                return AppUpdateCheckResult.Failed($"A release {latestVersion} não tem arquivos anexados.");
            }

            JsonElement? asset = assetsProp.EnumerateArray()
                .Cast<JsonElement?>()
                .FirstOrDefault(a => string.Equals(
                    a!.Value.TryGetProperty("name", out var n) ? n.GetString() : null,
                    AssetName,
                    StringComparison.OrdinalIgnoreCase));

            if (asset is null)
            {
                return AppUpdateCheckResult.Failed(
                    $"A release {latestVersion} não tem o instalador ({AssetName}) anexado.");
            }

            string? downloadUrl = asset.Value.TryGetProperty("browser_download_url", out var urlProp)
                ? urlProp.GetString()
                : null;
            if (string.IsNullOrEmpty(downloadUrl))
            {
                return AppUpdateCheckResult.Failed($"O instalador da release {latestVersion} não tem link de download.");
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
        if (update is not { Success: true, DownloadUrl: not null, LatestVersion: not null })
            throw new ArgumentException("Resultado de checagem inválido (sem DownloadUrl/LatestVersion).", nameof(update));

        string tempPath = Path.Combine(
            Path.GetTempPath(), $"WinProvision.Store-Setup-{update.LatestVersion}.exe");

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
