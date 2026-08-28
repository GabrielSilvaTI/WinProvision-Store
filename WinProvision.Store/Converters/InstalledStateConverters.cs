using System.Globalization;
using System.Windows.Data;
using Wpf.Ui.Controls;

namespace WinProvision.Store.Converters;

/// <summary>Troca o texto do botão de ação do card: "Instalar" (não instalado) / "Abrir" (instalado).</summary>
public class InstalledToLabelConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) =>
        value is true ? "Abrir" : "Instalar";

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}

/// <summary>Troca o ícone do botão de ação do card: download (não instalado) / abrir (instalado).</summary>
public class InstalledToIconConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) =>
        new SymbolIcon { Symbol = value is true ? SymbolRegular.Open24 : SymbolRegular.ArrowDownload24 };

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}

/// <summary>
/// Versão de 3 estados do rótulo do botão de ação: "Instalando" (instalação em
/// andamento) tem prioridade sobre "Abrir"/"Instalar". Espera values[0]=IsInstalled,
/// values[1]=IsInstalling.
/// </summary>
public class InstallStateToLabelConverter : IMultiValueConverter
{
    public object Convert(object[] values, Type targetType, object parameter, CultureInfo culture)
    {
        bool isInstalled = values.Length > 0 && values[0] is true;
        bool isInstalling = values.Length > 1 && values[1] is true;

        if (isInstalling) return "Instalando";
        return isInstalled ? "Abrir" : "Instalar";
    }

    public object[] ConvertBack(object value, Type[] targetTypes, object parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}

/// <summary>
/// Inverte um bool - usado para bloquear cliques (IsHitTestVisible) durante a
/// instalação sem tocar em IsEnabled, que mudaria a cor do botão via o estilo
/// "disabled" padrão do WPF-UI.
/// </summary>
public class InverseBooleanConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) =>
        !(value is true);

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) =>
        !(value is true);
}

/// <summary>
/// Versão de 3 estados do ícone do botão de ação (mesma prioridade do
/// <see cref="InstallStateToLabelConverter"/>). Durante a instalação mantém o ícone
/// de download — só o texto muda para "Instalando" — para não sugerir uma ação nova.
/// </summary>
public class InstallStateToIconConverter : IMultiValueConverter
{
    public object Convert(object[] values, Type targetType, object parameter, CultureInfo culture)
    {
        bool isInstalled = values.Length > 0 && values[0] is true;
        bool isInstalling = values.Length > 1 && values[1] is true;

        if (isInstalling) return new SymbolIcon { Symbol = SymbolRegular.ArrowDownload24 };
        return new SymbolIcon { Symbol = isInstalled ? SymbolRegular.Open24 : SymbolRegular.ArrowDownload24 };
    }

    public object[] ConvertBack(object value, Type[] targetTypes, object parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}
