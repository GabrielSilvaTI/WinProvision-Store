using System;
using System.Globalization;
using System.Windows;
using System.Windows.Data;

namespace WinProvision.Store.Converters;

/// <summary>
/// Usado na área expandida do cartão de app da StorePage: esconde linhas de metadado
/// opcionais (Description, License, Homepage, ReleaseNotesUrl) quando o campo do
/// AppEntry vem nulo/vazio no apps.json, em vez de mostrar um rótulo vazio.
/// </summary>
public class NullToVisibilityConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object parameter, CultureInfo culture)
    {
        bool isEmpty = value is null || (value is string s && string.IsNullOrWhiteSpace(s));
        return isEmpty ? Visibility.Collapsed : Visibility.Visible;
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
    {
        throw new NotSupportedException();
    }
}
