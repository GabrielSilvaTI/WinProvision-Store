using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Media.Animation;

namespace WinProvision.Store.Behaviors;

/// <summary>Transições curtas entre estados visuais, respeitando a preferência de movimento do Windows.</summary>
internal static class StateTransition
{
    private static readonly ConditionalWeakTable<FrameworkElement, TransitionState> States = new();
    private static readonly TimeSpan Duration = TimeSpan.FromMilliseconds(140);

    public static void SetVisibility(FrameworkElement element, Visibility target)
    {
        ArgumentNullException.ThrowIfNull(element);

        var state = States.GetOrCreateValue(element);
        if (element.Visibility == target && !state.IsAnimating)
            return;

        state.Generation++;
        int generation = state.Generation;
        state.IsAnimating = false;
        element.BeginAnimation(UIElement.OpacityProperty, null);

        if (!element.IsLoaded || !SystemParameters.ClientAreaAnimation)
        {
            element.Visibility = target;
            element.Opacity = 1;
            return;
        }

        if (element.Visibility == target)
            return;

        if (target != Visibility.Visible)
        {
            if (element.Visibility != Visibility.Visible)
            {
                element.Visibility = target;
                element.Opacity = 1;
                return;
            }

            state.IsAnimating = true;
            var fadeOut = new DoubleAnimation(1, 0, Duration)
            {
                EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut },
                FillBehavior = FillBehavior.Stop
            };
            fadeOut.Completed += (_, _) =>
            {
                if (state.Generation != generation)
                    return;

                element.BeginAnimation(UIElement.OpacityProperty, null);
                element.Opacity = 1;
                element.Visibility = target;
                state.IsAnimating = false;
            };
            element.BeginAnimation(UIElement.OpacityProperty, fadeOut, HandoffBehavior.SnapshotAndReplace);
            return;
        }

        element.Visibility = Visibility.Visible;
        element.Opacity = 0;
        state.IsAnimating = true;
        var fadeIn = new DoubleAnimation(0, 1, Duration)
        {
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut },
            FillBehavior = FillBehavior.Stop
        };
        fadeIn.Completed += (_, _) =>
        {
            if (state.Generation != generation)
                return;

            element.BeginAnimation(UIElement.OpacityProperty, null);
            element.Opacity = 1;
            state.IsAnimating = false;
        };
        element.BeginAnimation(UIElement.OpacityProperty, fadeIn, HandoffBehavior.SnapshotAndReplace);
    }

    private sealed class TransitionState
    {
        public int Generation { get; set; }
        public bool IsAnimating { get; set; }
    }
}
