using System;
using System.Globalization;
using System.Windows;
using System.Windows.Data;

namespace WinProvision.Store.Converters;

/// <summary>
/// Usado, por exemplo, para alternar entre a Image de um ícone e o SymbolIcon de
/// fallback: ambos ficam Collapsed por padrão e só um vira Visible, ligados ao mesmo
/// conv:AsyncImage.HasContent do elemento Image via ElementName, um deles com
/// ConverterParameter=Invert.
/// </summary>
public class BoolToVisibilityConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object parameter, CultureInfo culture)
    {
        bool result = value is true;
        if (string.Equals(parameter as string, "Invert", StringComparison.OrdinalIgnoreCase))
            result = !result;

        return result ? Visibility.Visible : Visibility.Collapsed;
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
    {
        throw new NotSupportedException();
    }
}
