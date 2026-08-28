using System;
using System.Collections.Concurrent;
using System.IO;
using System.Net.Http;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media.Imaging;
using SixLabors.ImageSharp.Formats.Png;
using SixImage = SixLabors.ImageSharp.Image;

namespace WinProvision.Store.Converters;

/// <summary>
/// Substitui o WebpUrlToBitmapConverter (que baixava e decodificava cada ícone de
/// forma SÍNCRONA na thread de UI durante o binding). Com listas/grades de várias
/// dezenas de cards (StorePage, PackagesPage, HomePage) isso travava a UI por
/// segundos toda vez que a lista era populada ou rolada.
///
/// Uso no XAML (em vez de Source="{Binding IconUrl, Converter=...}"):
///   <Image conv:AsyncImage.SourceUrl="{Binding IconUrl}" />
///
/// - Baixa + decodifica (via ImageSharp, pois o WIC não lê .webp nativamente) em
///   background, sem bloquear a UI.
/// - Cacheia por URL em memória (evita rebaixar o mesmo ícone ao navegar entre
///   páginas ou rolar a lista).
/// - Ignora o resultado se o Image já foi reciclado para outra URL enquanto o
///   download estava em andamento (evita "ícone trocado" em listas virtualizadas).
/// </summary>
public static class AsyncImage
{
    private static readonly ConcurrentDictionary<string, BitmapImage> Cache = new();
    private static readonly HttpClient Client = new() { Timeout = TimeSpan.FromSeconds(10) };

    public static readonly DependencyProperty SourceUrlProperty =
        DependencyProperty.RegisterAttached(
            "SourceUrl",
            typeof(string),
            typeof(AsyncImage),
            new PropertyMetadata(null, OnSourceUrlChanged));

    public static string? GetSourceUrl(DependencyObject obj) => (string?)obj.GetValue(SourceUrlProperty);

    public static void SetSourceUrl(DependencyObject obj, string? value) => obj.SetValue(SourceUrlProperty, value);

    private static async void OnSourceUrlChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not Image image)
        {
            return;
        }

        string? url = e.NewValue as string;
        image.Source = null;

        if (string.IsNullOrWhiteSpace(url))
        {
            return;
        }

        if (Cache.TryGetValue(url, out BitmapImage? cached))
        {
            image.Source = cached;
            return;
        }

        try
        {
            byte[] bytes = await Client.GetByteArrayAsync(url);
            BitmapImage? bitmap = await Task.Run(() => DecodeToBitmap(bytes));

            if (bitmap == null)
            {
                return;
            }

            Cache[url] = bitmap;

            // Se o Image já pediu outra URL enquanto isso baixava (item reciclado
            // numa lista virtualizada), não aplica um ícone que não é mais o dele.
            if (GetSourceUrl(image) == url)
            {
                image.Source = bitmap;
            }
        }
        catch
        {
            // Sem ícone disponível (rede, 404, formato inesperado etc.):
            // deixa Source nulo — o XAML deve prever um fallback visual
            // (Border com Background sólido por trás do Image, por exemplo).
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
