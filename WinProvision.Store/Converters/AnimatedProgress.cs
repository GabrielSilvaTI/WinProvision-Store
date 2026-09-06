using System;
using System.Windows;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Media.Animation;

namespace WinProvision.Store.Converters;

/// <summary>
/// Faz a transição de um percentual (0-100) parecer contínua, no lugar do "salto"
/// instantâneo que acontecia antes. Isso não é o mesmo problema de granularidade dos
/// dados: o winget/ODT, rodando sem console interativo (redirecionado pro Process),
/// não manda uma atualização a cada 1-2%, manda em rajadas — às vezes só no início e
/// no fim de um trecho. Antes, cada Report chegava e o Value/ScaleX era setado direto
/// pelo binding, então a barra ficava parada esperando o próximo Report e pulava de
/// uma vez quando ele chegava. Aqui, cada mudança de valor dispara uma animação curta
/// entre o valor antigo e o novo — o efeito visual final fica parecido com a barra do
/// winget rodando direto no console do PowerShell, mesmo com os dados chegando aos
/// pedaços.
///
/// Funciona em dois tipos de alvo, decidido pelo elemento onde a propriedade anexada é
/// usada:
///   - RangeBase (ex.: ProgressBar) -> anima a própria Value (0-100). Usado no painel
///     de operações isoladas (OperationsQueuePanel).
///   - Qualquer FrameworkElement cujo RenderTransform seja um ScaleTransform -> anima
///     ScaleX, tratando o percentual como fração (0-1). Usado na barra principal e nas
///     barras por etapa da AutoWindow (GlobalProgress / StageItemControl), que
///     preenchem via ScaleTransform em vez de um ProgressBar nativo.
/// </summary>
public static class AnimatedProgress
{
    // Rápido o bastante pra não parecer "atrasado" em relação ao valor real, devagar
    // o bastante pra realmente parecer um preenchimento contínuo em vez de um pulo.
    private static readonly Duration AnimationDuration = new(TimeSpan.FromMilliseconds(450));

    public static readonly DependencyProperty PercentProperty =
        DependencyProperty.RegisterAttached(
            "Percent",
            typeof(double),
            typeof(AnimatedProgress),
            new PropertyMetadata(0d, OnPercentChanged));

    public static double GetPercent(DependencyObject obj) => (double)obj.GetValue(PercentProperty);
    public static void SetPercent(DependencyObject obj, double value) => obj.SetValue(PercentProperty, value);

    private static void OnPercentChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        double from = e.OldValue is double oldValue ? oldValue : 0d;
        double to = e.NewValue is double newValue ? newValue : 0d;

        // Regressão (reinício de item/etapa voltando a 0, ou correção pra baixo) não
        // anima "pra trás" — só transições pra frente ganham o efeito suave. Isso
        // também evita animação estranha quando um novo item é enfileirado do zero.
        bool animate = to > from + 0.01;

        if (d is RangeBase rangeBase)
        {
            if (!animate)
            {
                rangeBase.BeginAnimation(RangeBase.ValueProperty, null);
                rangeBase.Value = to;
                return;
            }

            rangeBase.BeginAnimation(RangeBase.ValueProperty, new DoubleAnimation(from, to, AnimationDuration)
            {
                EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseOut }
            });
            return;
        }

        if (d is FrameworkElement { RenderTransform: ScaleTransform scale })
        {
            double fromScale = Math.Clamp(from, 0, 100) / 100d;
            double toScale = Math.Clamp(to, 0, 100) / 100d;

            if (!animate)
            {
                scale.BeginAnimation(ScaleTransform.ScaleXProperty, null);
                scale.ScaleX = toScale;
                return;
            }

            scale.BeginAnimation(ScaleTransform.ScaleXProperty, new DoubleAnimation(fromScale, toScale, AnimationDuration)
            {
                EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseOut }
            });
        }
    }
}
