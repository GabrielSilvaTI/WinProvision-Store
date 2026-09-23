// WinProvisionApiService.cs
// Cliente para a API própria da WinProvision Store (Store/Api/v1 no Cloudflare R2).
//
// Formato observado:
//   Store/Api/v1/index.json
//     { "schema":1, "generatedAt":"...", "count":N,
//       "packages":[ { "id":"115.115Chrome", "version":"36.0.1", "architectures":["x64","x86"] }, ... ] }
//
//   Store/Api/v1/packages/{id}.json
//     { "schema":1, "id":"115.115Chrome", "version":"36.0.1",
//       "installers":[
//         { "architecture":"x64", "type":"nullsoft", "scope":"user",
//           "url":"...", "sha256":"...", "silentArgs":"/S -disable-auto-start",
//           "silentSource":"default", "silentSupported":true, "productCode":"115Chrome" },
//         ...
//       ] }
//
// Este arquivo é standalone (só depende de System.Net.Http.Json e System.Text.Json,
// além de System.IO.Compression pra instaladores empacotados em zip) pra você colar
// dentro de WinProvision.Core/Services e ajustar namespace/usings conforme a
// estrutura real do projeto.

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Json;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Win32;
using WinProvision.Core.Models;

namespace WinProvision.Core.Services;

#region Modelos (batem 1:1 com o JSON publicado)

public sealed class PackageIndex
{
    [JsonPropertyName("schema")] public int Schema { get; set; }
    [JsonPropertyName("generatedAt")] public DateTimeOffset GeneratedAt { get; set; }
    [JsonPropertyName("count")] public int Count { get; set; }
    [JsonPropertyName("packages")] public List<PackageIndexEntry> Packages { get; set; } = [];
}

public sealed class PackageIndexEntry
{
    [JsonPropertyName("id")] public string Id { get; set; } = "";
    [JsonPropertyName("version")] public string Version { get; set; } = "";
    [JsonPropertyName("architectures")] public List<string> Architectures { get; set; } = [];
}

public sealed class PackageManifest
{
    [JsonPropertyName("schema")] public int Schema { get; set; }
    [JsonPropertyName("id")] public string Id { get; set; } = "";
    [JsonPropertyName("version")] public string Version { get; set; } = "";
    [JsonPropertyName("installers")] public List<PackageInstaller> Installers { get; set; } = [];
}

public sealed class PackageInstaller
{
    [JsonPropertyName("architecture")] public string Architecture { get; set; } = "";
    [JsonPropertyName("type")] public string Type { get; set; } = "";           // nullsoft, inno, msi, burn, exe, zip...
    // Só vem preenchido quando Type == "zip": tipo/caminho do instalador real de dentro do pacote.
    [JsonPropertyName("nestedType")] public string? NestedType { get; set; }
    [JsonPropertyName("nestedInstallerFile")] public string? NestedInstallerFile { get; set; }
    [JsonPropertyName("portableCommandAlias")] public string? PortableCommandAlias { get; set; }
    [JsonPropertyName("scope")] public string Scope { get; set; } = "";         // user | machine
    [JsonPropertyName("url")] public string Url { get; set; } = "";
    [JsonPropertyName("sha256")] public string Sha256 { get; set; } = "";
    [JsonPropertyName("silentArgs")] public string SilentArgs { get; set; } = "";
    [JsonPropertyName("silentSource")] public string SilentSource { get; set; } = "";
    [JsonPropertyName("silentSupported")] public bool SilentSupported { get; set; }
    [JsonPropertyName("productCode")] public string? ProductCode { get; set; }
    [JsonPropertyName("successCodes")] public List<int> SuccessCodes { get; set; } = [];

    /// <summary>Atalho: true quando este instalador é um zip com instalador aninhado resolvido pela API.</summary>
    [JsonIgnore]
    public bool IsZipWithNestedInstaller =>
        string.Equals(Type, "zip", StringComparison.OrdinalIgnoreCase) && !string.IsNullOrEmpty(NestedInstallerFile);

    [JsonIgnore]
    public bool IsPortable => string.Equals(Type, "portable", StringComparison.OrdinalIgnoreCase)
                              || (string.Equals(Type, "zip", StringComparison.OrdinalIgnoreCase)
                                  && string.Equals(NestedType, "portable", StringComparison.OrdinalIgnoreCase));
}

#endregion

public enum WinProvisionInstallOutcome
{
    Success,
    PackageNotFound,
    NoCompatibleInstaller,
    SilentInstallNotSupported,
    DownloadFailed,
    HashMismatch,
    ExtractionFailed,
    InstallProcessFailed,
    ElevationCanceled,
    InvalidManifest
}

public sealed record WinProvisionInstallResult(
    WinProvisionInstallOutcome Outcome,
    int? ExitCode = null,
    string? Message = null);

public sealed record WinProvisionInstallerCommand(string FileName, string Arguments);

public static class WinProvisionInstallerCommandBuilder
{
    public static WinProvisionInstallerCommand Build(string installerPath, string? installerType, string? silentArgs)
    {
        string type = installerType?.Trim().ToLowerInvariant() ?? string.Empty;
        bool isMsi = type is "msi" or "wixmsi"
                     || Path.GetExtension(installerPath).Equals(".msi", StringComparison.OrdinalIgnoreCase);
        string args = silentArgs?.Trim() ?? string.Empty;

        if (isMsi)
        {
            string msiexec = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "msiexec.exe");
            return new WinProvisionInstallerCommand(msiexec, $"/i \"{installerPath}\" {args}".TrimEnd());
        }

        return new WinProvisionInstallerCommand(installerPath, args);
    }
}

