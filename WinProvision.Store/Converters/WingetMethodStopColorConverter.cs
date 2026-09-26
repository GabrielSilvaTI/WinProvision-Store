using System;
using System.Globalization;
using System.Windows;
using System.Windows.Data;
using System.Windows.Media;
using WinProvision.Core.Models;

namespace WinProvision.Store.Converters;

/// <summary>
/// Resolve tons derivados da cor fixa do método para compatibilidade com preenchimentos em gradiente.
/// O preenchimento sólido da fila usa <see cref="WingetMethodBrushConverter"/>.
/// </summary>
public class WingetMethodStopColorConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object parameter, CultureInfo culture)
    {
        var method = value is WingetMethod typed ? typed : WingetMethod.Unknown;
        var resourceKey = method switch
        {
            WingetMethod.ComApi => "AppOperationComBrush",
            WingetMethod.OwnApi => "AppOperationApiBrush",
            WingetMethod.WingetExe => "AppOperationWingetBrush",
            _ => "AppOperationDefaultBrush"
        };
        var baseColor = Application.Current?.TryFindResource(resourceKey) is SolidColorBrush brush
            ? brush.Color
            : Color.FromRgb(0x16, 0x88, 0xE8);

        var slot = parameter as string ?? "A";
        return slot switch
        {
            "B" => Lighten(baseColor, 0.35),
            "C" => Lighten(baseColor, 0.72),
            _ => baseColor
        };
    }

    private static Color Lighten(Color color, double amount)
    {
        static byte Mix(byte source, double amount) =>
            (byte)Math.Clamp(source + ((byte.MaxValue - source) * amount), 0, byte.MaxValue);

        return Color.FromArgb(color.A, Mix(color.R, amount), Mix(color.G, amount), Mix(color.B, amount));
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}

/// <summary>Retorna a cor sólida da via atual para controles de progresso.</summary>
public sealed class WingetMethodBrushConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object parameter, CultureInfo culture)
    {
        var method = value is WingetMethod typed ? typed : WingetMethod.Unknown;
        var resourceKey = method switch
        {
            WingetMethod.ComApi => "AppOperationComBrush",
            WingetMethod.OwnApi => "AppOperationApiBrush",
            WingetMethod.WingetExe => "AppOperationWingetBrush",
            _ => "AppOperationDefaultBrush"
        };

        if (Application.Current?.TryFindResource(resourceKey) is Brush resourceBrush)
        {
            return resourceBrush;
        }

        Color fallback = method switch
        {
            WingetMethod.ComApi => Color.FromRgb(0xB9, 0x91, 0xFF),
            WingetMethod.OwnApi => Color.FromRgb(0xFF, 0x9A, 0x3D),
            _ => Color.FromRgb(0x16, 0x88, 0xE8)
        };
        return new SolidColorBrush(fallback);
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}
