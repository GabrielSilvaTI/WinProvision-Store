using System;
using System.Globalization;
using System.Windows;
using System.Windows.Data;
using System.Windows.Media;
using WinProvision.Core.Models;

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

/// <summary>Cor do texto/ícone de status final, de acordo com o resultado da operação.</summary>
public class OperationStateToBrushConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object parameter, CultureInfo culture) => value switch
    {
        OperationState.Completed => new SolidColorBrush(Color.FromRgb(0x4C, 0xD9, 0x64)),
        OperationState.Failed => new SolidColorBrush(Color.FromRgb(0xF2, 0x5A, 0x5A)),
        OperationState.Canceled => new SolidColorBrush(Color.FromRgb(0xB0, 0xB0, 0xB0)),
        _ => new SolidColorBrush(Color.FromRgb(0xB0, 0xB0, 0xB0))
    };

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}

/// <summary>Texto de resumo final exibido no lugar da barra de progresso quando a operação termina.</summary>
public class OperationStateToTextConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object parameter, CultureInfo culture) => value switch
    {
        OperationState.Completed => "Concluído",
        OperationState.Failed => "Falhou",
        OperationState.Canceled => "Cancelado",
        _ => string.Empty
    };

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}
