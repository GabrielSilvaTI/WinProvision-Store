using System.Windows;
using System.Windows.Media;
using Wpf.Ui.Controls;

namespace WinProvision.Store;

/// <summary>
/// Aplica aos MessageBox da aplicação o estilo compacto definido nos recursos globais.
/// </summary>
internal static class StoreDialogStyles
{
    public static Style ActionButtonStyle =>
        (Style)Application.Current.FindResource("DialogActionButtonStyle");

    public static void Apply(Wpf.Ui.Controls.MessageBox dialog)
    {
        ArgumentNullException.ThrowIfNull(dialog);
        dialog.Resources[typeof(Button)] = ActionButtonStyle;
    }

    public static Style CreatePrimaryActionButtonStyle()
    {
        var style = new Style(typeof(Button), ActionButtonStyle);
        style.Triggers.Add(new Trigger
        {
            Property = Button.AppearanceProperty,
            Value = ControlAppearance.Primary,
            Setters =
            {
                new Setter(Button.BackgroundProperty, Application.Current.Resources["InstallActionBrush"]),
                new Setter(Button.MouseOverBackgroundProperty, Application.Current.Resources["InstallActionHoverBrush"]),
                new Setter(Button.PressedBackgroundProperty, Application.Current.Resources["InstallActionPressedBrush"]),
                new Setter(Button.ForegroundProperty, Brushes.White),
                new Setter(Button.PressedForegroundProperty, Brushes.White)
            }
        });

        return style;
    }
}
