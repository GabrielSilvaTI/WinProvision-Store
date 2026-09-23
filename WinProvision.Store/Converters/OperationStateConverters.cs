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

/// <summary>Cor do texto/ícone de status final, de acordo com o resultado da operação e método de download.</summary>
public class OperationStateToBrushConverter : IValueConverter
{
    private static readonly Color DefaultCompletedColor = Color.FromRgb(0x4C, 0xD9, 0x64);
    private static readonly Color ComCompletedColor = Color.FromRgb(0x8A, 0x4F, 0xFF);
    private static readonly Color OwnApiCompletedColor = Color.FromRgb(0xE0, 0xB4, 0x00);
    private static readonly Color WingetCompletedColor = Color.FromRgb(0x00, 0xB8, 0xD9);

    public object Convert(object? value, Type targetType, object parameter, CultureInfo culture) => value switch
    {
        OperationState.Completed => new SolidColorBrush(DefaultCompletedColor),
        OperationState.Failed => new SolidColorBrush(Color.FromRgb(0xF2, 0x5A, 0x5A)),
        OperationState.Canceled => new SolidColorBrush(Color.FromRgb(0xB0, 0xB0, 0xB0)),
        _ => new SolidColorBrush(Color.FromRgb(0xB0, 0xB0, 0xB0))
    };

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}

/// <summary>Converter multi-valor para cor do texto/ícone de status final, considerando State e Method.</summary>
public class OperationStateToBrushMultiConverter : IMultiValueConverter
{
    private static readonly Color DefaultCompletedColor = Color.FromRgb(0x4C, 0xD9, 0x64);
    private static readonly Color ComCompletedColor = Color.FromRgb(0x8A, 0x4F, 0xFF);
    private static readonly Color OwnApiCompletedColor = Color.FromRgb(0xE0, 0xB4, 0x00);
    private static readonly Color WingetCompletedColor = Color.FromRgb(0x00, 0xB8, 0xD9);

    public object Convert(object[] values, Type targetType, object parameter, CultureInfo culture)
    {
        if (values.Length >= 2)
        {
            var state = values[0] as OperationState?;
            var method = values[1] as WingetMethod?;

            if (state == OperationState.Completed && method.HasValue)
            {
                return method.Value switch
                {
                    WingetMethod.ComApi => new SolidColorBrush(ComCompletedColor),
                    WingetMethod.OwnApi => new SolidColorBrush(OwnApiCompletedColor),
                    WingetMethod.WingetExe => new SolidColorBrush(WingetCompletedColor),
                    _ => new SolidColorBrush(DefaultCompletedColor)
                };
            }
        }

        // Fallback para o comportamento original (apenas State)
        var fallbackState = values.Length > 0 ? values[0] as OperationState? : null;
        return fallbackState switch
        {
            OperationState.Completed => new SolidColorBrush(DefaultCompletedColor),
            OperationState.Failed => new SolidColorBrush(Color.FromRgb(0xF2, 0x5A, 0x5A)),
            OperationState.Canceled => new SolidColorBrush(Color.FromRgb(0xB0, 0xB0, 0xB0)),
            _ => new SolidColorBrush(Color.FromRgb(0xB0, 0xB0, 0xB0))
        };
    }

    public object[] ConvertBack(object value, Type[] targetTypes, object parameter, CultureInfo culture) =>
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
