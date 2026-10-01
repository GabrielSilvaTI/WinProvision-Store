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

    /// <summary>
    /// Largura máxima usada ao decodificar uma imagem. Os arquivos originais continuam no
    /// cache em disco; o cache de memória guarda somente o bitmap no tamanho necessário
    /// pelo controle que o exibe.
    /// </summary>
    public static readonly DependencyProperty DecodePixelWidthProperty =
        DependencyProperty.RegisterAttached(
            "DecodePixelWidth",
            typeof(int),
            typeof(AsyncImage),
            new PropertyMetadata(0, OnDecodePixelWidthChanged));

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

    public static int GetDecodePixelWidth(DependencyObject obj) => (int)obj.GetValue(DecodePixelWidthProperty);
    public static void SetDecodePixelWidth(DependencyObject obj, int value) => obj.SetValue(DecodePixelWidthProperty, value);

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
    public static Task<BitmapImage?> LoadBitmapAsync(string url) => LoadAsync(url, 0);

    private static void OnSourceUrlChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not Image image)
            return;

        BeginLoad(image, e.NewValue as string, GetDecodePixelWidth(image));
    }

    private static void OnDecodePixelWidthChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is Image image)
            BeginLoad(image, GetSourceUrl(image), (int)e.NewValue);
    }

    private static void BeginLoad(Image image, string? url, int decodePixelWidth) =>
        _ = LoadAndAssignAsync(image, url, Math.Max(0, decodePixelWidth));

    private static async Task LoadAndAssignAsync(Image image, string? url, int decodePixelWidth)
    {
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

        string requestKey = GetRequestKey(url, decodePixelWidth);
        if (MemoryCache.TryGetValue(requestKey, out BitmapImage? cached))
        {
            image.Source = cached;
            SetHasContent(image, true);
            return;
        }

        try
        {
            BitmapImage? bitmap = await InFlight.GetOrAdd(
                requestKey,
                _ => LoadAsync(url, decodePixelWidth));

            // Download falhou (404, timeout, host fora do ar) ou o conteúdo baixado
            // não decodificou como imagem. Fica sem bitmap — o SymbolIcon genérico
            // assume via HasContent=false, em vez de mostrar um PNG de fallback.
            if (bitmap is null)
                return;

            MemoryCache[requestKey] = bitmap;
            TrimMemoryCache();

            // A lista pode ter reciclado o Image enquanto o download ocorria.
            if (string.Equals(GetSourceUrl(image), url, StringComparison.OrdinalIgnoreCase)
                && GetDecodePixelWidth(image) == decodePixelWidth)
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
            InFlight.TryRemove(requestKey, out _);
        }
    }

    private static async Task<BitmapImage?> LoadAsync(string url, int decodePixelWidth)
    {
        try
        {
            Directory.CreateDirectory(DiskCacheFolder);
            string path = GetDiskPath(url);

            if (File.Exists(path) && DateTime.UtcNow - File.GetLastWriteTimeUtc(path) < DiskCacheTtl)
            {
                byte[] cachedBytes = await File.ReadAllBytesAsync(path);
                BitmapImage? cachedBitmap = await Task.Run(() => DecodeToBitmap(cachedBytes, decodePixelWidth));
                if (cachedBitmap is not null)
                    return cachedBitmap;

                // Downloads antigos podem ter gravado uma resposta inválida como
                // imagem. Apaga o cache corrompido e tenta novamente a origem.
                try { File.Delete(path); } catch { }
            }

            byte[] bytes;
            if (Uri.TryCreate(url, UriKind.Absolute, out var imageUri) && imageUri.Scheme == "pack")
            {
                var resource = Application.GetResourceStream(imageUri);
                if (resource is null)
                    return null;

                using (resource.Stream)
                using (var content = new MemoryStream())
                {
                    await resource.Stream.CopyToAsync(content);
                    bytes = content.ToArray();
                }
            }
            else
            {
                string localPath = url.StartsWith("file://", StringComparison.OrdinalIgnoreCase)
                    ? new Uri(url).LocalPath
                    : url;
                bytes = File.Exists(localPath)
                    ? await File.ReadAllBytesAsync(localPath)
                    : await Client.GetByteArrayAsync(url);
            }
            BitmapImage? bitmap = await Task.Run(() => DecodeToBitmap(bytes, decodePixelWidth));
            if (bitmap is null)
                return null;

            try
            {
                await File.WriteAllBytesAsync(path, bytes);
            }
            catch
            {
                // A imagem ainda pode ser exibida mesmo se o cache de disco falhar.
            }

            return bitmap;
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

    private static string GetRequestKey(string url, int decodePixelWidth) =>
        $"{url}|width={decodePixelWidth}";

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
    private static BitmapImage? DecodeToBitmap(byte[] bytes, int decodePixelWidth)
    {
        if (IsIcoSignature(bytes))
        {
            BitmapImage? icoBitmap = DecodeIco(bytes, decodePixelWidth);
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
            if (decodePixelWidth > 0)
                bitmap.DecodePixelWidth = decodePixelWidth;
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

    private static BitmapImage? DecodeIco(byte[] bytes, int decodePixelWidth)
    {
        try
        {
            using var stream = new MemoryStream(bytes);
            var decoder = new IconBitmapDecoder(stream, BitmapCreateOptions.None, BitmapCacheOption.OnLoad);

            BitmapFrame? bestFrame = decodePixelWidth > 0
                ? decoder.Frames
                    .Where(frame => frame.PixelWidth >= decodePixelWidth)
                    .OrderBy(frame => frame.PixelWidth)
                    .FirstOrDefault()
                    ?? decoder.Frames.OrderByDescending(frame => frame.PixelWidth).FirstOrDefault()
                : decoder.Frames.OrderByDescending(frame => frame.PixelWidth).FirstOrDefault();

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
            if (decodePixelWidth > 0)
                bitmap.DecodePixelWidth = decodePixelWidth;
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
