using System.Globalization;
using System.Windows.Data;
using System.Windows.Media;
using Wpf.Ui.Controls;
using WinProvision.Core.Services;

namespace WinProvision.Store.Converters;

/// <summary>
/// Mapeamento único (1 fase = 1 símbolo) usado pela AutoWindow — mantém o ícone de
/// cada card padronizado em vez de cada estágio ter uma escolha independente
/// espalhada por triggers XAML. Ver StageItemControl.xaml.
/// </summary>
public sealed class StageToSymbolConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        => value is AutoInstallStage stage
            ? stage switch
            {
                AutoInstallStage.PackagesAndApps => SymbolRegular.Box24,
                AutoInstallStage.MicrosoftOffice => SymbolRegular.Apps24,
                AutoInstallStage.SystemPersonalization => SymbolRegular.PaintBrush24,
                AutoInstallStage.PredefinedConfigurations => SymbolRegular.Settings24,
                _ => SymbolRegular.Box24
            }
            : SymbolRegular.Box24;

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => Binding.DoNothing;
}

/// <summary>Fase concluída usa a variante "filled" do símbolo (ver StageStatusToIconForegroundConverter para a cor).</summary>
public sealed class StageStatusToFilledConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        => value is AutoInstallStageState.Completed;

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => Binding.DoNothing;
}

/// <summary>Verde quando a fase termina; o mesmo tom neutro do glifo nos demais estados (o fundo do badge não muda — ver StageItemControl.xaml).</summary>
public sealed class StageStatusToIconForegroundConverter : IValueConverter
{
    private static readonly SolidColorBrush CompletedBrush = CreateFrozenBrush("#37C85A");
    private static readonly SolidColorBrush NeutralBrush = CreateFrozenBrush("#EAF0FA");

    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        => value is AutoInstallStageState.Completed ? CompletedBrush : NeutralBrush;

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => Binding.DoNothing;

    private static SolidColorBrush CreateFrozenBrush(string hex)
    {
        var brush = (SolidColorBrush)new BrushConverter().ConvertFromString(hex)!;
        brush.Freeze();
        return brush;
    }
}
