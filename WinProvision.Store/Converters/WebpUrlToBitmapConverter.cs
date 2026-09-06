using System;
using System.Globalization;
using System.IO;
using System.Net.Http;
using System.Windows.Data;
using System.Windows.Media.Imaging;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Formats.Png;

namespace WinProvision.Store.Converters;

/// <summary>
/// Conversor de teste: baixa a URL do ícone (webp incluso) e decodifica via
/// ImageSharp, reencodando para PNG em memória antes de virar BitmapImage.
/// Bloqueia a thread de chamada — só para validar se os ícones do R2 aparecem
/// na UI. Não usar assim em produção (sem cache, sem async, sem tratamento de
/// falha por item).
/// </summary>
public class WebpUrlToBitmapConverter : IValueConverter
{
    private static readonly HttpClient Client = new() { Timeout = TimeSpan.FromSeconds(10) };

    public object? Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        if (value is not string url || string.IsNullOrWhiteSpace(url))
            return null;

        try
        {
            byte[] bytes = Client.GetByteArrayAsync(url).GetAwaiter().GetResult();

            using var image = Image.Load(bytes);
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

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => throw new NotSupportedException();
}
