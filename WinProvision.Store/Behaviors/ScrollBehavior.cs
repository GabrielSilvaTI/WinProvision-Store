using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

namespace WinProvision.Store.Behaviors;

/// <summary>
/// Corrige o bug reportado nas listas (StorePage/PackagesPage): a rolagem via
/// mouse wheel só funcionava com o ponteiro exatamente sobre a barra de rolagem,
/// não sobre o restante do conteúdo (cards/itens).
///
/// Uso no XAML: adicionar behaviors:ScrollBehavior.EnableMouseWheelOnContent="True"
/// no próprio ScrollViewer.
/// </summary>
public static class ScrollBehavior
{
    public static readonly DependencyProperty EnableMouseWheelOnContentProperty =
        DependencyProperty.RegisterAttached(
            "EnableMouseWheelOnContent",
            typeof(bool),
            typeof(ScrollBehavior),
            new PropertyMetadata(false, OnEnableChanged));

    public static bool GetEnableMouseWheelOnContent(DependencyObject obj) =>
        (bool)obj.GetValue(EnableMouseWheelOnContentProperty);

    public static void SetEnableMouseWheelOnContent(DependencyObject obj, bool value) =>
        obj.SetValue(EnableMouseWheelOnContentProperty, value);

    private static void OnEnableChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not ScrollViewer scrollViewer)
        {
            return;
        }

        scrollViewer.PreviewMouseWheel -= OnPreviewMouseWheel;

        if ((bool)e.NewValue)
        {
            scrollViewer.PreviewMouseWheel += OnPreviewMouseWheel;
        }
    }

    private static void OnPreviewMouseWheel(object sender, MouseWheelEventArgs e)
    {
        if (sender is not ScrollViewer scrollViewer)
        {
            return;
        }

        scrollViewer.ScrollToVerticalOffset(scrollViewer.VerticalOffset - e.Delta / 3.0);
        e.Handled = true;
    }
}
