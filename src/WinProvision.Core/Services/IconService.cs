using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Net.Http;
using System.Runtime.Versioning;
using System.Text.Json;
using System.Threading;
using WinProvision.Core.Models;
using WinProvision.Core.Services.IconSync;

namespace WinProvision.Core.Services;

public class IconService
{
    // Base de ícones da comunidade publicada pelo workflow sync-icon-databases.yml
    // (WinGet oficial + Cloudflare + Winstall aprovado manualmente + package-icons
    // externo + UniGetUI — ver WinProvision.Core.Services.IconSync.IconSyncPipeline).
    // Quando o Id do app não está nela, cai no ícone genérico local (ver
    // ResolveIconUrl).
    private const string IconsDatabaseUrl =
        "https://raw.githubusercontent.com/GabrielSilvaTI/WinProvision-Store/icons-cdn/icons-database.json";

    private readonly string _iconCacheFolder;
    private readonly string _iconsDatabaseCachePath;
    private readonly HttpClient _httpClient;

    private readonly object _loadLock = new();
    private Task? _loadTask;
    private Dictionary<string, string> _iconsDatabase = [];

    public IconService(HttpClient? httpClient = null)
    {
        _httpClient = httpClient ?? new HttpClient { Timeout = TimeSpan.FromSeconds(15) };

        string appDataFolder = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "WinProvisionStore"
        );

        _iconCacheFolder = Path.Combine(appDataFolder, "Cache", "Icons");
        _iconsDatabaseCachePath = Path.Combine(appDataFolder, "icons-database.json");

        Directory.CreateDirectory(_iconCacheFolder);
    }

    /// <summary>
    /// Garante que a base de ícones da comunidade esteja carregada em memória (do
    /// cache local, com refresh em segundo plano, ou baixada na primeira execução).
    /// Chamadas concorrentes reaproveitam a mesma Task em andamento em vez de disparar
    /// leituras/downloads duplicados do mesmo arquivo — chame antes de exibir a lista
    /// de apps pela primeira vez (ex.: de dentro de StoreService.LoadCatalogAsync).
    /// </summary>
    public Task EnsureIconsDatabaseLoadedAsync(CancellationToken cancellationToken = default)
    {
        lock (_loadLock)
        {
            _loadTask ??= LoadIconsDatabaseAsync(cancellationToken);
            return _loadTask;
        }
    }

    /// <summary>
    /// Limpa o cache em memória e em disco da base de ícones da comunidade.
    /// </summary>
    public void ClearCache()
    {
        _iconsDatabase = [];
        lock (_loadLock)
        {
            _loadTask = null;
        }

        try
        {
            if (File.Exists(_iconsDatabaseCachePath))
                File.Delete(_iconsDatabaseCachePath);
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[IconService] Não foi possível remover o cache da base de ícones: {ex.Message}");
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
    /// Ordem: 1) Base de ícones da comunidade sincronizada 2) Ícone genérico local
    /// (embutido no .exe). Se a base comunitária ainda não terminou de carregar (ou
    /// EnsureIconsDatabaseLoadedAsync nunca foi chamado), o passo 1 é pulado — nunca
    /// bloqueia esperando o carregamento.
    ///
    /// Antes existia um passo anterior a este, o lote curado embutido no .exe
    /// (CuratedIconCatalog, ~30 apps mais visíveis). Removido: o bucket Cloudflare
    /// já cobre esses mesmos apps e mais, então manter os dois só duplicava ícone
    /// sem necessidade e exigia recompilar o app pra corrigir qualquer um deles.
    ///
    /// Antes existia um passo que caía no favicon do site oficial
    /// (google.com/s2/favicons). Removido: para apps cujo Homepage aponta pra um
    /// repositório (github.com, gitlab.com etc.), o favicon retornado é o do próprio
    /// site, não do projeto — resultado prático era o logo do GitHub aparecendo como
    /// "ícone do app" em várias entradas. O genérico embutido é preferível a isso.
    /// </summary>
    public string ResolveIconUrl(AppEntry app)
    {
        string normalizedId = IconIdNormalizer.Normalize(app.Id);
        if (normalizedId.Length > 0 && _iconsDatabase.TryGetValue(normalizedId, out var communityIcon))
        {
            return communityIcon;
        }

        return "pack://application:,,,/Assets/Icons/default_app.png";
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

    private async Task LoadIconsDatabaseAsync(CancellationToken cancellationToken)
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
        if (!File.Exists(_iconsDatabaseCachePath)) return false;

        try
        {
            string json = await File.ReadAllTextAsync(_iconsDatabaseCachePath, cancellationToken);
            _iconsDatabase = JsonSerializer.Deserialize<Dictionary<string, string>>(json) ?? [];
            return true;
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[IconService] Cache local de ícones corrompido, baixando novamente: {ex.Message}");
            return false;
        }
    }

    private async Task RefreshInBackgroundAsync(CancellationToken cancellationToken)
    {
        try
        {
            string remoteJson = await _httpClient.GetStringAsync(IconsDatabaseUrl, cancellationToken);
            var parsed = JsonSerializer.Deserialize<Dictionary<string, string>>(remoteJson) ?? [];

            if (parsed.Count > 0)
            {
                _iconsDatabase = parsed;

                Directory.CreateDirectory(Path.GetDirectoryName(_iconsDatabaseCachePath)!);
                await File.WriteAllTextAsync(_iconsDatabaseCachePath, remoteJson, cancellationToken);
            }
        }
        catch (Exception ex)
        {
            // Sem base remota disponível (offline, primeira execução antes do primeiro
            // workflow rodar, branch icons-cdn ainda não publicada etc.) — ResolveIconUrl
            // já cai pro genérico local sozinho, não precisa propagar o erro.
            Debug.WriteLine($"[IconService] Falha ao baixar base de ícones da comunidade: {ex.Message}");
        }
    }
}
