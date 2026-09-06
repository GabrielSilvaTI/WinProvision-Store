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

namespace WinProvision.Store.Converters;

/// <summary>
/// Carregamento assíncrono de ícones para listas/grades virtualizadas.
/// Mantém cache em memória, cache em disco com expiração e deduplicação de
/// downloads concorrentes. Nunca bloqueia a thread da UI.
/// </summary>
public static class AsyncImage
{
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

    // Só true quando um bitmap de verdade está em Image.Source. Falso para URL vazia,
    // para o pack:// genérico (IconService.DefaultIconPackUri, que já não carregamos
    // aqui) e para qualquer falha de download/decodificação. Consumido pelo XAML via
    // ElementName + BoolToVisibilityConverter para alternar entre a Image e o
    // SymbolIcon de fallback sem nunca mostrar os dois ao mesmo tempo.
    public static readonly DependencyProperty HasContentProperty =
        DependencyProperty.RegisterAttached(
            "HasContent",
            typeof(bool),
            typeof(AsyncImage),
            new PropertyMetadata(false));

    public static string? GetSourceUrl(DependencyObject obj) => (string?)obj.GetValue(SourceUrlProperty);
    public static void SetSourceUrl(DependencyObject obj, string? value) => obj.SetValue(SourceUrlProperty, value);

    public static bool GetHasContent(DependencyObject obj) => (bool)obj.GetValue(HasContentProperty);
    public static void SetHasContent(DependencyObject obj, bool value) => obj.SetValue(HasContentProperty, value);

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
        }
    }

    /// <summary>
    /// Carrega um bitmap de forma assíncrona (cache em memória → cache em disco → download HTTP)
    /// com tratamento de ícones .ico (IconBitmapDecoder), igual ao pipeline usado pelo
    /// SourceUrl DP, mas para uso em code-behind quando você quer primeiro mostrar um
    /// fallback síncrono e depois substituir pelo ícone real baixado. Retorna null se
    /// a imagem não existir (404) ou não decodificar (formato ruim) — o chamador deve
    /// manter o fallback nesse caso.
    /// </summary>
    public static Task<BitmapImage?> LoadBitmapAsync(string url) => LoadAsync(url);

    private static async void OnSourceUrlChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not Image image)
            return;

        string? url = e.NewValue as string;
        image.Source = null;
        SetHasContent(image, false);

        // String vazia ou o pack:// genérico (ResolveIconUrl cai nele quando o app
        // não tem ícone da comunidade) contam como "sem ícone": deixamos a Image em
        // branco e HasContent em false, para o SymbolIcon Box24 do XAML assumir.
        // Não existe mais bitmap genérico carregado aqui — antes esse PNG embutido
        // acabava aparecendo por trás/sobre ícones reais que falhavam ao baixar,
        // criando a "caixa" duplicada que o usuário via.
        if (string.IsNullOrWhiteSpace(url) ||
            url.Equals(WinProvision.Core.Services.IconService.DefaultIconPackUri, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        if (MemoryCache.TryGetValue(url, out BitmapImage? cached))
        {
            image.Source = cached;
            SetHasContent(image, true);
            return;
        }

        try
        {
            BitmapImage? bitmap = await InFlight.GetOrAdd(url, static key => LoadAsync(key));

            // Download falhou (404, timeout, host fora do ar) ou o conteúdo baixado
            // não decodificou como imagem. Fica sem bitmap — o SymbolIcon genérico
            // assume via HasContent=false, em vez de mostrar um PNG de fallback.
            if (bitmap is null)
                return;

            MemoryCache[url] = bitmap;
            TrimMemoryCache();

            // A lista pode ter reciclado o Image enquanto o download ocorria.
            if (string.Equals(GetSourceUrl(image), url, StringComparison.OrdinalIgnoreCase))
            {
                image.Source = bitmap;
                SetHasContent(image, true);
            }
        }
        catch
        {
            // Mesmo tratamento de falha acima: mantém HasContent=false.
        }
        finally
        {
            InFlight.TryRemove(url, out _);
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

    /// <summary>
    /// Decodifica os bytes baixados usando só o WIC do Windows (BitmapDecoder/
    /// IconBitmapDecoder embutidos no .NET/WPF) — sem dependência de terceiros.
    /// PNG/JPEG/GIF/BMP/TIFF decodificam direto via BitmapImage. .ico (detectado
    /// pela assinatura de bytes, não pela extensão da URL, já que aqui só temos os
    /// bytes crus) usa o IconBitmapDecoder, escolhendo o maior frame disponível
    /// (a maioria dos ícones do cdn.winget.microsoft.com traz várias resoluções
    /// embutidas, de 16x16 a 256x256) e reencoda como PNG em memória para manter o
    /// restante do pipeline (cache em disco, BitmapImage final) igual para todos os
    /// formatos.
    /// </summary>
    private static BitmapImage? DecodeToBitmap(byte[] bytes)
    {
        if (IsIcoSignature(bytes))
        {
            BitmapImage? icoBitmap = DecodeIco(bytes);
            if (icoBitmap is not null)
                return icoBitmap;

            // .ico exótico/corrompido que nem o WIC conseguiu ler - cai para a
            // tentativa genérica abaixo como último recurso.
        }

        try
        {
            using var stream = new MemoryStream(bytes);
            var bitmap = new BitmapImage();
            bitmap.BeginInit();
            bitmap.CacheOption = BitmapCacheOption.OnLoad;
            bitmap.StreamSource = stream;
            bitmap.EndInit();
            bitmap.Freeze();
            return bitmap;
        }
        catch
        {
            return null;
        }
    }

    private static bool IsIcoSignature(byte[] bytes) =>
        bytes.Length >= 4 && bytes[0] == 0x00 && bytes[1] == 0x00 && bytes[2] == 0x01 && bytes[3] == 0x00;

    private static BitmapImage? DecodeIco(byte[] bytes)
    {
        try
        {
            using var stream = new MemoryStream(bytes);
            var decoder = new IconBitmapDecoder(stream, BitmapCreateOptions.None, BitmapCacheOption.OnLoad);

            BitmapFrame? bestFrame = decoder.Frames
                .OrderByDescending(frame => frame.PixelWidth)
                .FirstOrDefault();

            if (bestFrame is null)
                return null;

            var encoder = new PngBitmapEncoder();
            encoder.Frames.Add(bestFrame);

            using var pngStream = new MemoryStream();
            encoder.Save(pngStream);
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