/// <summary>
/// Cliente da API própria da WinProvision Store. Serve como:
///  1) fallback direto quando o Winget COM API falha;
///  2) caminho primário quando a reelevação falha ou o processo já roda como SYSTEM
///     (cenários em que a COM do winget não é viável).
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class WinProvisionApiService
{
    private const string BaseUrl = "https://pub-166b41912a994dbe86583ba10596d673.r2.dev/Store/Api/v1/";

    private readonly HttpClient _http;
    private readonly string _cacheDir;
    private readonly SemaphoreSlim _indexLock = new(1, 1);
    private readonly SemaphoreSlim _manifestCacheLock = new(1, 1);
    private readonly SemaphoreSlim _installRecordsLock = new(1, 1);
    private readonly string _installRecordsPath;

    private PackageIndex? _indexCache;
    private DateTimeOffset _indexCachedAt;
    private static readonly TimeSpan IndexTtl = TimeSpan.FromMinutes(15);
    private static readonly TimeSpan RequestTimeout = TimeSpan.FromMinutes(2);
    private static readonly TimeSpan DownloadTimeout = TimeSpan.FromMinutes(12);
    private const int MaxRequestAttempts = 4;
    private const long MaxInstallerBytes = 4L * 1024 * 1024 * 1024;

    public WinProvisionApiService(HttpClient? http = null, string? cacheDir = null)
    {
        _http = http ?? new HttpClient(new SocketsHttpHandler
        {
            ConnectTimeout = TimeSpan.FromSeconds(25),
            PooledConnectionLifetime = TimeSpan.FromMinutes(10),
            MaxConnectionsPerServer = 8
        }) { Timeout = Timeout.InfiniteTimeSpan };
        if (_http.DefaultRequestHeaders.UserAgent.Count == 0)
            _http.DefaultRequestHeaders.UserAgent.ParseAdd("WinProvisionStore/1.0");
        _cacheDir = cacheDir ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "WinProvisionStore", "ApiCache");
        _installRecordsPath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "WinProvisionStore", "ApiInstalledPackages.json");
        Directory.CreateDirectory(_cacheDir);
    }

    /// <summary>Detecta se o processo atual roda sob a conta SYSTEM (NT AUTHORITY\SYSTEM).</summary>
    public static bool IsRunningAsSystem()
    {
        using var identity = System.Security.Principal.WindowsIdentity.GetCurrent();
        return identity.User?.IsWellKnown(System.Security.Principal.WellKnownSidType.LocalSystemSid) == true;
    }

    /// <summary>
    /// Decide, antes de qualquer tentativa, se o caminho COM do winget deve nem ser tentado.
    /// Use no início do fluxo de instalação (tanto UI quanto /auto).
    /// </summary>
    public static bool ShouldSkipWingetCom() => IsRunningAsSystem();

    public async Task<PackageIndex> GetIndexAsync(bool forceRefresh = false, CancellationToken ct = default)
    {
        await _indexLock.WaitAsync(ct);
        try
        {
            if (!forceRefresh && _indexCache is not null && DateTimeOffset.UtcNow - _indexCachedAt < IndexTtl)
                return _indexCache;

            try
            {
                var index = await GetJsonWithRetryAsync<PackageIndex>(BaseUrl + "index.json", ct)
                    ?? throw new InvalidDataException("index.json vazio ou inválido.");
                _indexCache = index;
                _indexCachedAt = DateTimeOffset.UtcNow;
                await WriteCacheFileAsync(IndexCachePath, JsonSerializer.Serialize(index), ct);
                return index;
            }
            catch (Exception ex) when (ex is HttpRequestException or IOException or TaskCanceledException or JsonException)
            {
                var cached = await ReadCacheFileAsync<PackageIndex>(IndexCachePath, ct);
                if (cached is not null)
                {
                    _indexCache = cached;
                    _indexCachedAt = DateTimeOffset.UtcNow;
                    return cached;
                }
                throw new HttpRequestException("Não foi possível consultar a API e não há índice local disponível.", ex);
            }
        }
        finally
        {
            _indexLock.Release();
        }
    }

    public async Task<PackageManifest?> GetPackageAsync(string id, CancellationToken ct = default)
    {
        string cachePath = GetManifestCachePath(id);
        try
        {
            var manifest = await GetJsonWithRetryAsync<PackageManifest>(
                $"{BaseUrl}packages/{Uri.EscapeDataString(id)}.json", ct);
            if (manifest is not null)
            {
                await WriteCacheFileAsync(cachePath, JsonSerializer.Serialize(manifest), ct);
                return manifest;
            }
            return await ReadCacheFileAsync<PackageManifest>(cachePath, ct);
        }
        catch (HttpRequestException ex) when (ex.StatusCode == HttpStatusCode.NotFound)
        {
            return null;
        }
        catch (Exception ex) when (ex is HttpRequestException or IOException or TaskCanceledException or JsonException)
        {
            var cached = await ReadCacheFileAsync<PackageManifest>(cachePath, ct);
            if (cached is not null) return cached;
            throw new HttpRequestException($"Falha ao consultar o manifesto de '{id}' e não há cache local.", ex);
        }
    }

    private string IndexCachePath => Path.Combine(_cacheDir, "index.json");

    private string GetManifestCachePath(string id)
    {
        string key = Convert.ToHexString(SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(id.ToUpperInvariant())));
        return Path.Combine(_cacheDir, $"manifest-{key}.json");
    }

    private async Task<T?> GetJsonWithRetryAsync<T>(string url, CancellationToken ct)
    {
        for (int attempt = 1; ; attempt++)
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(RequestTimeout);
            try
            {
                using var response = await SendWithRetryAsync(url, timeout.Token).ConfigureAwait(false);
                if (response.StatusCode == HttpStatusCode.NotFound) return default;
                response.EnsureSuccessStatusCode();
                return await response.Content.ReadFromJsonAsync<T>(cancellationToken: timeout.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested && attempt < MaxRequestAttempts)
            {
                await Task.Delay(TimeSpan.FromMilliseconds(350 * attempt), ct).ConfigureAwait(false);
            }
            catch (HttpRequestException) when (attempt < MaxRequestAttempts)
            {
                await Task.Delay(TimeSpan.FromMilliseconds(350 * attempt), ct).ConfigureAwait(false);
            }
        }
    }

    private async Task<HttpResponseMessage> SendWithRetryAsync(string url, CancellationToken ct)
    {
        for (int attempt = 1; ; attempt++)
        {
            try
            {
                var response = await _http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
                if (!IsTransientStatus(response.StatusCode) || attempt >= MaxRequestAttempts)
                    return response;

                var delay = GetRetryDelay(response, attempt);
                response.Dispose();
                await Task.Delay(delay, ct).ConfigureAwait(false);
            }
            catch (HttpRequestException) when (attempt < MaxRequestAttempts)
            {
                await Task.Delay(TimeSpan.FromMilliseconds(400 * attempt), ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested && attempt < MaxRequestAttempts)
            {
                await Task.Delay(TimeSpan.FromMilliseconds(400 * attempt), ct).ConfigureAwait(false);
            }
        }
    }

    private static bool IsTransientStatus(HttpStatusCode status) =>
        status is HttpStatusCode.RequestTimeout or HttpStatusCode.TooManyRequests
        || (int)status is >= 500 and <= 599;

    private static TimeSpan GetRetryDelay(HttpResponseMessage response, int attempt)
    {
        var retryAfter = response.Headers.RetryAfter;
        if (retryAfter?.Delta is { } delta) return TimeSpan.FromMilliseconds(Math.Clamp(delta.TotalMilliseconds, 250, 8000));
        if (retryAfter?.Date is { } date) return TimeSpan.FromMilliseconds(Math.Clamp((date - DateTimeOffset.UtcNow).TotalMilliseconds, 250, 8000));
        return TimeSpan.FromMilliseconds(Math.Min(5000, 350 * Math.Pow(2, attempt - 1)));
    }

    private async Task<T?> ReadCacheFileAsync<T>(string path, CancellationToken ct)
    {
        if (!File.Exists(path)) return default;
        try
        {
            await using var stream = File.OpenRead(path);
            return await JsonSerializer.DeserializeAsync<T>(stream, cancellationToken: ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            return default;
        }
    }

    private async Task WriteCacheFileAsync(string path, string contents, CancellationToken ct)
    {
        await _manifestCacheLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            string temp = path + ".tmp";
            await File.WriteAllTextAsync(temp, contents, ct).ConfigureAwait(false);
            File.Move(temp, path, overwrite: true);
        }
        finally
        {
            _manifestCacheLock.Release();
        }
    }

    /// <summary>
    /// Escolhe o melhor installer para a arquitetura atual da máquina, com fallback pra x86
    /// (compatível via WOW64) quando não existe build nativa.
    /// </summary>
    public static PackageInstaller? PickInstaller(
        PackageManifest manifest,
        Architecture? preferred = null,
        string? preferredScope = null)
    {
        var current = preferred ?? RuntimeInformation.OSArchitecture;
        var order = current switch
        {
            Architecture.Arm64 => new[] { "arm64", "x64", "x86" },
            Architecture.X64 => new[] { "x64", "x86" },
            Architecture.Arm => new[] { "arm", "x86" },
            _ => new[] { "x86" }
        };

        foreach (var arch in order)
        {
            // Dentro da mesma arquitetura, prefere um installer com silentSupported=true
            // (ex.: pacote que publica tanto um .exe quanto um .zip pra mesma arch) —
            // evita escolher por acaso um tipo que a API própria não sabe instalar sem
            // interação quando existe alternativa silenciosa disponível.
            var candidates = manifest.Installers
                .Where(i => string.Equals(i.Architecture, arch, StringComparison.OrdinalIgnoreCase))
                .ToList();
            candidates = PreferScope(candidates, preferredScope)
                .OrderByDescending(i => i.SilentSupported)
                .ToList();
            if (candidates.Count > 0) return candidates[0];
        }
        // Um instalador sem arquitetura declarada ainda pode ser selecionado. Não escolha
        // um binário marcado para uma arquitetura incompatível só por ser o único restante;
        // o fallback WinGet sabe tratar manifests especiais e emulation melhor.
        return PreferScope(manifest.Installers.Where(i => string.IsNullOrWhiteSpace(i.Architecture)), preferredScope)
            .OrderByDescending(i => i.SilentSupported)
            .FirstOrDefault();
    }

    private static IEnumerable<PackageInstaller> PreferScope(
        IEnumerable<PackageInstaller> installers,
        string? preferredScope)
    {
        var available = installers.ToList();
        string? target = preferredScope?.Trim().ToLowerInvariant() switch
        {
            "user" => "user",
            "machine" or "system" => "machine",
            _ => null
        };
        if (target is null)
            return available;

        var matching = available.Where(i => string.Equals(i.Scope, target, StringComparison.OrdinalIgnoreCase)).ToList();
        return matching.Count > 0 ? matching : available;
    }

    /// <summary>
    /// Baixa e instala silenciosamente. Ponto de entrada único a ser chamado
    /// pelos três gatilhos: falha da COM, falha de reelevação, execução em SYSTEM.
    /// </summary>
    public async Task<WinProvisionInstallResult> TryInstallAsync(
        string packageId,
        IProgress<string>? onLog = null,
        CancellationToken ct = default,
        string? preferredArchitecture = null,
        string? preferredScope = null,
        Action<InstallProgressUpdate>? onProgress = null)
    {
        onLog?.Report($"[WinProvisionAPI] Consultando manifest de '{packageId}'...");
        PackageManifest? manifest;
        try
        {
            manifest = await GetPackageAsync(packageId, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex) when (ex is HttpRequestException or IOException or JsonException or TaskCanceledException)
        {
            return new WinProvisionInstallResult(WinProvisionInstallOutcome.DownloadFailed,
                Message: $"Falha ao consultar o catálogo da API própria: {ex.Message}");
        }

        if (manifest is null)
            return new WinProvisionInstallResult(WinProvisionInstallOutcome.PackageNotFound,
                Message: $"Pacote '{packageId}' não encontrado na API própria.");

        if (!string.Equals(manifest.Id, packageId, StringComparison.OrdinalIgnoreCase)
            || manifest.Installers is null || manifest.Installers.Count == 0)
            return new WinProvisionInstallResult(WinProvisionInstallOutcome.InvalidManifest,
                Message: "O manifesto retornado não corresponde ao pacote ou não possui instaladores.");

        var requestedArchitecture = preferredArchitecture?.Trim().ToLowerInvariant() switch
        {
            "x86" => Architecture.X86,
            "x64" => Architecture.X64,
            "arm64" => Architecture.Arm64,
            "arm" => Architecture.Arm,
            _ => (Architecture?)null
        };
        var installer = PickInstaller(manifest, requestedArchitecture, preferredScope);
        if (installer is null)
            return new WinProvisionInstallResult(WinProvisionInstallOutcome.NoCompatibleInstaller,
                Message: $"Nenhum installer compatível para '{packageId}'.");

        if (!Uri.TryCreate(installer.Url, UriKind.Absolute, out var installerUri)
            || installerUri.Scheme != Uri.UriSchemeHttps)
            return new WinProvisionInstallResult(WinProvisionInstallOutcome.InvalidManifest,
                Message: "O manifesto não contém uma URL HTTPS válida para o instalador.");

        if (!IsValidSha256(installer.Sha256))
            return new WinProvisionInstallResult(WinProvisionInstallOutcome.InvalidManifest,
                Message: "O manifesto não contém um SHA-256 válido; o instalador não será executado.");

        // O catálogo (InstallerApiExporter) já marca silentSupported=false pra tipos que
        // não sabe instalar sem interação (msix, appx, portable, exe sem switch declarado
        // no manifesto, ou um zip cujo NestedInstallerType/NestedInstallerFiles não foi
        // resolvido). Baixar e tentar EXECUTAR esse arquivo sempre falha nesses casos —
        // melhor falhar aqui, ANTES do download, e deixar o nível 3 (winget.exe) cuidar
        // deles, já que o próprio winget sabe lidar com zip/portable/msix nativamente.
        if (!installer.SilentSupported && !installer.IsPortable)
            return new WinProvisionInstallResult(WinProvisionInstallOutcome.SilentInstallNotSupported,
                Message: $"Installer tipo '{installer.Type}' de '{packageId}' não suporta instalação silenciosa pela API própria.");

        string extension = Path.GetExtension(installerUri.AbsolutePath);
        string workingDirectory = Path.Combine(Path.GetTempPath(), "WinProvision", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(workingDirectory);
        var tempFile = Path.Combine(workingDirectory, SanitizeFileName(packageId) + extension);
        // Só usado quando o instalador vem dentro de um zip (extraído aqui, apagado no final).
        string? extractDir = null;

        try
        {
            onLog?.Report($"[WinProvisionAPI] Baixando {installer.Url}...");
            await DownloadFileWithRetryAsync(installer.Url, tempFile, onLog, onProgress, ct);

            onProgress?.Invoke(new InstallProgressUpdate(InstallProgressPhase.Preparing, Method: WingetMethod.OwnApi));
            onLog?.Report("[WinProvisionAPI] Verificando SHA-256...");
            if (!await VerifySha256Async(tempFile, installer.Sha256, ct))
                return new WinProvisionInstallResult(WinProvisionInstallOutcome.HashMismatch,
                    Message: "Hash do instalador baixado não confere com o manifest.");

            if (installer.IsPortable)
            {
                onLog?.Report("[WinProvisionAPI] Instalando aplicativo portátil e criando atalho na Área de Trabalho...");
                onProgress?.Invoke(new InstallProgressUpdate(InstallProgressPhase.Installing, Method: WingetMethod.OwnApi));
                try
                {
                    var portableResult = await InstallPortablePackageAsync(tempFile, packageId, installer, ct).ConfigureAwait(false);
                    if (portableResult.Outcome == WinProvisionInstallOutcome.Success)
                        await RememberApiInstallAsync(packageId, isPortable: true, CancellationToken.None).ConfigureAwait(false);
                    return portableResult;
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    return new WinProvisionInstallResult(WinProvisionInstallOutcome.InstallProcessFailed,
                        ex.HResult, $"Não foi possível instalar o aplicativo portátil: {ex.Message}");
                }
            }

            // O que de fato roda: o próprio arquivo baixado, ou (quando o pacote é um zip
            // com instalador aninhado) o instalador extraído de dentro dele.
            string runnablePath = tempFile;
            string effectiveType = installer.Type;

            if (installer.IsZipWithNestedInstaller)
            {
                onLog?.Report($"[WinProvisionAPI] Extraindo {installer.NestedInstallerFile} do pacote zip...");
                extractDir = Path.Combine(workingDirectory, "extracted");

                string? extractedPath;
                try
                {
                    extractedPath = ExtractNestedInstaller(tempFile, extractDir, installer.NestedInstallerFile!);
                }
                catch (Exception ex) when (ex is InvalidDataException or IOException or UnauthorizedAccessException)
                {
                    return new WinProvisionInstallResult(WinProvisionInstallOutcome.ExtractionFailed, Message: ex.Message);
                }

                if (extractedPath is null)
                    return new WinProvisionInstallResult(WinProvisionInstallOutcome.ExtractionFailed,
                        Message: $"'{installer.NestedInstallerFile}' não encontrado dentro do zip de '{packageId}'.");

                runnablePath = extractedPath;
                effectiveType = installer.NestedType!;
            }

            onLog?.Report($"[WinProvisionAPI] Instalando formato {effectiveType} (silent: {installer.SilentArgs})...");
            onProgress?.Invoke(new InstallProgressUpdate(InstallProgressPhase.Installing, Method: WingetMethod.OwnApi));
            var exitCode = await RunInstallerAsync(runnablePath, effectiveType, installer.SilentArgs, installer.Scope, ct);

            // Códigos padrão do Windows Installer/Burn mais os códigos de sucesso
            // adicionais declarados no manifesto WinGet.
            var ok = exitCode is 0 or 3010 or 1641 || installer.SuccessCodes.Contains(exitCode);
            if (ok)
                await RememberApiInstallAsync(packageId, isPortable: false, CancellationToken.None).ConfigureAwait(false);
            return ok
                ? new WinProvisionInstallResult(WinProvisionInstallOutcome.Success, exitCode)
                : new WinProvisionInstallResult(WinProvisionInstallOutcome.InstallProcessFailed, exitCode,
                    $"Instalador retornou código {exitCode}.");
        }
        catch (Exception ex) when (ex is HttpRequestException or IOException)
        {
            return new WinProvisionInstallResult(WinProvisionInstallOutcome.DownloadFailed, Message: ex.Message);
        }
        catch (System.ComponentModel.Win32Exception ex) when (ex.NativeErrorCode == 1223)
        {
            return new WinProvisionInstallResult(WinProvisionInstallOutcome.ElevationCanceled,
                ex.NativeErrorCode, "A solicitação de elevação foi cancelada.");
        }
        catch (System.ComponentModel.Win32Exception ex)
        {
            return new WinProvisionInstallResult(WinProvisionInstallOutcome.InstallProcessFailed,
                ex.NativeErrorCode, $"Não foi possível iniciar o instalador: {ex.Message}");
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            return new WinProvisionInstallResult(WinProvisionInstallOutcome.DownloadFailed,
                Message: "A operação excedeu o tempo limite.");
        }
        catch (Exception ex) when (ex is InvalidOperationException or ArgumentException or UnauthorizedAccessException)
        {
            return new WinProvisionInstallResult(WinProvisionInstallOutcome.InstallProcessFailed,
                ex.HResult, $"Não foi possível executar o instalador: {ex.Message}");
        }
        finally
        {
            TryDeleteDirectory(workingDirectory);
        }
    }

    /// <summary>
    /// Extrai só o instalador aninhado (não o zip inteiro) para uma pasta temporária
    /// dedicada. <paramref name="relativePath"/> vem tal como publicado no manifesto
    /// (winget-pkgs usa "\" como separador); comparação por sufixo de caminho normalizado
    /// tolera zips onde o entry vem prefixado por uma pasta-raiz extra.
    /// </summary>
    private static string? ExtractNestedInstaller(string zipPath, string extractDir, string relativePath)
    {
        string normalizedTarget = relativePath.Replace('\\', '/').TrimStart('/');

        using var archive = ZipFile.OpenRead(zipPath);
        var entry = archive.Entries.FirstOrDefault(e =>
                e.FullName.Replace('\\', '/').Equals(normalizedTarget, StringComparison.OrdinalIgnoreCase))
            ?? archive.Entries.FirstOrDefault(e =>
                e.FullName.Replace('\\', '/').EndsWith("/" + normalizedTarget, StringComparison.OrdinalIgnoreCase));

        if (entry is null)
            return null;

        Directory.CreateDirectory(extractDir);
        string destination = Path.Combine(extractDir, Path.GetFileName(entry.FullName));
        entry.ExtractToFile(destination, overwrite: true);
        return destination;
    }

    /// <summary>
    /// Remove um pacote previamente instalado por esta API. Primeiro executa o desinstalador
    /// normal (com retries e detecção de tecnologia); se falhar, usa o Force Uninstall. Os
    /// portáteis só são removidos da pasta gerenciada da WinProvision Store.
    /// </summary>
    public async Task<bool?> UninstallAsync(
        InstalledAppDetail app,
        UninstallerEngineService uninstaller,
        Action<string>? onStatus = null,
        CancellationToken ct = default)
    {
        ApiInstallRecord? record = await FindApiInstallAsync(app.Id, ct).ConfigureAwait(false);
        if (record is null)
            return null; // Não foi instalado pelo método da API própria.

        onStatus?.Invoke("Tentando a desinstalação normal...");
        bool success;
        if (record.IsPortable)
        {
            // ZIP portátil não registra um desinstalador no Windows. A primeira etapa tenta
            // remover só os atalhos do app; a remoção da pasta gerenciada é a etapa agressiva.
            RemovePortableShortcuts(record.PackageId);
            success = TryRemoveManagedPortableDirectory(record.PackageId);
        }
        else
        {
            success = await uninstaller.RunSilentUninstallAsync(app, ct).ConfigureAwait(false);
            if (success)
            {
                onStatus?.Invoke("Limpando arquivos restantes...");
                await uninstaller.PerformAggressiveCleanupAsync(app, ct).ConfigureAwait(false);
            }
            else
            {
                ct.ThrowIfCancellationRequested();
                onStatus?.Invoke("A desinstalação normal falhou; tentando a remoção avançada...");
                success = await uninstaller.ForceUninstallAsync(app, ct).ConfigureAwait(false);
            }
        }

        if (success)
            await ForgetApiInstallAsync(record.PackageId, CancellationToken.None).ConfigureAwait(false);
        return success;
    }

    private async Task RememberApiInstallAsync(string packageId, bool isPortable, CancellationToken ct)
    {
        await _installRecordsLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var records = await ReadInstallRecordsAsync(ct).ConfigureAwait(false);
            records.RemoveAll(record => record.PackageId.Equals(packageId, StringComparison.OrdinalIgnoreCase));
            records.Add(new ApiInstallRecord(packageId, isPortable));
            await WriteInstallRecordsAsync(records, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            Debug.WriteLine($"[WinProvisionAPI] Não foi possível salvar o registro da instalação: {ex.Message}");
        }
        finally
        {
            _installRecordsLock.Release();
        }
    }

    private async Task<ApiInstallRecord?> FindApiInstallAsync(string packageId, CancellationToken ct)
    {
        await _installRecordsLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            return (await ReadInstallRecordsAsync(ct).ConfigureAwait(false))
                .FirstOrDefault(record => record.PackageId.Equals(packageId, StringComparison.OrdinalIgnoreCase));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            Debug.WriteLine($"[WinProvisionAPI] Não foi possível ler o registro de instalações: {ex.Message}");
            return null;
        }
        finally
        {
            _installRecordsLock.Release();
        }
    }

    private async Task ForgetApiInstallAsync(string packageId, CancellationToken ct)
    {
        await _installRecordsLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var records = await ReadInstallRecordsAsync(ct).ConfigureAwait(false);
            records.RemoveAll(record => record.PackageId.Equals(packageId, StringComparison.OrdinalIgnoreCase));
            await WriteInstallRecordsAsync(records, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            Debug.WriteLine($"[WinProvisionAPI] Não foi possível atualizar o registro da instalação: {ex.Message}");
        }
        finally
        {
            _installRecordsLock.Release();
        }
    }

    private async Task<List<ApiInstallRecord>> ReadInstallRecordsAsync(CancellationToken ct)
    {
        if (!File.Exists(_installRecordsPath))
            return [];
        await using var stream = File.OpenRead(_installRecordsPath);
        return await JsonSerializer.DeserializeAsync<List<ApiInstallRecord>>(stream, cancellationToken: ct)
            .ConfigureAwait(false) ?? [];
    }

    private async Task WriteInstallRecordsAsync(List<ApiInstallRecord> records, CancellationToken ct)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_installRecordsPath)!);
        string tempPath = _installRecordsPath + ".tmp";
        await using (var stream = File.Create(tempPath))
            await JsonSerializer.SerializeAsync(stream, records, cancellationToken: ct).ConfigureAwait(false);
        File.Move(tempPath, _installRecordsPath, overwrite: true);
    }

    private static bool TryRemoveManagedPortableDirectory(string packageId)
    {
        string root = Path.GetFullPath(Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "WinProvisionStore", "PortablePackages"));
        string directory = Path.GetFullPath(Path.Combine(root, SanitizeFileName(packageId)));
        if (!directory.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)
            || !Directory.Exists(directory))
            return !Directory.Exists(directory);

        try
        {
            Directory.Delete(directory, recursive: true);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Debug.WriteLine($"[WinProvisionAPI] Não foi possível remover o pacote portátil: {ex.Message}");
            return false;
        }
    }

    private static void RemovePortableShortcuts(string packageId)
    {
        string packageDirectory = Path.GetFullPath(Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "WinProvisionStore", "PortablePackages", SanitizeFileName(packageId))) + Path.DirectorySeparatorChar;
        string desktop = Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);
        if (!Directory.Exists(desktop)) return;

        Type? shellType = Type.GetTypeFromProgID("WScript.Shell");
        if (shellType is null) return;
        object? shell = null;
        try
        {
            shell = Activator.CreateInstance(shellType);
            if (shell is null) return;
            dynamic automation = shell;
            foreach (string shortcutPath in Directory.EnumerateFiles(desktop, "*.lnk"))
            {
                object? shortcutObject = null;
                try
                {
                    shortcutObject = automation.CreateShortcut(shortcutPath);
                    dynamic shortcut = shortcutObject;
                    string target = (string)shortcut.TargetPath;
                    if (Path.GetFullPath(target).StartsWith(packageDirectory, StringComparison.OrdinalIgnoreCase))
                        File.Delete(shortcutPath);
                }
                catch (Exception ex)
                {
                    Debug.WriteLine($"[WinProvisionAPI] Ignorando atalho portátil inválido: {ex.Message}");
                }
                finally
                {
                    if (shortcutObject is not null && Marshal.IsComObject(shortcutObject))
                        Marshal.FinalReleaseComObject(shortcutObject);
                }
            }
        }
        catch (Exception ex) when (ex is System.Runtime.InteropServices.COMException or UnauthorizedAccessException or InvalidOperationException)
        {
            Debug.WriteLine($"[WinProvisionAPI] Não foi possível enumerar atalhos portáteis: {ex.Message}");
        }
        finally
        {
            if (shell is not null && Marshal.IsComObject(shell)) Marshal.FinalReleaseComObject(shell);
        }
    }

    private sealed record ApiInstallRecord(string PackageId, bool IsPortable);

    private static async Task<WinProvisionInstallResult> InstallPortablePackageAsync(
        string downloadedPath,
        string packageId,
        PackageInstaller installer,
        CancellationToken ct)
    {
        string portableRoot = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "WinProvisionStore", "PortablePackages");
        Directory.CreateDirectory(portableRoot);

        string installName = SanitizeFileName(packageId);
        string destination = Path.Combine(portableRoot, installName);
        string staging = Path.Combine(portableRoot, $".{installName}.staging-{Guid.NewGuid():N}");
        string backup = Path.Combine(portableRoot, $".{installName}.backup-{Guid.NewGuid():N}");
        Directory.CreateDirectory(staging);

        bool movedExisting = false;
        try
        {
            string relativeExe;
            if (string.Equals(installer.Type, "zip", StringComparison.OrdinalIgnoreCase))
            {
                if (string.IsNullOrWhiteSpace(installer.NestedInstallerFile))
                    return new WinProvisionInstallResult(WinProvisionInstallOutcome.InvalidManifest,
                        Message: "O pacote portátil ZIP não declara o EXE em NestedInstallerFiles.");

                ZipFile.ExtractToDirectory(downloadedPath, staging, overwriteFiles: true);
                relativeExe = installer.NestedInstallerFile;
            }
            else
            {
                string extension = Path.GetExtension(downloadedPath);
                string fileName = SanitizeFileName(installer.PortableCommandAlias ?? packageId) + extension;
                File.Copy(downloadedPath, Path.Combine(staging, fileName), overwrite: true);
                relativeExe = fileName;
            }

            relativeExe = relativeExe.Replace('\\', Path.DirectorySeparatorChar).Replace('/', Path.DirectorySeparatorChar);
            string stagingRoot = Path.GetFullPath(staging) + Path.DirectorySeparatorChar;
            string executablePath = Path.GetFullPath(Path.Combine(staging, relativeExe));
            if (!executablePath.StartsWith(stagingRoot, StringComparison.OrdinalIgnoreCase)
                || !File.Exists(executablePath)
                || !string.Equals(Path.GetExtension(executablePath), ".exe", StringComparison.OrdinalIgnoreCase))
                return new WinProvisionInstallResult(WinProvisionInstallOutcome.InvalidManifest,
                    Message: "O executável portátil declarado não foi encontrado dentro do ZIP.");

            ct.ThrowIfCancellationRequested();
            if (Directory.Exists(destination))
            {
                Directory.Move(destination, backup);
                movedExisting = true;
            }

            try
            {
                Directory.Move(staging, destination);
            }
            catch
            {
                if (movedExisting && !Directory.Exists(destination))
                    Directory.Move(backup, destination);
                throw;
            }

            string installedExe = Path.Combine(destination, relativeExe);
            string shortcutName = SanitizeFileName(installer.PortableCommandAlias
                ?? Path.GetFileNameWithoutExtension(installedExe));
            string desktop = Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);
            Directory.CreateDirectory(desktop);
            try
            {
                CreateDesktopShortcut(installedExe, shortcutName);
            }
            catch
            {
                TryDeleteDirectory(destination);
                if (movedExisting && Directory.Exists(backup))
                {
                    Directory.Move(backup, destination);
                    movedExisting = false;
                }
                throw;
            }

            if (movedExisting) TryDeleteDirectory(backup);
            return new WinProvisionInstallResult(WinProvisionInstallOutcome.Success, 0,
                $"Aplicativo portátil instalado em '{destination}' e atalho criado na Área de Trabalho.");
        }
        finally
        {
            TryDeleteDirectory(staging);
            if (movedExisting && Directory.Exists(backup) && !Directory.Exists(destination))
                Directory.Move(backup, destination);
        }
    }

    private static void CreateDesktopShortcut(string targetPath, string shortcutName)
    {
        var shellType = Type.GetTypeFromProgID("WScript.Shell", throwOnError: true)
                        ?? throw new InvalidOperationException("O componente de atalhos do Windows não está disponível.");
        object shell = Activator.CreateInstance(shellType)
                       ?? throw new InvalidOperationException("Não foi possível iniciar o componente de atalhos do Windows.");
        object? shortcut = null;
        try
        {
            dynamic automation = shell;
            shortcut = automation.CreateShortcut(Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory), shortcutName + ".lnk"));
            dynamic link = shortcut;
            link.TargetPath = targetPath;
            link.WorkingDirectory = Path.GetDirectoryName(targetPath)!;
            link.IconLocation = targetPath + ",0";
            link.Description = $"Atalho para {shortcutName}";
            link.Save();
        }
        finally
        {
            if (shortcut is not null && Marshal.IsComObject(shortcut)) Marshal.FinalReleaseComObject(shortcut);
            if (Marshal.IsComObject(shell)) Marshal.FinalReleaseComObject(shell);
        }
    }

    private async Task DownloadFileAsync(
        string url,
        string destination,
        Action<InstallProgressUpdate>? onProgress,
        CancellationToken ct)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(DownloadTimeout);
        using var response = await SendWithRetryAsync(url, timeout.Token).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
            throw new HttpRequestException(
                $"O servidor recusou o download (HTTP {(int)response.StatusCode} {response.ReasonPhrase}).",
                null, response.StatusCode);

        long? expectedLength = response.Content.Headers.ContentLength;
        if (expectedLength is <= 0 || expectedLength > MaxInstallerBytes)
            throw new InvalidDataException($"Tamanho de download inválido ou acima do limite ({expectedLength} bytes).");

        await using var input = await response.Content.ReadAsStreamAsync(timeout.Token).ConfigureAwait(false);
        await using var output = new FileStream(destination, FileMode.Create, FileAccess.Write, FileShare.None,
            bufferSize: 128 * 1024, useAsync: true);
        bool canReportPercentage = expectedLength is > 0
            && !response.Content.Headers.ContentEncoding.Any();
        long bytesRead = 0;
        int lastPercent = -1;
        var buffer = new byte[128 * 1024];

        onProgress?.Invoke(new InstallProgressUpdate(
            InstallProgressPhase.Downloading,
            canReportPercentage ? 0 : null,
            WingetMethod.OwnApi));

        while (true)
        {
            int count = await input.ReadAsync(buffer.AsMemory(0, buffer.Length), timeout.Token).ConfigureAwait(false);
            if (count == 0) break;

            bytesRead += count;
            if (bytesRead > MaxInstallerBytes)
                throw new InvalidDataException($"O download excedeu o limite de {MaxInstallerBytes} bytes.");

            await output.WriteAsync(buffer.AsMemory(0, count), timeout.Token).ConfigureAwait(false);

            if (canReportPercentage && expectedLength is > 0)
            {
                int percent = (int)Math.Clamp(
                    Math.Floor(bytesRead * 100d / expectedLength.Value), 0, 99);
                if (percent != lastPercent)
                {
                    lastPercent = percent;
                    onProgress?.Invoke(new InstallProgressUpdate(
                        InstallProgressPhase.Downloading, percent, WingetMethod.OwnApi));
                }
            }
        }

        await output.FlushAsync(timeout.Token).ConfigureAwait(false);

        if (output.Length == 0 || output.Length > MaxInstallerBytes)
            throw new InvalidDataException($"O download terminou com tamanho inválido ({output.Length} bytes).");
        onProgress?.Invoke(new InstallProgressUpdate(
            InstallProgressPhase.Downloading, 100, WingetMethod.OwnApi));
        // O Content-Length pode descrever a representação comprimida/intermediária do CDN.
        // A integridade real do arquivo é confirmada pelo SHA-256 do manifesto.
    }

    private async Task DownloadFileWithRetryAsync(
        string url,
        string destination,
        IProgress<string>? onLog,
        Action<InstallProgressUpdate>? onProgress,
        CancellationToken ct)
    {
        for (int attempt = 1; ; attempt++)
        {
            try
            {
                await DownloadFileAsync(url, destination, onProgress, ct);
                return;
            }
            catch (Exception ex) when (attempt < 4
                && (ex is HttpRequestException or IOException or InvalidDataException
                    || ex is OperationCanceledException && !ct.IsCancellationRequested)
                && !ct.IsCancellationRequested)
            {
                TryDeleteFile(destination);
                onLog?.Report($"Falha temporária no download; nova tentativa {attempt + 1} de 4...");
                await Task.Delay(TimeSpan.FromMilliseconds(700 * Math.Pow(2, attempt - 1)), ct);
            }
        }
    }

    private static async Task<bool> VerifySha256Async(string filePath, string expectedHex, CancellationToken ct)
    {
        await using var stream = File.OpenRead(filePath);
        var hash = await SHA256.HashDataAsync(stream, ct);
        var actualHex = Convert.ToHexString(hash);
        return string.Equals(actualHex, expectedHex, StringComparison.OrdinalIgnoreCase);
    }

    private static async Task<int> RunInstallerAsync(
        string filePath, string installerType, string silentArgs, string scope, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var tcs = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
        bool needsElevation = string.Equals(scope, "machine", StringComparison.OrdinalIgnoreCase)
                              || string.Equals(scope, "system", StringComparison.OrdinalIgnoreCase);
        bool elevate = needsElevation && !IsRunningElevated();
        var command = WinProvisionInstallerCommandBuilder.Build(filePath, installerType, silentArgs);
        var psi = new System.Diagnostics.ProcessStartInfo
        {
            FileName = command.FileName,
            Arguments = command.Arguments,
            UseShellExecute = elevate,
            CreateNoWindow = !elevate,
            Verb = elevate ? "runas" : string.Empty
        };

        var process = new System.Diagnostics.Process { StartInfo = psi, EnableRaisingEvents = true };
        process.Exited += (_, _) =>
        {
            tcs.TrySetResult(process.ExitCode);
            process.Dispose();
        };

        process.Start();
        using var registration = ct.Register(() =>
        {
            try { if (!process.HasExited) process.Kill(entireProcessTree: true); }
            catch { /* processo já pode ter saído */ }
            tcs.TrySetCanceled(ct);
        });
        return await tcs.Task.ConfigureAwait(false);
    }

    private static bool IsRunningElevated()
    {
        using var identity = System.Security.Principal.WindowsIdentity.GetCurrent();
        return new System.Security.Principal.WindowsPrincipal(identity)
            .IsInRole(System.Security.Principal.WindowsBuiltInRole.Administrator);
    }

    private static bool IsValidSha256(string? value) =>
        value is { Length: 64 } && value.All(Uri.IsHexDigit);

    private static string SanitizeFileName(string value)
    {
        var invalid = Path.GetInvalidFileNameChars();
        string safe = new(value.Select(character => invalid.Contains(character) ? '_' : character).ToArray());
        safe = safe.Trim().TrimEnd('.');
        if (safe.Length > 72) safe = safe[..72];
        return string.IsNullOrWhiteSpace(safe) ? "WinProvisionPackage" : safe;
    }

    private static void TryDeleteFile(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); } catch { /* best-effort */ }
    }

    private static void TryDeleteDirectory(string? path)
    {
        if (path is null) return;
        try { if (Directory.Exists(path)) Directory.Delete(path, recursive: true); } catch { /* best-effort */ }
    }
}
