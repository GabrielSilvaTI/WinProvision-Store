using System.Globalization;
using System.Windows.Data;

namespace WinProvision.Store.Converters;

/// <summary>Converte os identificadores internos de origem para rótulos da interface.</summary>
public sealed class PackageSourceLabelConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        string source = value?.ToString()?.Trim() ?? string.Empty;

        if (source.StartsWith("WinGet:", StringComparison.OrdinalIgnoreCase))
            source = source[(source.IndexOf(':') + 1)..].Trim();

        if (source.Equals("winget", StringComparison.OrdinalIgnoreCase))
            return "WinGet";

        if (source.Equals("msstore", StringComparison.OrdinalIgnoreCase)
            || source.Equals("Microsoft Store", StringComparison.OrdinalIgnoreCase))
            return "Microsoft Store";

        if (string.IsNullOrWhiteSpace(source)
            || source.Equals("local", StringComparison.OrdinalIgnoreCase)
            || source.Equals("Windows (local)", StringComparison.OrdinalIgnoreCase)
            || source.Equals("Programas instalados", StringComparison.OrdinalIgnoreCase))
            return "PC Local";

        return source;
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) =>
        Binding.DoNothing;
}
