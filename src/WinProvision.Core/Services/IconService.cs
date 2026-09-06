using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Net.Http;
using System.Runtime.Versioning;
using System.Text.Json;
using System.Threading;
using WinProvision.Core.Models;

namespace WinProvision.Core.Services;

public class IconService
{
    // Manifesto único de ícones, publicado no bucket Cloudflare R2 (mesmo bucket
    // que hospeda os arquivos de imagem em Store/Icon_Database/{id}.{ext}) pelo
    // build_kv_mapping.py + upload_manifest.py. Formato: {"vendor.appname": "url"},
    // com a chave sendo o PackageIdentifier do winget em minúsculo — igual ao
    // AppEntry.Id, então a busca é um ToLowerInvariant() direto, sem normalização
    // "fuzzy" (essa era necessária quando havia várias fontes heterogêneas de
    // ícone; agora só existe uma fonte, com id já no formato certo).
    //
    // Cloudflare é a única fonte remota de ícones do Store. Quando o Id do app
    // não está no manifesto, cai no ícone genérico local (ver ResolveIconUrl).
    private const string IconManifestUrl =
        "https://pub-166b41912a994dbe86583ba10596d673.r2.dev/Store/icon-manifest.json";

    // Exposto publicamente para quem precisa distinguir "app com ícone real"
    // de "app caiu no genérico" sem duplicar essa string (ex.: filtro da Visão
    // Geral em HomePage.xaml.cs).
    public const string DefaultIconPackUri = "pack://application:,,,/Assets/Icons/default_app.png";

    private readonly string _iconCacheFolder;
    private readonly string _iconManifestCachePath;
    private readonly HttpClient _httpClient;

    private readonly object _loadLock = new();
    private Task? _loadTask;
    private Dictionary<string, string> _iconManifest = [];

    public IconService(HttpClient? httpClient = null)
    {
        _httpClient = httpClient ?? new HttpClient { Timeout = TimeSpan.FromSeconds(15) };

        string appDataFolder = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "WinProvisionStore"
        );

        _iconCacheFolder = Path.Combine(appDataFolder, "Cache", "Icons");
        _iconManifestCachePath = Path.Combine(appDataFolder, "icon-manifest.json");

        Directory.CreateDirectory(_iconCacheFolder);
    }

    /// <summary>
    /// Garante que o manifesto de ícones do R2 esteja carregado em memória (do
    /// cache local, com refresh em segundo plano, ou baixado na primeira execução).
    /// Chamadas concorrentes reaproveitam a mesma Task em andamento em vez de disparar
    /// leituras/downloads duplicados do mesmo arquivo — chame antes de exibir a lista
    /// de apps pela primeira vez (ex.: de dentro de StoreService.LoadCatalogAsync).
    /// </summary>
    public Task EnsureIconsDatabaseLoadedAsync(CancellationToken cancellationToken = default)
    {
        lock (_loadLock)
        {
            _loadTask ??= LoadIconManifestAsync(cancellationToken);
            return _loadTask;
        }
    }

    /// <summary>
    /// Limpa o cache em memória e em disco do manifesto de ícones.
    /// </summary>
    public void ClearCache()
    {
        _iconManifest = [];
        lock (_loadLock)
        {
            _loadTask = null;
        }

        try
        {
            if (File.Exists(_iconManifestCachePath))
                File.Delete(_iconManifestCachePath);
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[IconService] Não foi possível remover o cache do manifesto de ícones: {ex.Message}");
        }

        try
        {
            foreach (string file in Directory.EnumerateFiles(_iconCacheFolder, "*.png"))
                File.Delete(file);
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[IconService] Não foi possível limpar todos os ícones locais: {ex.Message}");
        }
    }

    /// <summary>
    /// Retorna a melhor URL ou caminho local de ícone para o aplicativo.
    /// Ordem: 1) Manifesto de ícones do Cloudflare R2 2) Ícone genérico local
    /// (embutido no .exe). Se o manifesto ainda não terminou de carregar (ou
    /// EnsureIconsDatabaseLoadedAsync nunca foi chamado), o passo 1 é pulado — nunca
    /// bloqueia esperando o carregamento.
    /// </summary>
    public string ResolveIconUrl(AppEntry app)
    {
        string normalizedId = app.Id.Trim().ToLowerInvariant();
        if (normalizedId.Length > 0 && _iconManifest.TryGetValue(normalizedId, out var icon))
        {
            return icon;
        }

        return DefaultIconPackUri;
    }

    /// <summary>
    /// Extrai o ícone real do arquivo .exe instalado no Windows (pós-instalação).
    /// </summary>
    [SupportedOSPlatform("windows")]
    public string? ExtractIconFromExe(string exePath, string appId)
    {
        if (!File.Exists(exePath)) return null;

        try
        {
            string destinationPath = Path.Combine(_iconCacheFolder, $"{appId}.png");

            if (File.Exists(destinationPath))
                return destinationPath;

            using Icon? sysIcon = Icon.ExtractAssociatedIcon(exePath);
            if (sysIcon != null)
            {
                using Bitmap bitmap = sysIcon.ToBitmap();
                bitmap.Save(destinationPath, System.Drawing.Imaging.ImageFormat.Png);
                return destinationPath;
            }
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[IconService] Falha ao extrair ícone de '{exePath}': {ex.Message}");
        }

        return null;
    }

    private async Task LoadIconManifestAsync(CancellationToken cancellationToken)
    {
        if (await TryLoadFromLocalCacheAsync(cancellationToken))
        {
            // Cache local já deixou algo utilizável em memória - atualiza em segundo
            // plano sem fazer quem chamou esperar a rede.
            _ = RefreshInBackgroundAsync(cancellationToken);
            return;
        }

        await RefreshInBackgroundAsync(cancellationToken);
    }

    private async Task<bool> TryLoadFromLocalCacheAsync(CancellationToken cancellationToken)
    {
        if (!File.Exists(_iconManifestCachePath)) return false;

        try
        {
            string json = await File.ReadAllTextAsync(_iconManifestCachePath, cancellationToken);
            _iconManifest = JsonSerializer.Deserialize<Dictionary<string, string>>(json, WinProvisionJsonOptions.Compact) ?? [];
            return true;
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[IconService] Cache local do manifesto de ícones corrompido, baixando novamente: {ex.Message}");
            return false;
        }
    }

    private async Task RefreshInBackgroundAsync(CancellationToken cancellationToken)
    {
        try
        {
            string remoteJson = await _httpClient.GetStringAsync(IconManifestUrl, cancellationToken);
            var parsed = JsonSerializer.Deserialize<Dictionary<string, string>>(remoteJson, WinProvisionJsonOptions.Compact) ?? [];

            if (parsed.Count > 0)
            {
                _iconManifest = parsed;

                Directory.CreateDirectory(Path.GetDirectoryName(_iconManifestCachePath)!);
                await File.WriteAllTextAsync(_iconManifestCachePath, remoteJson, cancellationToken);
            }
        }
        catch (Exception ex)
        {
            // Sem manifesto remoto disponível (offline, primeira execução antes do
            // primeiro upload rodar etc.) — ResolveIconUrl já cai pro genérico local
            // sozinho, não precisa propagar o erro.
            Debug.WriteLine($"[IconService] Falha ao baixar manifesto de ícones do R2: {ex.Message}");
        }
    }
}
