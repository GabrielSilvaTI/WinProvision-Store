using System.Globalization;
using System.Windows.Data;

namespace WinProvision.Store.Converters;

public sealed class ProgressScaleConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is double d)
            return Math.Clamp(d / 100d, 0d, 1d);
        if (value is int i)
            return Math.Clamp(i / 100d, 0d, 1d);
        return 0d;
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => Binding.DoNothing;
}
