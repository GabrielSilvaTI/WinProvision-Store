using System;
using System.Collections.Concurrent;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media.Imaging;
using SixLabors.ImageSharp.Formats.Png;
using SixImage = SixLabors.ImageSharp.Image;

namespace WinProvision.Store.Converters;

/// <summary>
/// Carregamento assíncrono de ícones para listas/grades virtualizadas.
/// Mantém cache em memória, cache em disco com expiração e deduplicação de
/// downloads concorrentes. Nunca bloqueia a thread da UI.
/// </summary>
public static class AsyncImage
{
    private const string FallbackPackUri = "pack://application:,,,/Assets/Icons/default_app.png";
    private const int MaxMemoryEntries = 384;
    private static readonly TimeSpan DiskCacheTtl = TimeSpan.FromDays(14);
    private static readonly HttpClient Client = new() { Timeout = TimeSpan.FromSeconds(10) };
    private static readonly ConcurrentDictionary<string, BitmapImage> MemoryCache = new(StringComparer.OrdinalIgnoreCase);
    private static readonly ConcurrentDictionary<string, Task<BitmapImage?>> InFlight = new(StringComparer.OrdinalIgnoreCase);
    private static readonly object CacheTrimLock = new();
    private static readonly string DiskCacheFolder = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "WinProvisionStore", "Cache", "RenderedIcons");

    public static readonly DependencyProperty SourceUrlProperty =
        DependencyProperty.RegisterAttached(
            "SourceUrl",
            typeof(string),
            typeof(AsyncImage),
            new PropertyMetadata(null, OnSourceUrlChanged));

    public static string? GetSourceUrl(DependencyObject obj) => (string?)obj.GetValue(SourceUrlProperty);
    public static void SetSourceUrl(DependencyObject obj, string? value) => obj.SetValue(SourceUrlProperty, value);

    public static void ClearCache()
    {
        MemoryCache.Clear();
        InFlight.Clear();
        try
        {
            if (Directory.Exists(DiskCacheFolder))
                Directory.Delete(DiskCacheFolder, recursive: true);
        }
        catch
        {
            // Limpeza de cache é best-effort; nunca deve derrubar a UI.
        }
    }

    private static async void OnSourceUrlChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not Image image)
            return;

        string? url = e.NewValue as string;
        image.Source = null;

        if (string.IsNullOrWhiteSpace(url))
            return;

        if (MemoryCache.TryGetValue(url, out BitmapImage? cached))
        {
            image.Source = cached;
            return;
        }

        // Recurso embutido no próprio .exe (ver IconService.ResolveIconUrl - lote
        // curado + fallback genérico, ambos em WinProvision.Store/Assets/Icons):
        // carregado direto do assembly, sem passar pelo HttpClient/disco abaixo, que
        // só sabem buscar via rede e nunca souberam resolver esse esquema. Sem este
        // desvio, qualquer pack:// simplesmente falhava em silêncio (catch genérico
        // logo abaixo) e a imagem ficava em branco.
        if (url.StartsWith("pack://", StringComparison.OrdinalIgnoreCase))
        {
            BitmapImage? packBitmap = LoadPackResource(url);
            if (packBitmap is not null)
            {
                MemoryCache[url] = packBitmap;
                if (string.Equals(GetSourceUrl(image), url, StringComparison.OrdinalIgnoreCase))
                    image.Source = packBitmap;
            }

            return;
        }

        try
        {
            BitmapImage? bitmap = await InFlight.GetOrAdd(url, static key => LoadAsync(key));

            // Download falhou (404, timeout, host fora do ar) ou o conteúdo baixado
            // não decodificou como imagem — antes ficava em branco (nada no XAML
            // definia um Source padrão). Cai no genérico embutido no .exe.
            bitmap ??= LoadPackResource(FallbackPackUri);
            if (bitmap is null)
                return;

            MemoryCache[url] = bitmap;
            TrimMemoryCache();

            // A lista pode ter reciclado o Image enquanto o download ocorria.
            if (string.Equals(GetSourceUrl(image), url, StringComparison.OrdinalIgnoreCase))
                image.Source = bitmap;
        }
        catch
        {
            if (string.Equals(GetSourceUrl(image), url, StringComparison.OrdinalIgnoreCase))
                image.Source = LoadPackResource(FallbackPackUri);
        }
        finally
        {
            InFlight.TryRemove(url, out _);
        }
    }

    /// <summary>
    /// Carrega um pack:// (recurso embutido no assembly) diretamente — síncrono e sem
    /// cache em disco, já que não faz sentido cachear algo que já está no .exe.
    /// </summary>
    private static BitmapImage? LoadPackResource(string url)
    {
        try
        {
            var bitmap = new BitmapImage();
            bitmap.BeginInit();
            bitmap.CacheOption = BitmapCacheOption.OnLoad;
            bitmap.UriSource = new Uri(url, UriKind.Absolute);
            bitmap.EndInit();
            bitmap.Freeze();
            return bitmap;
        }
        catch
        {
            // Recurso não encontrado no assembly (ex.: nome de arquivo errado) - cai
            // em silêncio, mesmo padrão de falha do LoadAsync abaixo.
            return null;
        }
    }

    private static async Task<BitmapImage?> LoadAsync(string url)
    {
        try
        {
            Directory.CreateDirectory(DiskCacheFolder);
            string path = GetDiskPath(url);

            if (File.Exists(path) && DateTime.UtcNow - File.GetLastWriteTimeUtc(path) < DiskCacheTtl)
            {
                byte[] cachedBytes = await File.ReadAllBytesAsync(path);
                return await Task.Run(() => DecodeToBitmap(cachedBytes));
            }

            byte[] bytes = await Client.GetByteArrayAsync(url);
            try
            {
                await File.WriteAllBytesAsync(path, bytes);
            }
            catch
            {
                // A imagem ainda pode ser exibida mesmo se o cache de disco falhar.
            }

            return await Task.Run(() => DecodeToBitmap(bytes));
        }
        catch
        {
            return null;
        }
    }

    private static string GetDiskPath(string url)
    {
        byte[] hash = SHA256.HashData(Encoding.UTF8.GetBytes(url));
        return Path.Combine(DiskCacheFolder, Convert.ToHexString(hash).ToLowerInvariant() + ".img");
    }

    private static void TrimMemoryCache()
    {
        if (MemoryCache.Count <= MaxMemoryEntries)
            return;

        lock (CacheTrimLock)
        {
            if (MemoryCache.Count <= MaxMemoryEntries)
                return;

            foreach (string key in MemoryCache.Keys.Take(Math.Max(32, MemoryCache.Count - MaxMemoryEntries)))
                MemoryCache.TryRemove(key, out _);
        }
    }

    private static BitmapImage? DecodeToBitmap(byte[] bytes)
    {
        try
        {
            using SixImage image = SixImage.Load(bytes);
            using var pngStream = new MemoryStream();
            image.Save(pngStream, new PngEncoder());
            pngStream.Position = 0;

            var bitmap = new BitmapImage();
            bitmap.BeginInit();
            bitmap.CacheOption = BitmapCacheOption.OnLoad;
            bitmap.StreamSource = pngStream;
            bitmap.EndInit();
            bitmap.Freeze();
            return bitmap;
        }
        catch
        {
            return null;
        }
    }
}
