using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace WinProvision.Store.Behaviors;

/// <summary>
/// Rolagem suave para ScrollViewer via mouse wheel.
///
/// A primeira versão usava DoubleAnimation (Storyboard/Clock) por notch da roda.
/// Isso aloca um DoubleAnimation + CubicEase novos a cada evento de wheel, e
/// mouses de precisão disparam dezenas de eventos por segundo. Cada alocação
/// gera pressão de GC no thread de UI, e as pausas do coletor aparecem como
/// as micro travadas relatadas.
///
/// Agora o offset é suavizado por frame via CompositionTarget.Rendering, com
/// interpolação exponencial baseada no tempo decorrido (frame-rate independente,
/// sem alocação por tick). Cada novo notch só atualiza o alvo; a suavização
/// continua o movimento já em curso.
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

    private static readonly Dictionary<ScrollViewer, ScrollState> States = new();

    private const double PixelsPerNotch = 120.0;

    // Maior = alcança o alvo mais rápido. 14 dá uma sensação fluida, sem parecer
    // elástico nem instantâneo.
    private const double SmoothingRate = 14.0;

    // Abaixo desta distância do alvo, encerra a animação e assenta no valor exato.
    private const double SnapThreshold = 0.4;

    private sealed class ScrollState
    {
        public double Target;
        public bool IsRunning;
        public TimeSpan LastRenderTime;
        public EventHandler? RenderingHandler;
    }

    private static void OnEnableChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not ScrollViewer scrollViewer)
        {
            return;
        }

        scrollViewer.PreviewMouseWheel -= OnPreviewMouseWheel;
        scrollViewer.Unloaded -= OnScrollViewerUnloaded;

        if ((bool)e.NewValue)
        {
            scrollViewer.PreviewMouseWheel += OnPreviewMouseWheel;
            scrollViewer.Unloaded += OnScrollViewerUnloaded;
        }
        else
        {
            StopAndRemove(scrollViewer);
        }
    }

    private static void OnScrollViewerUnloaded(object sender, RoutedEventArgs e)
    {
        if (sender is ScrollViewer scrollViewer)
        {
            StopAndRemove(scrollViewer);
        }
    }

    private static void StopAndRemove(ScrollViewer scrollViewer)
    {
        if (States.TryGetValue(scrollViewer, out var state))
        {
            if (state.IsRunning && state.RenderingHandler is not null)
            {
                CompositionTarget.Rendering -= state.RenderingHandler;
            }

            States.Remove(scrollViewer);
        }
    }

    private static void OnPreviewMouseWheel(object sender, MouseWheelEventArgs e)
    {
        if (sender is not ScrollViewer scrollViewer)
        {
            return;
        }

        // Eventos de roda disparados dentro de um Popup aberto (ex.: a lista
        // suspensa de um ComboBox "expandido") tunelam por este handler antes de
        // chegar ao ScrollViewer interno do próprio Popup, porque o Popup usa o
        // controle que o abriu (aqui, um descendente deste ScrollViewer) como pai
        // lógico pra fins de roteamento de evento — mesmo a lista sendo desenhada
        // fora da árvore visual da página. Sem este desvio, "e.Handled = true"
        // abaixo sequestra a rolagem: o mouse fica sobre as opções (1 minuto, 2
        // minutos...) mas quem rola é o card por trás, não a lista aberta. Ao
        // detectar que a origem do evento pertence a um Popup aberto, saímos sem
        // marcar Handled, deixando o ScrollViewer do próprio Popup (ver
        // Styles/ComboBoxStyle.xaml) tratar a rolagem normalmente.
        if (IsInsideOpenPopup(e.OriginalSource as DependencyObject))
        {
            return;
        }

        e.Handled = true;

        if (!States.TryGetValue(scrollViewer, out var state))
        {
            state = new ScrollState { Target = scrollViewer.VerticalOffset };
            States[scrollViewer] = state;
        }

        var notches = e.Delta / 120.0;
        state.Target = Math.Clamp(state.Target - (notches * PixelsPerNotch), 0, scrollViewer.ScrollableHeight);

        if (!state.IsRunning)
        {
            state.IsRunning = true;
            state.LastRenderTime = TimeSpan.Zero;
            state.RenderingHandler = (_, renderArgs) => OnRendering(scrollViewer, state, (RenderingEventArgs)renderArgs);
            CompositionTarget.Rendering += state.RenderingHandler;
        }
    }

    private static bool IsInsideOpenPopup(DependencyObject? source)
    {
        while (source is not null)
        {
            if (source is System.Windows.Controls.Primitives.Popup { IsOpen: true })
            {
                return true;
            }

            // Um Popup com AllowsTransparency (caso do ComboBox) renderiza numa
            // janela Win32 separada, então VisualTreeHelper para no topo dessa
            // árvore; LogicalTreeHelper e TemplatedParent completam o caminho de
            // volta até o Popup em si.
            var next = VisualTreeHelper.GetParent(source)
                       ?? LogicalTreeHelper.GetParent(source)
                       ?? (source as FrameworkElement)?.TemplatedParent;

            source = next;
        }

        return false;
    }

    private static void OnRendering(ScrollViewer scrollViewer, ScrollState state, RenderingEventArgs e)
    {
        if (state.LastRenderTime == TimeSpan.Zero)
        {
            state.LastRenderTime = e.RenderingTime;
            return;
        }

        var deltaSeconds = (e.RenderingTime - state.LastRenderTime).TotalSeconds;
        state.LastRenderTime = e.RenderingTime;

        if (deltaSeconds <= 0)
        {
            return;
        }

        var current = scrollViewer.VerticalOffset;
        var diff = state.Target - current;

        if (Math.Abs(diff) <= SnapThreshold)
        {
            scrollViewer.ScrollToVerticalOffset(state.Target);
            CompositionTarget.Rendering -= state.RenderingHandler;
            state.IsRunning = false;
            state.RenderingHandler = null;
            return;
        }

        // Interpolação exponencial: converge para o alvo a uma taxa constante no
        // tempo, não por número de frames, então o movimento é igualmente fluido
        // a 60Hz, 120Hz ou sob variação de frame time.
        var t = 1.0 - Math.Exp(-SmoothingRate * deltaSeconds);
        scrollViewer.ScrollToVerticalOffset(current + (diff * t));
    }
}