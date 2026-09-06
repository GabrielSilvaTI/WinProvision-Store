using System;
using System.Globalization;
using System.Windows;
using System.Windows.Data;

namespace WinProvision.Store.Converters;

/// <summary>
/// Usado para esconder o bloco de tags (chips) na área expandida do cartão de app
/// quando o AppEntry não tem nenhuma tag no apps.json.
/// </summary>
public class CountToVisibilityConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object parameter, CultureInfo culture)
    {
        int count = value is int i ? i : 0;
        bool visible = count > 0;

        if (string.Equals(parameter as string, "Invert", StringComparison.OrdinalIgnoreCase))
        {
            visible = !visible;
        }

        return visible ? Visibility.Visible : Visibility.Collapsed;
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
    {
        throw new NotSupportedException();
    }
}
