using System.Windows;
using System.Windows.Controls;

namespace OpenIsland.App.Controls;

/// <summary>
/// A Border whose corner radius mirrors an animatable double so the island can morph
/// between the closed pill radius (h/2) and the opened panel radius with a single
/// DoubleAnimation (WPF has no CornerRadiusAnimation).
/// </summary>
public class AnimatedCornerBorder : Border
{
    public static readonly DependencyProperty AnimatedCornerRadiusProperty = DependencyProperty.Register(
        nameof(AnimatedCornerRadius), typeof(double), typeof(AnimatedCornerBorder),
        new PropertyMetadata(double.NaN, OnAnimatedCornerRadiusChanged));

    public double AnimatedCornerRadius
    {
        get => (double)GetValue(AnimatedCornerRadiusProperty);
        set => SetValue(AnimatedCornerRadiusProperty, value);
    }

    private static void OnAnimatedCornerRadiusChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is AnimatedCornerBorder border && e.NewValue is double radius && !double.IsNaN(radius))
        {
            border.CornerRadius = new CornerRadius(radius);
        }
    }
}
