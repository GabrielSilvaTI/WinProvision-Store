using System;
using System.Globalization;
using System.Windows;
using System.Windows.Data;

namespace WinProvision.Store.Converters;

/// <summary>Esconde a barra de progresso e o botão de cancelar quando a operação já terminou.</summary>
public class NotFinishedToVisibilityConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object parameter, CultureInfo culture) =>
        value is true ? Visibility.Collapsed : Visibility.Visible;

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}

/// <summary>Mostra o resumo final (sucesso/erro/cancelado) só quando a operação já terminou.</summary>
public class FinishedToVisibilityConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object parameter, CultureInfo culture) =>
        value is true ? Visibility.Visible : Visibility.Collapsed;

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}

