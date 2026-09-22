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
using System.IO;
using System.Linq;
using System.IO.Compression;
using System.Net.Http;
using System.Net.Http.Json;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;

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
    [JsonPropertyName("scope")] public string Scope { get; set; } = "";         // user | machine
    [JsonPropertyName("url")] public string Url { get; set; } = "";
    [JsonPropertyName("sha256")] public string Sha256 { get; set; } = "";
    [JsonPropertyName("silentArgs")] public string SilentArgs { get; set; } = "";
    [JsonPropertyName("silentSource")] public string SilentSource { get; set; } = "";
    [JsonPropertyName("silentSupported")] public bool SilentSupported { get; set; }
    [JsonPropertyName("productCode")] public string? ProductCode { get; set; }

    /// <summary>Atalho: true quando este instalador é um zip com instalador aninhado resolvido pela API.</summary>
    [JsonIgnore]
    public bool IsZipWithNestedInstaller =>
        string.Equals(Type, "zip", StringComparison.OrdinalIgnoreCase) && !string.IsNullOrEmpty(NestedInstallerFile);
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
    InstallProcessFailed
}

public sealed record WinProvisionInstallResult(
    WinProvisionInstallOutcome Outcome,
    int? ExitCode = null,
    string? Message = null);

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

    private PackageIndex? _indexCache;
    private DateTimeOffset _indexCachedAt;
    private static readonly TimeSpan IndexTtl = TimeSpan.FromMinutes(15);

    public WinProvisionApiService(HttpClient? http = null, string? cacheDir = null)
    {
        _http = http ?? new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
        _cacheDir = cacheDir ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "WinProvisionStore", "ApiCache");
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

            var index = await _http.GetFromJsonAsync<PackageIndex>(BaseUrl + "index.json", ct)
                        ?? throw new InvalidOperationException("index.json vazio ou inválido.");
            _indexCache = index;
            _indexCachedAt = DateTimeOffset.UtcNow;
            return index;
        }
        finally
        {
            _indexLock.Release();
        }
    }

    public async Task<PackageManifest?> GetPackageAsync(string id, CancellationToken ct = default)
    {
        try
        {
            return await _http.GetFromJsonAsync<PackageManifest>(
                $"{BaseUrl}packages/{Uri.EscapeDataString(id)}.json", ct);
        }
        catch (HttpRequestException)
        {
            return null; // 404 ou similar -> pacote não existe na API própria
        }
    }

    /// <summary>
    /// Escolhe o melhor installer para a arquitetura atual da máquina, com fallback pra x86
    /// (compatível via WOW64) quando não existe build nativa.
    /// </summary>
    public static PackageInstaller? PickInstaller(PackageManifest manifest, Architecture? preferred = null)
    {
        var current = preferred ?? RuntimeInformation.OSArchitecture;
        var order = current switch
        {
            Architecture.Arm64 => new[] { "arm64", "x64", "x86" },
            Architecture.X64 => new[] { "x64", "x86" },
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
                .OrderByDescending(i => i.SilentSupported)
                .ToList();
            if (candidates.Count > 0) return candidates[0];
        }
        return manifest.Installers
            .OrderByDescending(i => i.SilentSupported)
            .FirstOrDefault();
    }

    /// <summary>
    /// Baixa e instala silenciosamente. Ponto de entrada único a ser chamado
    /// pelos três gatilhos: falha da COM, falha de reelevação, execução em SYSTEM.
    /// </summary>
    public async Task<WinProvisionInstallResult> TryInstallAsync(
        string packageId,
        IProgress<string>? onLog = null,
        CancellationToken ct = default)
    {
        onLog?.Report($"[WinProvisionAPI] Consultando manifest de '{packageId}'...");
        var manifest = await GetPackageAsync(packageId, ct);
        if (manifest is null)
            return new WinProvisionInstallResult(WinProvisionInstallOutcome.PackageNotFound,
                Message: $"Pacote '{packageId}' não encontrado na API própria.");

        var installer = PickInstaller(manifest);
        if (installer is null)
            return new WinProvisionInstallResult(WinProvisionInstallOutcome.NoCompatibleInstaller,
                Message: $"Nenhum installer compatível para '{packageId}'.");

        // O catálogo (InstallerApiExporter) já marca silentSupported=false pra tipos que
        // não sabe instalar sem interação (msix, appx, portable, exe sem switch declarado
        // no manifesto, ou um zip cujo NestedInstallerType/NestedInstallerFiles não foi
        // resolvido). Baixar e tentar EXECUTAR esse arquivo sempre falha nesses casos —
        // melhor falhar aqui, ANTES do download, e deixar o nível 3 (winget.exe) cuidar
        // deles, já que o próprio winget sabe lidar com zip/portable/msix nativamente.
        if (!installer.SilentSupported)
            return new WinProvisionInstallResult(WinProvisionInstallOutcome.SilentInstallNotSupported,
                Message: $"Installer tipo '{installer.Type}' de '{packageId}' não suporta instalação silenciosa pela API própria.");

        var tempFile = Path.Combine(Path.GetTempPath(),
            $"winprovision_{packageId}_{Guid.NewGuid():N}{Path.GetExtension(installer.Url)}");
        // Só usado quando o instalador vem dentro de um zip (extraído aqui, apagado no final).
        string? extractDir = null;

        try
        {
            onLog?.Report($"[WinProvisionAPI] Baixando {installer.Url}...");
            await DownloadFileAsync(installer.Url, tempFile, ct);

            if (!string.IsNullOrEmpty(installer.Sha256))
            {
                onLog?.Report("[WinProvisionAPI] Verificando SHA-256...");
                if (!await VerifySha256Async(tempFile, installer.Sha256, ct))
                    return new WinProvisionInstallResult(WinProvisionInstallOutcome.HashMismatch,
                        Message: "Hash do instalador baixado não confere com o manifest.");
            }

            // O que de fato roda: o próprio arquivo baixado, ou (quando o pacote é um zip
            // com instalador aninhado) o instalador extraído de dentro dele.
            string runnablePath = tempFile;

            if (installer.IsZipWithNestedInstaller)
            {
                onLog?.Report($"[WinProvisionAPI] Extraindo {installer.NestedInstallerFile} do pacote zip...");
                extractDir = Path.Combine(Path.GetTempPath(), $"winprovision_{packageId}_{Guid.NewGuid():N}");

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
            }

            onLog?.Report($"[WinProvisionAPI] Instalando (silent: {installer.SilentArgs})...");
            var exitCode = await RunInstallerAsync(runnablePath, installer.SilentArgs, ct);

            // A maioria dos instaladores silenciosos usa 0 = sucesso;
            // 3010 = sucesso com reboot pendente (comum em MSI/Burn).
            var ok = exitCode is 0 or 3010;
            return ok
                ? new WinProvisionInstallResult(WinProvisionInstallOutcome.Success, exitCode)
                : new WinProvisionInstallResult(WinProvisionInstallOutcome.InstallProcessFailed, exitCode,
                    $"Instalador retornou código {exitCode}.");
        }
        catch (Exception ex) when (ex is HttpRequestException or IOException)
        {
            return new WinProvisionInstallResult(WinProvisionInstallOutcome.DownloadFailed, Message: ex.Message);
        }
        finally
        {
            TryDeleteFile(tempFile);
            TryDeleteDirectory(extractDir);
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

    private async Task DownloadFileAsync(string url, string destination, CancellationToken ct)
    {
        await using var stream = await _http.GetStreamAsync(url, ct);
        await using var file = File.Create(destination);
        await stream.CopyToAsync(file, ct);
    }

    private static async Task<bool> VerifySha256Async(string filePath, string expectedHex, CancellationToken ct)
    {
        await using var stream = File.OpenRead(filePath);
        var hash = await SHA256.HashDataAsync(stream, ct);
        var actualHex = Convert.ToHexString(hash);
        return string.Equals(actualHex, expectedHex, StringComparison.OrdinalIgnoreCase);
    }

    private static Task<int> RunInstallerAsync(string filePath, string silentArgs, CancellationToken ct)
    {
        var tcs = new TaskCompletionSource<int>();
        var psi = new System.Diagnostics.ProcessStartInfo
        {
            FileName = filePath,
            Arguments = silentArgs,
            UseShellExecute = false,
            CreateNoWindow = true,
        };

        var process = new System.Diagnostics.Process { StartInfo = psi, EnableRaisingEvents = true };
        process.Exited += (_, _) =>
        {
            tcs.TrySetResult(process.ExitCode);
            process.Dispose();
        };

        ct.Register(() =>
        {
            try { if (!process.HasExited) process.Kill(entireProcessTree: true); }
            catch { /* processo já pode ter saído */ }
            tcs.TrySetCanceled(ct);
        });

        process.Start();
        return tcs.Task;
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
