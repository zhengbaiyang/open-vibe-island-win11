using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;

namespace OpenIsland.App.Controls;

public enum BarsMode
{
    /// <summary>Rest: three short static bars.</summary>
    Idle,
    /// <summary>An agent is working: bars run a looping wave.</summary>
    Running,
    /// <summary>An agent needs attention: outer bars breathe, middle bar is hidden.</summary>
    Waiting,
    /// <summary>All agents finished: bars settle low.</summary>
    Completed
}

/// <summary>
/// The macOS Open Island "UnifiedBars" glyph ported to WPF.
/// Geometry mirrors the upstream SwiftUI implementation (24x24 box, three 2.5px bars,
/// columns at x = 5.25 / 10.75 / 16.25) so the closed pill keeps the same silhouette;
/// the run/wait cycles mirror the upstream Core Animation keyframes.
/// </summary>
public partial class BarsGlyph : UserControl
{
    private const double RunDurationSeconds = 0.9;
    private const double WaitDurationSeconds = 1.8;

    public static readonly DependencyProperty ModeProperty = DependencyProperty.Register(
        nameof(Mode), typeof(BarsMode), typeof(BarsGlyph),
        new PropertyMetadata(BarsMode.Idle, OnVisualPropertyChanged));

    public static readonly DependencyProperty TintProperty = DependencyProperty.Register(
        nameof(Tint), typeof(Brush), typeof(BarsGlyph),
        new PropertyMetadata(new SolidColorBrush(Color.FromRgb(0xF1, 0xEA, 0xD9)), OnVisualPropertyChanged));

    public static readonly DependencyProperty AnimateProperty = DependencyProperty.Register(
        nameof(Animate), typeof(bool), typeof(BarsGlyph),
        new PropertyMetadata(true, OnVisualPropertyChanged));

    public BarsMode Mode
    {
        get => (BarsMode)GetValue(ModeProperty);
        set => SetValue(ModeProperty, value);
    }

    public Brush Tint
    {
        get => (Brush)GetValue(TintProperty);
        set => SetValue(TintProperty, value);
    }

    /// <summary>When false, the glyph renders its idle frame (used for previews / reduced motion).</summary>
    public bool Animate
    {
        get => (bool)GetValue(AnimateProperty);
        set => SetValue(AnimateProperty, value);
    }

    public BarsGlyph()
    {
        InitializeComponent();
        Loaded += (_, _) => Apply();
    }

    private static void OnVisualPropertyChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        => ((BarsGlyph)d).Apply();

    private void Apply()
    {
        if (!IsLoaded) return;

        Bar0.Background = Tint;
        Bar1.Background = Tint;
        Bar2.Background = Tint;

        // Reset animated properties to their base values first.
        Scale0.BeginAnimation(ScaleTransform.ScaleYProperty, null);
        Scale1.BeginAnimation(ScaleTransform.ScaleYProperty, null);
        Scale2.BeginAnimation(ScaleTransform.ScaleYProperty, null);
        Bar0.BeginAnimation(OpacityProperty, null);
        Bar1.BeginAnimation(OpacityProperty, null);
        Bar2.BeginAnimation(OpacityProperty, null);

        Bar0.Visibility = Visibility.Visible;
        Bar1.Visibility = Visibility.Visible;
        Bar2.Visibility = Visibility.Visible;
        Bar0.Opacity = 1;
        Bar1.Opacity = 1;
        Bar2.Opacity = 1;

        var mode = Animate ? Mode : BarsMode.Idle;

        switch (mode)
        {
            case BarsMode.Idle:
                Scale0.ScaleY = 3.0 / 12.0;
                Scale1.ScaleY = 5.0 / 14.0;
                Scale2.ScaleY = 3.0 / 10.0;
                break;

            case BarsMode.Completed:
                Scale0.ScaleY = 5.0 / 12.0;
                Scale1.ScaleY = 9.0 / 14.0;
                Scale2.ScaleY = 5.0 / 10.0;
                break;

            case BarsMode.Running:
                AnimateWave(Scale0, 4.0 / 12.0, 12.0 / 12.0, 4.0 / 12.0, 0.0);
                AnimateWave(Scale1, 6.0 / 14.0, 14.0 / 14.0, 6.0 / 14.0, 0.15);
                AnimateWave(Scale2, 4.0 / 10.0, 10.0 / 10.0, 4.0 / 10.0, 0.30);
                break;

            case BarsMode.Waiting:
                Bar1.Visibility = Visibility.Collapsed;
                Scale0.ScaleY = 10.0 / 12.0;
                Scale2.ScaleY = 10.0 / 10.0;
                AnimateBreathe(Bar0, 0.0);
                AnimateBreathe(Bar2, WaitDurationSeconds / 2.0);
                break;
        }
    }

    private static void AnimateWave(ScaleTransform scale, double from, double mid, double to, double delaySeconds)
    {
        var animation = new DoubleAnimationUsingKeyFrames
        {
            Duration = TimeSpan.FromSeconds(RunDurationSeconds),
            RepeatBehavior = RepeatBehavior.Forever,
            BeginTime = TimeSpan.FromSeconds(delaySeconds)
        };
        var ease = new SineEase { EasingMode = EasingMode.EaseInOut };
        animation.KeyFrames.Add(new EasingDoubleKeyFrame(from, KeyTime.FromPercent(0.0), ease));
        animation.KeyFrames.Add(new EasingDoubleKeyFrame(mid, KeyTime.FromPercent(0.5), ease));
        animation.KeyFrames.Add(new EasingDoubleKeyFrame(to, KeyTime.FromPercent(1.0), ease));
        scale.BeginAnimation(ScaleTransform.ScaleYProperty, animation, HandoffBehavior.SnapshotAndReplace);
    }

    private static void AnimateBreathe(UIElement element, double delaySeconds)
    {
        var animation = new DoubleAnimation
        {
            From = 0.55,
            To = 1.0,
            Duration = TimeSpan.FromSeconds(WaitDurationSeconds / 2.0),
            AutoReverse = true,
            RepeatBehavior = RepeatBehavior.Forever,
            BeginTime = TimeSpan.FromSeconds(delaySeconds),
            EasingFunction = new SineEase { EasingMode = EasingMode.EaseInOut }
        };
        element.BeginAnimation(OpacityProperty, animation, HandoffBehavior.SnapshotAndReplace);
    }
}
