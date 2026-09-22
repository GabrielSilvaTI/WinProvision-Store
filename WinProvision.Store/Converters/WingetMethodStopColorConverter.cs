using System;
using System.Globalization;
using System.Windows.Data;
using System.Windows.Media;
using WinProvision.Core.Models;

namespace WinProvision.Store.Converters;

/// <summary>
/// Resolve a cor de um GradientStop da barra de progresso (ver GradientProgressBar.xaml)
/// conforme a via da operação (<see cref="WingetMethod"/>). Usado via Binding direto no
/// próprio Color do GradientStop — não dá pra usar Setter.TargetName num GradientStop (erro de
/// compilação MC4111: "o destino deve aparecer antes dos Setters/Triggers que o utilizem", que
/// só vale pra FrameworkElement, não pra Freezable declarado dentro de uma árvore de
/// propriedades). Bindar o Color diretamente contorna isso e continua funcionando tanto no
/// preenchimento determinado (download/instalação com %) quanto na faixa indeterminada — a
/// mesma via colore os dois.
///
/// ConverterParameter é "A", "B" ou "C", correspondendo aos mesmos três tons que cada via já
/// usa (GradientProgressColorA/B/C é o padrão sem via identificada).
/// </summary>
public class WingetMethodStopColorConverter : IValueConverter
{
    private static readonly Color DefaultA = Color.FromRgb(0x3A, 0x8D, 0xFF);
    private static readonly Color DefaultB = Color.FromRgb(0x7C, 0xD4, 0xFD);
    private static readonly Color DefaultC = Color.FromRgb(0xEA, 0xF6, 0xFF);

    private static readonly Color ComA = Color.FromRgb(0x8A, 0x4F, 0xFF);
    private static readonly Color ComB = Color.FromRgb(0xC9, 0xA6, 0xFF);
    private static readonly Color ComC = Color.FromRgb(0xEF, 0xE6, 0xFF);

    private static readonly Color OwnApiA = Color.FromRgb(0xE0, 0xB4, 0x00);
    private static readonly Color OwnApiB = Color.FromRgb(0xFF, 0xDE, 0x7A);
    private static readonly Color OwnApiC = Color.FromRgb(0xFF, 0xF6, 0xD8);

    private static readonly Color WingetA = Color.FromRgb(0x00, 0xB8, 0xD9);
    private static readonly Color WingetB = Color.FromRgb(0x7F, 0xE8, 0xF5);
    private static readonly Color WingetC = Color.FromRgb(0xE6, 0xFB, 0xFF);

    public object Convert(object? value, Type targetType, object parameter, CultureInfo culture)
    {
        var slot = parameter as string ?? "A";
        var method = value as WingetMethod? ?? WingetMethod.Unknown;

        return (method, slot) switch
        {
            (WingetMethod.ComApi, "A") => ComA,
            (WingetMethod.ComApi, "B") => ComB,
            (WingetMethod.ComApi, "C") => ComC,

            (WingetMethod.OwnApi, "A") => OwnApiA,
            (WingetMethod.OwnApi, "B") => OwnApiB,
            (WingetMethod.OwnApi, "C") => OwnApiC,

            (WingetMethod.WingetExe, "A") => WingetA,
            (WingetMethod.WingetExe, "B") => WingetB,
            (WingetMethod.WingetExe, "C") => WingetC,

            (_, "B") => DefaultB,
            (_, "C") => DefaultC,
            _ => DefaultA
        };
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}
