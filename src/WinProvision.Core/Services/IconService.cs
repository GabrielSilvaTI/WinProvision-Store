using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Runtime.Versioning;
using WinProvision.Core.Models;

namespace WinProvision.Core.Services;

public class IconService
{
    private const string CatalogAppsBaseUrl = "https://pub-166b41912a994dbe86583ba10596d673.r2.dev/Store/Catalog/";
    // Exposto publicamente para compartilhar o fallback quando o app não tem
    // ícone na mídia V2 nem URL fornecida pela Microsoft Store.
    public const string DefaultIconPackUri = "pack://application:,,,/Assets/Icons/default_app.png";

    private readonly string _iconCacheFolder;
    private readonly string _legacyIconManifestCachePath;
    public IconService()
    {
        string appDataFolder = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "WinProvisionStore"
        );

        _iconCacheFolder = Path.Combine(appDataFolder, "Cache", "Icons");
        _legacyIconManifestCachePath = Path.Combine(appDataFolder, "icon-manifest.json");
        Directory.CreateDirectory(_iconCacheFolder);
    }

    /// <summary>
    /// Limpa o cache local dos ícones extraídos de aplicativos instalados.
    /// </summary>
    public void ClearCache()
    {
        try
        {
            if (File.Exists(_legacyIconManifestCachePath))
                File.Delete(_legacyIconManifestCachePath);
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
    /// Para o catálogo WinGet, usa a mídia declarada no próprio registro V2. Para a
    /// Microsoft Store, usa o ícone fornecido por ela. Sem mídia associada, retorna o
    /// ícone genérico local.
    /// </summary>
    public string ResolveIconUrl(AppEntry app)
    {
        if (TryGetCatalogMediaUrl(app, app.Media?.Icon, app.Media?.IconSha256) is { } catalogIcon)
            return catalogIcon;

        // Apps "msstore" usam o ícone fornecido pela própria Microsoft Store.
        if (!string.IsNullOrWhiteSpace(app.StoreIconUrl))
        {
            return app.StoreIconUrl;
        }

        return DefaultIconPackUri;
    }

    private static string? TryGetCatalogMediaUrl(AppEntry app, string? mediaPath, string? sha256)
    {
        if (string.IsNullOrWhiteSpace(mediaPath)
            || string.IsNullOrWhiteSpace(app.CatalogDetailPath)
            || !mediaPath.StartsWith("media/", StringComparison.Ordinal)
            || Path.IsPathRooted(mediaPath)
            || mediaPath.Replace('\\', '/').Split('/').Any(segment => segment is "" or "." or ".."))
            return null;

        string detailPath = app.CatalogDetailPath.Replace('\\', '/');
        int lastSlash = detailPath.LastIndexOf('/');
        if (lastSlash < 0)
            return null;

        string encodedMedia = string.Join('/', mediaPath.Split('/').Select(Uri.EscapeDataString));
        string url = $"{CatalogAppsBaseUrl}{detailPath[..(lastSlash + 1)]}{encodedMedia}";
        return !string.IsNullOrWhiteSpace(sha256) && sha256.Length == 64 && sha256.All(Uri.IsHexDigit)
            ? $"{url}?v={sha256[..16]}"
            : url;
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
}
