using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Shapes;
using System.Windows.Threading;
using OpenIsland.App.Controls;
using OpenIsland.Core.Localization;
using OpenIsland.Core.Models;
using OpenIsland.Core.Platform;
using OpenIsland.Core.State;
using Forms = System.Windows.Forms;
using Path = System.Windows.Shapes.Path;

namespace OpenIsland.App;

/// <summary>
/// Floating "dynamic island" pill.
/// Visual language and motion are ported from the macOS SwiftUI implementation:
/// spring response .42 / damping .8 opening, smooth .3 closing, 1.028 hover scale,
/// 1.04 pop, 120 ms delayed content fade, 0.45 s width timing curve, and the
/// three-bar UnifiedBars glyph with its run / wait cycles.
/// </summary>
public partial class IslandWindow : Window
{
    private const int HotkeyId = 9001;

    // --- Metrics (DIPs), matching the macOS proportions as closely as WPF allows.
    private const double ExpandedWidth = 468;
    private const double ExpandedCornerRadius = 20;
    private const double CompactCornerRadius = 23;
    private const double HeaderHeight = 46;
    private const double BodyBottomMargin = 15;
    private const double SurfaceBorder = 1;
    private const double ExpandedMeasureWidth = ExpandedWidth - (15 * 2) - (SurfaceBorder * 2);
    private const double ClosedHoverScale = 1.028;
    private const double PopScale = 1.045;

    private static readonly Duration OpenDuration = TimeSpan.FromMilliseconds(420);
    private static readonly Duration CloseDuration = TimeSpan.FromMilliseconds(300);
    private static readonly Duration PopDuration = TimeSpan.FromMilliseconds(320);

    private readonly App _app;
    private readonly DispatcherTimer _collapseTimer;
    private bool _isExpanded;
    private readonly IslandPinState _pinState = new();
    private bool _suppressHoverExpandUntilLeave;
    private bool _surfaceSizeAnimating;
    private double _compactSurfaceWidth;
    private double _compactSurfaceHeight;
    private HwndSource? _hwndSource;
    private string _lastSessionSignature = "";
    private string _lastActionCardsSignature = "";
    private string _lastCompletionCardsSignature = "";
    private readonly HashSet<string> _seenActionCardIds = new(StringComparer.OrdinalIgnoreCase);

    // --- Auto-hide (tuck into the screen edge when idle) -----------------
    private const double HiddenStripHeight = 5;
    private readonly DispatcherTimer _autoHideTimer;
    private readonly DispatcherTimer _revealWatchTimer;
    private bool _autoHideEnabled;
    private int _autoHideDelaySeconds = 30;
    private bool _isAutoHidden;
    private bool _topAnimating;
    private double _lastVisibleTop;
    private double _hiddenTop;

    public IslandWindow(App app)
    {
        InitializeComponent();
        _app = app;

        _collapseTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(420) };
        _collapseTimer.Tick += (_, _) =>
        {
            _collapseTimer.Stop();
            if (!_pinState.IsPinned && !IsMouseOver) SetExpanded(false, animate: true);
        };

        _autoHideTimer = new DispatcherTimer();
        _autoHideTimer.Tick += (_, _) =>
        {
            _autoHideTimer.Stop();
            TryAutoHide();
        };

        _revealWatchTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(120) };
        _revealWatchTimer.Tick += (_, _) => WatchForReveal();

        // Any pointer interaction over the island postpones hiding (and reveals it if hidden).
        PreviewMouseMove += (_, _) => OnPointerInteraction();
        MouseEnter += (_, _) => OnPointerInteraction();
        MouseLeave += (_, _) => ScheduleAutoHide();

        Loaded += IslandWindow_Loaded;
        Closed += IslandWindow_Closed;
        SizeChanged += IslandWindow_SizeChanged;

        _app.Reducer.StateChanged += () => Dispatcher.Invoke(() =>
        {
            OnSessionsChanged();
            RenderState();
        });
        _app.Reducer.ApprovalRequested += _ => Dispatcher.Invoke(() =>
        {
            RevealIsland();
            if (_app.Settings.AutoExpandOnApproval) ExpandFromNotification(pinForAttention: true);
            RenderState();
        });
        _app.Reducer.QuestionRequested += _ => Dispatcher.Invoke(() =>
        {
            RevealIsland();
            if (_app.Settings.AutoExpandOnApproval) ExpandFromNotification(pinForAttention: true);
            RenderState();
        });
        _app.Reducer.TurnCompleted += card => Dispatcher.Invoke(() =>
        {
            if (card.IsInterrupt) return;
            // Expanding a hidden island would animate off-screen; keep it tucked away.
            if (_app.Settings.AutoExpandOnCompletion && !_isAutoHidden) ExpandFromNotification(pinForAttention: false);
            var autoDismiss = new DispatcherTimer
            {
                Interval = TimeSpan.FromSeconds(Math.Clamp(_app.Settings.CompletionCardSeconds, 3, 30))
            };
            autoDismiss.Tick += (_, _) =>
            {
                autoDismiss.Stop();
                _app.Reducer.DismissCompletionCard(card.Id);
                if (!_pinState.IsPinned && !IsMouseOver) SetExpanded(false, animate: true);
            };
            autoDismiss.Start();
            RenderState();
        });
    }

    // =====================================================================
    // Lifecycle
    // =====================================================================

    private void IslandWindow_Loaded(object sender, RoutedEventArgs e)
    {
        var hwnd = new WindowInteropHelper(this).Handle;
        Win32Native.ApplyWindows11OverlayStyle(hwnd, noActivate: false);

        _hwndSource = HwndSource.FromHwnd(hwnd);
        _hwndSource?.AddHook(WndProc);

        Win32Native.RegisterHotKey(hwnd, HotkeyId,
            Win32Native.MOD_CONTROL | Win32Native.MOD_ALT,
            (uint)KeyInterop.VirtualKeyFromKey(Key.I));

        ApplyDisplaySettings();
        RenderState();
        ConfigureAutoHide();

        // Capture the natural (auto-sized) compact surface size once layout settles.
        Dispatcher.BeginInvoke(DispatcherPriority.Loaded, () =>
        {
            CaptureCompactSize();
            RepositionWindow();
            PlayPop(); // subtle entrance pop
            ScheduleAutoHide();
        });
    }

    private void IslandWindow_Closed(object? sender, EventArgs e)
    {
        _autoHideTimer.Stop();
        _revealWatchTimer.Stop();
        var hwnd = new WindowInteropHelper(this).Handle;
        Win32Native.UnregisterHotKey(hwnd, HotkeyId);
        _hwndSource?.RemoveHook(WndProc);
    }

    private void IslandWindow_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        RepositionWindow();
        if (!_isExpanded && !_surfaceSizeAnimating)
        {
            CaptureCompactSize();
        }
    }

    private void CaptureCompactSize()
    {
        if (PillSurface.ActualWidth > 0) _compactSurfaceWidth = PillSurface.ActualWidth;
        if (PillSurface.ActualHeight > 0) _compactSurfaceHeight = PillSurface.ActualHeight;
    }

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == Win32Native.WM_HOTKEY && wParam.ToInt32() == HotkeyId)
        {
            ToggleVisibility();
            handled = true;
        }
        return IntPtr.Zero;
    }

    // =====================================================================
    // Public API
    // =====================================================================

    public void ToggleVisibility()
    {
        if (IsVisible)
        {
            if (_isAutoHidden)
            {
                RevealIsland();
                return;
            }
            Hide();
        }
        else
        {
            Show();
            RepositionWindow();
            ConfigureAutoHide();
        }
    }

    /// <summary>Shows the island and opens it (used by the Settings connection self-test).</summary>
    public void ShowAndExpand()
    {
        if (!IsVisible) Show();
        RevealIsland(animate: false);
        _pinState.PinManually();
        _autoHideTimer.Stop();
        SetExpanded(true, animate: true);
    }

    public void ApplyDisplaySettings()
    {
        var graphite = _app.Settings.ThemeStyle == IslandThemeStyle.Graphite;
        PillSurface.Background = B(graphite ? "#FC14171D" : "#FC0A0C10");
        PillSurface.BorderBrush = B(graphite ? "#26FFFFFF" : "#1FFFFFFF");
        RefreshBtn.ToolTip = Strings.Get("Refresh");
        SettingsBtn.ToolTip = Strings.Get("Settings");
        CompactRefreshBtn.ToolTip = Strings.Get("Refresh");
        CompactSettingsBtn.ToolTip = Strings.Get("Settings");
        // Re-render card copy cleanly if the UI language changed, without re-triggering entry fade.
        _lastActionCardsSignature = "";
        _lastCompletionCardsSignature = "";
        RepositionWindow();
        ConfigureAutoHide();
        RenderState();
    }

    private void RepositionWindow()
    {
        if (!IsVisible) return;

        var placement = ComputePlacement();
        Left = placement.Left;
        _lastVisibleTop = placement.VisibleTop;
        _hiddenTop = placement.HiddenTop;

        // While the hide/reveal animation runs it drives Top itself.
        if (!_topAnimating)
        {
            Top = _isAutoHidden ? _hiddenTop : _lastVisibleTop;
        }
    }

    private (double Left, double VisibleTop, double HiddenTop, double WinWidth, double WinHeight) ComputePlacement()
    {
        var screens = Forms.Screen.AllScreens;
        var idx = Math.Clamp(_app.Settings.MonitorIndex, 0, Math.Max(0, screens.Length - 1));
        var screen = screens.Length > 0 ? screens[idx] : Forms.Screen.PrimaryScreen!;
        var area = screen.Bounds;
        var (scaleX, scaleY) = GetDpiScale();

        double screenLeft = area.Left / scaleX;
        double screenTop = area.Top / scaleY;
        double screenWidth = area.Width / scaleX;
        double screenHeight = area.Height / scaleY;

        double winWidth = ActualWidth > 0 ? ActualWidth : _compactSurfaceWidth + 36;
        double winHeight = ActualHeight > 0 ? ActualHeight : _compactSurfaceHeight + 30;

        var bottom = _app.Settings.EdgePosition == IslandEdgePosition.BottomCenter;
        var visibleTop = bottom
            ? screenTop + screenHeight - winHeight - 52
            : screenTop + 4;
        // Leave a thin clickable strip at the edge so the user knows where to hover.
        var hiddenTop = bottom
            ? screenTop + screenHeight - HiddenStripHeight
            : screenTop - winHeight + HiddenStripHeight;

        var left = screenLeft + ((screenWidth - winWidth) / 2.0);
        return (left, visibleTop, hiddenTop, winWidth, winHeight);
    }

    private (double X, double Y) GetDpiScale()
    {
        var source = PresentationSource.FromVisual(this);
        var matrix = source?.CompositionTarget?.TransformToDevice;
        if (matrix is { M11: > 0, M22: > 0 })
        {
            return (matrix.Value.M11, matrix.Value.M22);
        }
        return (1.0, 1.0);
    }

    // =====================================================================
    // Auto-hide (tuck into the screen edge)
    // =====================================================================

    private void ConfigureAutoHide()
    {
        _autoHideEnabled = _app.Settings.AutoHideIsland;
        _autoHideDelaySeconds = Math.Clamp(_app.Settings.AutoHideDelaySeconds, 5, 300);

        if (!_autoHideEnabled)
        {
            _autoHideTimer.Stop();
            _revealWatchTimer.Stop();
            if (_isAutoHidden) RevealIsland(animate: false);
            return;
        }

        if (_isAutoHidden)
        {
            _revealWatchTimer.Start();
        }
        else
        {
            ScheduleAutoHide();
        }
    }

    private void ScheduleAutoHide()
    {
        _autoHideTimer.Stop();
        if (!_autoHideEnabled || _isAutoHidden || _isExpanded || _pinState.IsPinned || !IsVisible) return;
        if (HasPendingAttention()) return;

        _autoHideTimer.Interval = TimeSpan.FromSeconds(_autoHideDelaySeconds);
        _autoHideTimer.Start();
    }

    private bool HasPendingAttention()
        => _app.Reducer.GetActiveSessions()
            .Any(s => s.PendingApproval != null || s.PendingQuestion != null);

    private void OnPointerInteraction()
    {
        if (_isAutoHidden)
        {
            RevealIsland();
            return;
        }
        if (_isExpanded) return;
        ScheduleAutoHide();
    }

    private void OnSessionsChanged()
    {
        bool hasPending = HasPendingAttention();
        // Never stay tucked away while the user is asked to approve something.
        if (_isAutoHidden && hasPending)
        {
            RevealIsland();
            return;
        }

        if (!hasPending)
        {
            if (_pinState.ReleaseAutoPinIfNoAttention(hasPending))
            {
                UpdateHeaderPinAndChevronState();
                bool hasActiveCompletion = _app.Reducer.GetRecentCompletions().Any(c => !c.IsInterrupt);
                if (_isExpanded && !IsMouseOver && !hasActiveCompletion)
                {
                    _collapseTimer.Stop();
                    SetExpanded(false, animate: true);
                }
            }

            if (!_isExpanded && !_pinState.IsPinned)
            {
                ScheduleAutoHide();
            }
        }
    }

    private void TryAutoHide()
    {
        if (!_autoHideEnabled || _isAutoHidden || _isExpanded || _pinState.IsPinned || !IsVisible) return;
        if (IsMouseOver) { ScheduleAutoHide(); return; }
        if (HasPendingAttention()) return;

        var placement = ComputePlacement();
        _lastVisibleTop = placement.VisibleTop;
        _hiddenTop = placement.HiddenTop;
        Left = placement.Left;
        _isAutoHidden = true;
        _revealWatchTimer.Start();
        AnimateTop(_hiddenTop, 320);
    }

    private void RevealIsland(bool animate = true)
    {
        if (!_isAutoHidden && !_revealWatchTimer.IsEnabled) return;
        _isAutoHidden = false;
        _revealWatchTimer.Stop();

        var placement = ComputePlacement();
        _lastVisibleTop = placement.VisibleTop;
        _hiddenTop = placement.HiddenTop;
        Left = placement.Left;

        if (animate) AnimateTop(_lastVisibleTop, 260, ScheduleAutoHide);
        else
        {
            BeginAnimation(TopProperty, null);
            Top = _lastVisibleTop;
            _topAnimating = false;
            ScheduleAutoHide();
        }
    }

    private void WatchForReveal()
    {
        if (!_isAutoHidden)
        {
            _revealWatchTimer.Stop();
            return;
        }

        if (IsMouseOver)
        {
            RevealIsland();
            return;
        }

        var placement = ComputePlacement();
        var screen = Forms.Screen.AllScreens[
            Math.Clamp(_app.Settings.MonitorIndex, 0, Math.Max(0, Forms.Screen.AllScreens.Length - 1))];
        var (scaleX, scaleY) = GetDpiScale();
        var cursor = Forms.Cursor.Position;
        double cursorX = cursor.X / scaleX;
        double cursorY = cursor.Y / scaleY;
        double screenTop = screen.Bounds.Top / scaleY;
        double screenBottom = screen.Bounds.Bottom / scaleY;
        bool bottom = _app.Settings.EdgePosition == IslandEdgePosition.BottomCenter;

        bool inX = cursorX >= placement.Left - 80 && cursorX <= placement.Left + placement.WinWidth + 80;
        bool atEdge = bottom
            ? cursorY >= screenBottom - HiddenStripHeight - 6
            : cursorY <= screenTop + HiddenStripHeight + 6;

        if (inX && atEdge) RevealIsland();
    }

    private void AnimateTop(double target, int durationMs, Action? completed = null)
    {
        _topAnimating = true;
        var animation = new DoubleAnimation(Top, target, TimeSpan.FromMilliseconds(durationMs))
        {
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut },
            FillBehavior = FillBehavior.HoldEnd
        };
        animation.Completed += (_, _) =>
        {
            BeginAnimation(TopProperty, null);
            Top = target;
            _topAnimating = false;
            completed?.Invoke();
        };
        BeginAnimation(TopProperty, animation);
    }

    // =====================================================================
    // Expand / collapse motion
    // =====================================================================

    private void ExpandFromNotification(bool pinForAttention)
    {
        if (pinForAttention) _pinState.OnAttentionNotification();
        if (!IsVisible) Show();

        // Defer until the current dispatcher operation (and any in-flight layout) completes;
        // expanding from inside a bridge callback occasionally raced with initial layout on
        // a freshly launched instance.
        Dispatcher.BeginInvoke(DispatcherPriority.Background, new Action(() =>
        {
            if (pinForAttention && !HasPendingAttention() && !_pinState.IsPinned) return;
            try
            {
                SetExpanded(true, animate: true);
            }
            catch (Exception ex)
            {
                Core.Diagnostics.AppLog.Error("auto-expand failed, falling back to instant open", ex);
                try { SetExpanded(true, animate: false); } catch { }
            }
        }));
    }

    private void SetExpanded(bool expanded, bool animate)
    {
        if (_isExpanded == expanded)
        {
            UpdateHeaderPinAndChevronState();
            if (expanded) EnsureExpandedHeight();
            return;
        }

        _isExpanded = expanded;
        if (expanded)
        {
            _autoHideTimer.Stop();
            _revealWatchTimer.Stop();
            if (_isAutoHidden) RevealIsland(animate: false);
            Expand(animate);
        }
        else
        {
            Collapse(animate);
            ScheduleAutoHide();
        }
    }

    private void Expand(bool animate)
    {
        StopSurfaceAnimations();

        ExpandedBody.Visibility = Visibility.Visible;
        if (animate)
        {
            ExpandedBody.Opacity = 0;
            ExpandedTranslate.Y = -8;
        }

        ExpandedBody.Measure(new Size(ExpandedMeasureWidth, double.PositiveInfinity));
        double targetHeight = HeaderHeight + BodyBottomMargin + (SurfaceBorder * 2) + ExpandedBody.DesiredSize.Height;
        double fromWidth = PillSurface.ActualWidth > 0 ? PillSurface.ActualWidth : _compactSurfaceWidth;
        double fromHeight = PillSurface.ActualHeight > 0 ? PillSurface.ActualHeight : _compactSurfaceHeight;

        AnimateChevron(180, animate);
        UpdateHeaderPinAndChevronState();

        if (!animate)
        {
            PillSurface.Width = ExpandedWidth;
            PillSurface.Height = targetHeight;
            PillSurface.AnimatedCornerRadius = ExpandedCornerRadius;
            ExpandedBody.Opacity = 1;
            ExpandedTranslate.Y = 0;
            _surfaceSizeAnimating = false;
            RepositionWindow();
            return;
        }

        _surfaceSizeAnimating = true;
        var ease = new BackEase { Amplitude = 0.045, EasingMode = EasingMode.EaseOut };
        AnimateProperty(PillSurface, FrameworkElement.WidthProperty, fromWidth, ExpandedWidth, OpenDuration, ease,
            () => PillSurface.Width = ExpandedWidth);
        AnimateProperty(PillSurface, FrameworkElement.HeightProperty, fromHeight, targetHeight, OpenDuration, ease,
            () => { PillSurface.Height = targetHeight; _surfaceSizeAnimating = false; });
        AnimateCornerRadius(PillSurface, PillSurface.CornerRadius.TopLeft, ExpandedCornerRadius, OpenDuration, ease);

        var bodyEase = new CubicEase { EasingMode = EasingMode.EaseOut };
        ExpandedBody.BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(230))
        {
            BeginTime = TimeSpan.FromMilliseconds(150),
            EasingFunction = bodyEase,
            FillBehavior = FillBehavior.HoldEnd
        });
        ExpandedTranslate.BeginAnimation(TranslateTransform.YProperty, new DoubleAnimation(-8, 0, TimeSpan.FromMilliseconds(250))
        {
            BeginTime = TimeSpan.FromMilliseconds(120),
            EasingFunction = bodyEase,
            FillBehavior = FillBehavior.HoldEnd
        });
    }

    private void Collapse(bool animate)
    {
        StopSurfaceAnimations();
        AnimateChevron(0, animate);
        UpdateHeaderPinAndChevronState();

        if (!animate)
        {
            ExpandedBody.Visibility = Visibility.Collapsed;
            ExpandedBody.Opacity = 0;
            ResetSurfaceToAuto();
            return;
        }

        _surfaceSizeAnimating = true;
        ExpandedBody.BeginAnimation(OpacityProperty, new DoubleAnimation(ExpandedBody.Opacity, 0, TimeSpan.FromMilliseconds(120))
        {
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseIn },
            FillBehavior = FillBehavior.HoldEnd
        });

        double targetWidth = _compactSurfaceWidth > 0 ? _compactSurfaceWidth : Math.Min(ExpandedWidth, 300);
        double targetHeight = _compactSurfaceHeight > 0 ? _compactSurfaceHeight : HeaderHeight + (SurfaceBorder * 2);
        double fromWidth = PillSurface.ActualWidth > 0 ? PillSurface.ActualWidth : ExpandedWidth;
        double fromHeight = PillSurface.ActualHeight > 0 ? PillSurface.ActualHeight : targetHeight;
        var ease = new SineEase { EasingMode = EasingMode.EaseInOut };

        AnimateProperty(PillSurface, FrameworkElement.WidthProperty, fromWidth, targetWidth, CloseDuration, ease, null,
            () => PillSurface.Width = double.NaN);
        AnimateProperty(PillSurface, FrameworkElement.HeightProperty, fromHeight, targetHeight, CloseDuration, ease,
            () =>
            {
                ExpandedBody.Visibility = Visibility.Collapsed;
                ExpandedBody.Opacity = 0;
                PillSurface.BeginAnimation(FrameworkElement.HeightProperty, null);
                PillSurface.Height = double.NaN;
                PillSurface.BeginAnimation(FrameworkElement.WidthProperty, null);
                PillSurface.Width = double.NaN;
                _surfaceSizeAnimating = false;
                RepositionWindow();
            },
            null);
        AnimateCornerRadius(PillSurface, PillSurface.CornerRadius.TopLeft, CompactCornerRadius, CloseDuration, ease);
    }

    private void ResetSurfaceToAuto()
    {
        PillSurface.BeginAnimation(FrameworkElement.WidthProperty, null);
        PillSurface.BeginAnimation(FrameworkElement.HeightProperty, null);
        PillSurface.Width = double.NaN;
        PillSurface.Height = double.NaN;
        PillSurface.AnimatedCornerRadius = CompactCornerRadius;
        _surfaceSizeAnimating = false;
        RepositionWindow();
    }

    /// <summary>Re-measures the expanded drawer and glides the surface to its new height.</summary>
    private void EnsureExpandedHeight()
    {
        if (!IsLoaded || !_isExpanded || _surfaceSizeAnimating) return;

        ExpandedBody.Measure(new Size(ExpandedMeasureWidth, double.PositiveInfinity));
        double targetHeight = HeaderHeight + BodyBottomMargin + (SurfaceBorder * 2) + ExpandedBody.DesiredSize.Height;
        if (Math.Abs(targetHeight - PillSurface.ActualHeight) < 1.5) return;

        _surfaceSizeAnimating = true;
        var ease = new CubicEase { EasingMode = EasingMode.EaseInOut };
        AnimateProperty(PillSurface, FrameworkElement.HeightProperty, PillSurface.ActualHeight, targetHeight,
            TimeSpan.FromMilliseconds(240), ease,
            () => { PillSurface.Height = targetHeight; _surfaceSizeAnimating = false; });
    }

    private void StopSurfaceAnimations()
    {
        PillSurface.BeginAnimation(FrameworkElement.WidthProperty, null);
        PillSurface.BeginAnimation(FrameworkElement.HeightProperty, null);
        PillSurface.BeginAnimation(Border.CornerRadiusProperty, null);
    }

    private static void AnimateProperty(DependencyObject target, DependencyProperty property,
        double from, double to, Duration duration, IEasingFunction? ease,
        Action? onCompleted = null, Action? beforeBaseSet = null)
    {
        if (target is not IAnimatable animatable) return;

        var animation = new DoubleAnimation(from, to, duration)
        {
            EasingFunction = ease,
            FillBehavior = FillBehavior.Stop
        };
        if (onCompleted != null || beforeBaseSet != null)
        {
            animation.Completed += (_, _) =>
            {
                beforeBaseSet?.Invoke();
                onCompleted?.Invoke();
            };
        }
        animatable.BeginAnimation(property, animation, HandoffBehavior.SnapshotAndReplace);
    }

    private static void AnimateCornerRadius(AnimatedCornerBorder border, double from, double to, Duration duration, IEasingFunction? ease)
    {
        if (from <= 0) from = to;
        var animation = new DoubleAnimation(from, to, duration)
        {
            EasingFunction = ease,
            FillBehavior = FillBehavior.Stop
        };
        animation.Completed += (_, _) =>
        {
            border.BeginAnimation(AnimatedCornerBorder.AnimatedCornerRadiusProperty, null);
            border.AnimatedCornerRadius = to;
        };
        border.BeginAnimation(AnimatedCornerBorder.AnimatedCornerRadiusProperty, animation, HandoffBehavior.SnapshotAndReplace);
    }

    private void AnimateChevron(double angle, bool animate)
    {
        var duration = TimeSpan.FromMilliseconds(animate ? 260 : 0);
        var animation = new DoubleAnimation(ChevronRotate.Angle, angle, duration)
        {
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseInOut },
            FillBehavior = FillBehavior.HoldEnd
        };
        ChevronRotate.BeginAnimation(RotateTransform.AngleProperty, animation);
    }

    // =====================================================================
    // Pointer interaction
    // =====================================================================

    private void Window_MouseEnter(object sender, MouseEventArgs e)
    {
        _collapseTimer.Stop();
        if (!_isExpanded && !_suppressHoverExpandUntilLeave)
        {
            SetExpanded(true, animate: true);
        }
        AnimateHover(_isExpanded ? 1.0 : ClosedHoverScale);
    }

    private void Window_MouseLeave(object sender, MouseEventArgs e)
    {
        _suppressHoverExpandUntilLeave = false;
        AnimateHover(1.0);
        if (!_pinState.IsPinned) _collapseTimer.Start();
    }

    private void PinToggleBadge_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        _pinState.ToggleManualPin();
        if (_pinState.IsPinned)
        {
            _suppressHoverExpandUntilLeave = false;
            _collapseTimer.Stop();
            if (!_isExpanded) SetExpanded(true, animate: true);
        }
        UpdateHeaderPinAndChevronState();
        e.Handled = true;
    }

    private void ChevronBtn_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (!_isExpanded)
        {
            _suppressHoverExpandUntilLeave = false;
            _pinState.PinManually();
            SetExpanded(true, animate: true);
        }
        else
        {
            // Explicitly clicking the top-right arrow always unpins and collapses immediately,
            // even while the mouse is still hovering over the header.
            _pinState.Unpin();
            _suppressHoverExpandUntilLeave = true;
            _collapseTimer.Stop();
            SetExpanded(false, animate: true);
        }
        UpdateHeaderPinAndChevronState();
        e.Handled = true;
    }

    private void CompactHeader_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (!_isExpanded)
        {
            _suppressHoverExpandUntilLeave = false;
            _pinState.PinManually();
            SetExpanded(true, animate: true);
        }
        else if (_pinState.IsPinned)
        {
            _pinState.Unpin();
            _suppressHoverExpandUntilLeave = true;
            _collapseTimer.Stop();
            SetExpanded(false, animate: true);
        }
        else
        {
            // Hover-opened: clicking the header pins it open and lights up the pinned indicator.
            _pinState.PinManually();
        }
        UpdateHeaderPinAndChevronState();
        e.Handled = true;
    }

    private void UpdateHeaderPinAndChevronState()
    {
        if (PinToggleBadge is null || Chevron is null || ChevronBtn is null) return;

        PinToggleBadge.Visibility = _isExpanded ? Visibility.Visible : Visibility.Collapsed;
        if (_pinState.IsPinned)
        {
            PinToggleBadge.Background = B("#286EA7FF");
            PinToggleBadge.BorderBrush = B("#786EA7FF");
            PinStatusDot.Fill = B("#7FC9FF");
            PinStatusText.Text = Strings.Get("PinnedLabel");
            PinStatusText.Foreground = B("#CBE6FF");
            PinToggleBadge.ToolTip = Strings.Get("UnpinToolTip");
            Chevron.Stroke = B("#8CC2FF");
            ChevronBtn.Background = B("#166EA7FF");
        }
        else
        {
            PinToggleBadge.Background = B("#0FFFFFFF");
            PinToggleBadge.BorderBrush = B("#1FFFFFFF");
            PinStatusDot.Fill = B("#6F7B8F");
            PinStatusText.Text = Strings.Get("UnpinnedLabel");
            PinStatusText.Foreground = B("#8A95A6");
            PinToggleBadge.ToolTip = Strings.Get("PinToolTip");
            Chevron.Stroke = B("#8A95A6");
            ChevronBtn.Background = Brushes.Transparent;
        }

        ChevronBtn.ToolTip = Strings.Get(_isExpanded ? "CollapseToolTip" : "ExpandPinToolTip");
    }

    private void JumpAndCollapse(AgentSession session)
    {
        TerminalJump(session);
        _pinState.Unpin();
        _suppressHoverExpandUntilLeave = true;
        _collapseTimer.Stop();
        if (_isExpanded) SetExpanded(false, animate: true);
        UpdateHeaderPinAndChevronState();
    }

    private void AnimateHover(double target)
    {
        var duration = TimeSpan.FromMilliseconds(260);
        var ease = new BackEase { Amplitude = 0.08, EasingMode = EasingMode.EaseOut };
        HoverScale.BeginAnimation(ScaleTransform.ScaleXProperty,
            new DoubleAnimation(target, duration) { EasingFunction = ease, FillBehavior = FillBehavior.HoldEnd });
        HoverScale.BeginAnimation(ScaleTransform.ScaleYProperty,
            new DoubleAnimation(target, duration) { EasingFunction = ease, FillBehavior = FillBehavior.HoldEnd });
    }

    private void PlayPop()
    {
        var animation = new DoubleAnimationUsingKeyFrames { FillBehavior = FillBehavior.Stop };
        animation.KeyFrames.Add(new EasingDoubleKeyFrame(1.0, KeyTime.FromTimeSpan(TimeSpan.Zero)));
        animation.KeyFrames.Add(new EasingDoubleKeyFrame(PopScale, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(110)),
            new BackEase { Amplitude = 0.4, EasingMode = EasingMode.EaseOut }));
        animation.KeyFrames.Add(new EasingDoubleKeyFrame(1.0, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(320)),
            new CubicEase { EasingMode = EasingMode.EaseInOut }));
        HoverScale.BeginAnimation(ScaleTransform.ScaleXProperty, animation, HandoffBehavior.SnapshotAndReplace);
        HoverScale.BeginAnimation(ScaleTransform.ScaleYProperty, animation.Clone(), HandoffBehavior.SnapshotAndReplace);
    }

    private void RenderState()
    {
        var sessions = _app.Reducer.GetActiveSessions();
        var completions = _app.Reducer.GetRecentCompletions();

        int pendingApprovalOnly = sessions.Count(s => s.PendingApproval != null);
        int pendingQuestionOnly = sessions.Count(s => s.PendingQuestion != null);
        int approvalCount = sessions.Count(s => s.PendingApproval != null || s.PendingQuestion != null);
        int runningCount = sessions.Count(s => s.Phase == SessionPhase.Running);
        int waitingCount = sessions.Count(s => s.Phase is SessionPhase.WaitingForApproval or SessionPhase.WaitingForAnswer);

        // ---- Header status -------------------------------------------------
        string status;
        if (pendingApprovalOnly > 0)
        {
            status = Strings.Get("ApprovalCount", approvalCount);
            AlertChip.Visibility = Visibility.Collapsed;
            HeaderStatus.Foreground = B("#FFD58A");
        }
        else if (pendingQuestionOnly > 0)
        {
            status = Strings.Get("QuestionCount", pendingQuestionOnly);
            AlertChip.Visibility = Visibility.Collapsed;
            HeaderStatus.Foreground = B("#7FC9FF");
        }
        else if (waitingCount > 0)
        {
            status = Strings.Get("PendingApproval");
            AlertChip.Visibility = Visibility.Collapsed;
            HeaderStatus.Foreground = B("#FFD58A");
        }
        else if (runningCount > 0)
        {
            status = Strings.Get("RunningCount", runningCount);
            AlertChip.Visibility = Visibility.Collapsed;
            HeaderStatus.Foreground = B("#7D8899");
        }
        else if (sessions.Count > 0)
        {
            status = Strings.Get("ActiveCount", sessions.Count);
            AlertChip.Visibility = Visibility.Collapsed;
            HeaderStatus.Foreground = B("#7D8899");
        }
        else
        {
            status = Strings.Get("CompactIdle");
            AlertChip.Visibility = Visibility.Collapsed;
            HeaderStatus.Foreground = B("#7D8899");
        }
        HeaderStatus.Text = status;

        // ---- Bars glyph state ---------------------------------------------
        HeaderBars.Mode = approvalCount > 0
            ? BarsMode.Waiting
            : runningCount > 0
                ? BarsMode.Running
                : sessions.Count > 0
                    ? BarsMode.Completed
                    : BarsMode.Idle;
        HeaderBars.Tint = approvalCount > 0 ? B("#FFD58A") : B("#F1EAD9");
        EmptyBars.Tint = B("#D9C9A0");

        // ---- Localized chrome ---------------------------------------------
        LiveSessionsLabel.Text = Strings.Get("LiveSessions");
        EmptyTitle.Text = Strings.Get("NoSessionsTitle");
        EmptyHint.Text = Strings.Get("NoSessionsHint");
        SessionCountText.Text = sessions.Count.ToString();
        UpdateHeaderPinAndChevronState();

        // ---- Action cards (approvals + HITL questions) ---------------------
        // Diff by RequestId signature so periodic discovery polls never destroy & re-animate
        // (flash) existing cards or clear the user's in-progress multi-select state.
        var activeCardIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var actionSigParts = new List<string>();
        foreach (var s in sessions)
        {
            if (s.PendingApproval != null)
            {
                activeCardIds.Add(s.PendingApproval.RequestId);
                actionSigParts.Add($"A:{s.SessionId}:{s.PendingApproval.RequestId}:{s.PendingApproval.IsBlocking}:{s.PendingApproval.Options.Count}");
            }
            if (s.PendingQuestion != null)
            {
                activeCardIds.Add(s.PendingQuestion.RequestId);
                actionSigParts.Add($"Q:{s.SessionId}:{s.PendingQuestion.RequestId}:{s.PendingQuestion.IsBlocking}:{s.PendingQuestion.Questions.Count}");
            }
        }
        var actionSignature = string.Join("|", actionSigParts);
        if (actionSignature != _lastActionCardsSignature)
        {
            _lastActionCardsSignature = actionSignature;
            ActionCardsHost.Children.Clear();
            foreach (var s in sessions)
            {
                if (s.PendingApproval != null)
                {
                    bool animateIn = _seenActionCardIds.Add(s.PendingApproval.RequestId);
                    ActionCardsHost.Children.Add(BuildApprovalCard(s, s.PendingApproval, animateIn));
                }
                if (s.PendingQuestion != null)
                {
                    bool animateIn = _seenActionCardIds.Add(s.PendingQuestion.RequestId);
                    ActionCardsHost.Children.Add(BuildQuestionCard(s, s.PendingQuestion, animateIn));
                }
            }
            _seenActionCardIds.RemoveWhere(id => !activeCardIds.Contains(id));
        }

        // ---- Completion cards ---------------------------------------------
        var visibleCompletions = completions.Take(2).ToList();
        var completionSignature = string.Join("|", visibleCompletions.Select(c => c.Id));
        if (completionSignature != _lastCompletionCardsSignature)
        {
            _lastCompletionCardsSignature = completionSignature;
            CompletionCardsHost.Children.Clear();
            foreach (var card in visibleCompletions)
            {
                CompletionCardsHost.Children.Add(BuildCompletionCard(card));
            }
        }

        // ---- Session rows / empty state ------------------------------------
        SessionsHost.Children.Clear();
        bool hasSessions = sessions.Count > 0;
        SessionsScroll.Visibility = hasSessions ? Visibility.Visible : Visibility.Collapsed;
        EmptyStatePanel.Visibility = hasSessions ? Visibility.Collapsed : Visibility.Visible;

        foreach (var session in sessions)
        {
            SessionsHost.Children.Add(BuildSessionRow(session));
        }

        // ---- Content-height glide + pop on compact state changes -----------
        if (_isExpanded)
        {
            Dispatcher.BeginInvoke(DispatcherPriority.Loaded, EnsureExpandedHeight);
        }
        else
        {
            var signature = $"{sessions.Count}|{approvalCount}|{runningCount}";
            if (signature != _lastSessionSignature && _lastSessionSignature.Length > 0)
            {
                PlayPop();
            }
            _lastSessionSignature = signature;
        }
    }

    // ---------------------------------------------------------------------
    // Cards
    // ---------------------------------------------------------------------

    private UIElement BuildApprovalCard(AgentSession session, PendingApprovalRequest req, bool animateIn = true)
    {
        var meta = AgentRegistry.Get(req.Source);
        var card = new Border
        {
            Background = B("#16F4A4A4"),
            BorderBrush = B("#5CF4A4A4"),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(12),
            Padding = new Thickness(12, 11, 12, 11),
            Margin = new Thickness(0, 0, 0, 8)
        };
        var stack = new StackPanel();

        var header = new WrapPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        header.Children.Add(IconPath("IconAlert", 13, "#F4A4A4", margin: new Thickness(0, 0, 7, 3)));
        header.Children.Add(new TextBlock
        {
            Text = $"{meta.DisplayName} · {Strings.Get("ApproveHint")}",
            Foreground = B("#FFDCDC"),
            FontWeight = FontWeights.SemiBold,
            FontSize = 12,
            Margin = new Thickness(0, 0, 8, 3),
            VerticalAlignment = VerticalAlignment.Center
        });
        if (!string.IsNullOrWhiteSpace(req.ToolName))
        {
            header.Children.Add(Chip(req.ToolName, "#FFD9D9", "#24F4A4A4", new Thickness(0, 0, 6, 3)));
        }
        if (!req.IsBlocking)
        {
            header.Children.Add(Chip(Strings.Get("ActionInAppBadge"), "#FFD58A", "#22FFD58A", new Thickness(0, 0, 6, 3)));
        }
        stack.Children.Add(header);

        if (!string.IsNullOrWhiteSpace(req.Summary))
        {
            stack.Children.Add(new TextBlock
            {
                Text = req.Summary,
                Foreground = B("#F2F4F8"),
                FontSize = 12.5,
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(0, 6, 0, 0)
            });
        }

        if (!string.IsNullOrWhiteSpace(req.CommandOrDetails))
        {
            stack.Children.Add(new Border
            {
                Background = B("#B307090D"),
                BorderBrush = B("#1FFFFFFF"),
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(8),
                Padding = new Thickness(9, 6, 9, 6),
                Margin = new Thickness(0, 8, 0, 7),
                Child = new TextBlock
                {
                    Text = req.CommandOrDetails,
                    Foreground = B("#DCE3ED"),
                    FontFamily = (FontFamily)FindResource("FontMono"),
                    FontSize = 11,
                    TextWrapping = TextWrapping.Wrap,
                    MaxHeight = 92
                }
            });
        }

        if (req.Options.Count > 0 && req.IsBlocking)
        {
            var optsList = new StackPanel { Margin = new Thickness(0, 6, 0, 2) };
            for (int i = 0; i < req.Options.Count; i++)
            {
                var opt = req.Options[i];
                var (displayLabel, isRecommended) = StripRecommendedPrefix(opt.Label);
                var isDenyOpt = IsDenyLikeLabel(displayLabel);

                var optGrid = new Grid();
                optGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
                optGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

                var idxBadge = new Border
                {
                    Width = 18,
                    Height = 18,
                    CornerRadius = new CornerRadius(5),
                    Background = B(isDenyOpt ? "#26F26D6D" : "#246FB982"),
                    BorderBrush = B(isDenyOpt ? "#55F26D6D" : "#556FB982"),
                    BorderThickness = new Thickness(1),
                    VerticalAlignment = VerticalAlignment.Top,
                    Margin = new Thickness(0, 1, 8, 0),
                    Child = new TextBlock
                    {
                        Text = (i + 1).ToString(),
                        FontSize = 10,
                        FontWeight = FontWeights.SemiBold,
                        Foreground = B(isDenyOpt ? "#F6B3B3" : "#A8E0B9"),
                        HorizontalAlignment = HorizontalAlignment.Center,
                        VerticalAlignment = VerticalAlignment.Center
                    }
                };
                Grid.SetColumn(idxBadge, 0);
                optGrid.Children.Add(idxBadge);

                var optTextStack = new StackPanel();
                if (isRecommended)
                {
                    var recBadge = Chip(Strings.Get("RecommendedBadge"), "#8ED7A5", "#246FB982", new Thickness(0, 0, 0, 3));
                    recBadge.HorizontalAlignment = HorizontalAlignment.Left;
                    optTextStack.Children.Add(recBadge);
                }
                optTextStack.Children.Add(new TextBlock
                {
                    Text = displayLabel,
                    Foreground = B("#F2F4F8"),
                    FontSize = 11.5,
                    TextWrapping = TextWrapping.Wrap,
                    LineHeight = 16
                });
                if (!string.IsNullOrWhiteSpace(opt.Description))
                {
                    optTextStack.Children.Add(new TextBlock
                    {
                        Text = opt.Description,
                        Foreground = B("#8A95A6"),
                        FontSize = 10.5,
                        TextWrapping = TextWrapping.Wrap,
                        Margin = new Thickness(0, 2, 0, 0)
                    });
                }
                Grid.SetColumn(optTextStack, 1);
                optGrid.Children.Add(optTextStack);

                var baseBg = "#12FFFFFF";
                var hoverBg = isDenyOpt ? "#22F26D6D" : "#226FB982";
                var optCard = new Border
                {
                    Background = B(baseBg),
                    BorderBrush = B("#22FFFFFF"),
                    BorderThickness = new Thickness(1),
                    CornerRadius = new CornerRadius(8),
                    Padding = new Thickness(9, 6, 10, 6),
                    Margin = new Thickness(0, 0, 0, 5),
                    Cursor = Cursors.Hand,
                    Child = optGrid
                };
                optCard.MouseEnter += (_, _) => AnimateRowBackground(optCard, hoverBg, 110);
                optCard.MouseLeave += (_, _) => AnimateRowBackground(optCard, baseBg, 150);
                optCard.MouseLeftButtonUp += (_, e) =>
                {
                    e.Handled = true;
                    _app.Bridge.ResolveApproval(req, allow: !isDenyOpt, reason: displayLabel);
                    AfterApprovalResolved();
                };
                optsList.Children.Add(optCard);
            }
            stack.Children.Add(optsList);
        }
        else if (req.Options.Count > 0 && !req.IsBlocking)
        {
            // Observational approval: display available host-app actions as non-interactive preview chips
            // so users see what awaits in the client without mistaking them for remote-approve buttons.
            var previewWrap = new WrapPanel { Margin = new Thickness(0, 6, 0, 2) };
            foreach (var opt in req.Options)
            {
                var (displayLabel, _) = StripRecommendedPrefix(opt.Label);
                previewWrap.Children.Add(Chip(displayLabel, "#C9D3E2", "#14FFFFFF", new Thickness(0, 0, 6, 4)));
            }
            stack.Children.Add(previewWrap);
        }

        if (req.IsBlocking)
        {
            var buttons = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                HorizontalAlignment = HorizontalAlignment.Right,
                Margin = new Thickness(0, 4, 0, 0)
            };
            var allowBtn = new Button
            {
                Content = Strings.Get("Approve"),
                Style = (Style)FindResource("ApproveButton"),
                Margin = new Thickness(0, 0, 6, 0),
                Padding = new Thickness(13, 5, 13, 5)
            };
            allowBtn.Click += (_, _) =>
            {
                _app.Bridge.ResolveApproval(req, allow: true);
                AfterApprovalResolved();
            };
            var denyBtn = new Button
            {
                Content = Strings.Get("Deny"),
                Style = (Style)FindResource("DenyButton"),
                Margin = new Thickness(0, 0, 6, 0),
                Padding = new Thickness(13, 5, 13, 5)
            };
            denyBtn.Click += (_, _) =>
            {
                _app.Bridge.ResolveApproval(req, allow: false, interrupt: false);
                AfterApprovalResolved();
            };
            var jumpBtn = new Button
            {
                Content = Strings.Get("JumpBack"),
                Style = (Style)FindResource("PillButton"),
                Padding = new Thickness(11, 5, 11, 5)
            };
            jumpBtn.Click += (_, _) => JumpAndCollapse(session);

            buttons.Children.Add(allowBtn);
            buttons.Children.Add(denyBtn);
            buttons.Children.Add(jumpBtn);
            stack.Children.Add(buttons);
        }
        else
        {
            var footer = new Grid { Margin = new Thickness(0, 6, 0, 0) };
            footer.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            footer.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            var hintText = new TextBlock
            {
                Text = Strings.Get("ClickOptionToApproveHint", meta.DisplayName),
                Foreground = B("#C6A6A6"),
                FontSize = 10.5,
                TextWrapping = TextWrapping.Wrap,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(0, 0, 8, 0)
            };
            Grid.SetColumn(hintText, 0);
            footer.Children.Add(hintText);

            var actionButtons = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
            var openBtn = new Button
            {
                Content = Strings.Get("OpenToApprove", meta.DisplayName),
                Style = (Style)FindResource("ApproveButton"),
                Padding = new Thickness(11, 5, 11, 5),
                Margin = new Thickness(0, 0, 6, 0)
            };
            openBtn.Click += (_, _) => JumpAndCollapse(session);

            var dismissBtn = new Button
            {
                Content = Strings.Get("IgnoreReminder"),
                Style = (Style)FindResource("PillButton"),
                Padding = new Thickness(10, 5, 10, 5)
            };
            dismissBtn.Click += (_, _) =>
            {
                _app.Reducer.ClearPendingApproval(session.SessionId, req.RequestId, allowed: false);
                AfterApprovalResolved();
            };

            actionButtons.Children.Add(openBtn);
            actionButtons.Children.Add(dismissBtn);
            Grid.SetColumn(actionButtons, 1);
            footer.Children.Add(actionButtons);
            stack.Children.Add(footer);
        }

        card.Child = stack;
        if (animateIn) AnimateCardIn(card);
        return card;
    }

    private static bool IsDenyLikeLabel(string label)
    {
        var lower = (label ?? "").Trim().ToLowerInvariant();
        return lower.Contains("deny") || lower.Contains("reject") || lower.Contains("decline")
               || lower.Contains("cancel") || lower.Contains("拒绝") || lower.Contains("取消");
    }

    private void AfterApprovalResolved()
    {
        if (_pinState.ReleaseAutoPinIfNoAttention(HasPendingAttention()))
        {
            UpdateHeaderPinAndChevronState();
            bool hasActiveCompletion = _app.Reducer.GetRecentCompletions().Any(c => !c.IsInterrupt);
            if (_isExpanded && !IsMouseOver && !hasActiveCompletion)
            {
                _collapseTimer.Stop();
                SetExpanded(false, animate: true);
            }
        }
        RenderState();
    }

    private UIElement BuildQuestionCard(AgentSession session, PendingQuestionRequest req, bool animateIn = true)
    {
        var meta = AgentRegistry.Get(req.Source);
        var questions = req.Questions.Count > 0
            ? req.Questions
            : new List<PendingQuestionItem>
            {
                new() { Header = "", Question = Strings.Get("QuestionHint") }
            };
        var firstQ = questions[0];

        var card = new Border
        {
            Background = B("#1538BDF8"),
            BorderBrush = B("#5538BDF8"),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(12),
            Padding = new Thickness(12, 11, 12, 11),
            Margin = new Thickness(0, 0, 0, 8)
        };
        var stack = new StackPanel();

        // Header row
        var headerText = !string.IsNullOrWhiteSpace(firstQ.Header)
            ? $"{meta.DisplayName} · {firstQ.Header}"
            : $"{meta.DisplayName} · {Strings.Get("QuestionHint")}";

        var header = new WrapPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        header.Children.Add(IconPath("IconQuestion", 13, "#7FC9FF", margin: new Thickness(0, 0, 7, 3)));
        header.Children.Add(new TextBlock
        {
            Text = headerText,
            Foreground = B("#CBE6FF"),
            FontWeight = FontWeights.SemiBold,
            FontSize = 12,
            Margin = new Thickness(0, 0, 8, 3),
            VerticalAlignment = VerticalAlignment.Center
        });
        if (questions.Any(q => q.Options.Count > 0))
        {
            var modeKey = firstQ.MultiSelect ? "MultiSelectBadge" : "SingleSelectBadge";
            header.Children.Add(Chip(Strings.Get(modeKey), "#7FC9FF", "#2238BDF8", new Thickness(0, 0, 6, 3)));
        }
        if (!req.IsBlocking)
        {
            header.Children.Add(Chip(Strings.Get("SelectInAppBadge"), "#FFD58A", "#22FFD58A", new Thickness(0, 0, 6, 3)));
        }
        stack.Children.Add(header);

        // Scrollable questions & full wrapped option cards (handles long /grill-me prompts & multi-select cleanly)
        bool requiresExplicitSubmit = req.IsBlocking && (questions.Count > 1 || questions.Any(q => q.MultiSelect));
        var selectedByQuestion = new Dictionary<int, List<string>>();
        var optionCardsByQuestion = new Dictionary<int, List<(Border Card, TextBlock IndexText, string Label, bool IsRecommended)>>();

        var questionsBody = new StackPanel();
        for (int qIdx = 0; qIdx < questions.Count; qIdx++)
        {
            var qItem = questions[qIdx];
            var qTitle = questions.Count > 1 ? $"{qIdx + 1}. {qItem.Question}" : qItem.Question;
            if (!string.IsNullOrWhiteSpace(qTitle))
            {
                questionsBody.Children.Add(new TextBlock
                {
                    Text = qTitle,
                    Foreground = B("#F2F4F8"),
                    FontSize = 12.5,
                    FontWeight = FontWeights.Medium,
                    TextWrapping = TextWrapping.Wrap,
                    LineHeight = 17.5,
                    Margin = new Thickness(0, qIdx == 0 ? 7 : 10, 0, 0)
                });
            }

            if (qItem.Options.Count > 0)
            {
                var qList = new List<(Border, TextBlock, string, bool)>();
                optionCardsByQuestion[qIdx] = qList;

                var optionsList = new StackPanel { Margin = new Thickness(0, 7, 0, 2) };
                for (int oIdx = 0; oIdx < qItem.Options.Count; oIdx++)
                {
                    var opt = qItem.Options[oIdx];
                    var (displayLabel, isRecommended) = StripRecommendedPrefix(opt.Label);

                    var optGrid = new Grid();
                    optGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
                    optGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

                    var idxText = new TextBlock
                    {
                        Text = (oIdx + 1).ToString(),
                        FontSize = 10,
                        FontWeight = FontWeights.SemiBold,
                        Foreground = B(isRecommended ? "#9CC7FF" : "#A6B3C6"),
                        HorizontalAlignment = HorizontalAlignment.Center,
                        VerticalAlignment = VerticalAlignment.Center
                    };
                    var indexBadge = new Border
                    {
                        Width = 18,
                        Height = 18,
                        CornerRadius = new CornerRadius(5),
                        Background = B(isRecommended ? "#2E6EA7FF" : "#1AFFFFFF"),
                        BorderBrush = B(isRecommended ? "#666EA7FF" : "#22FFFFFF"),
                        BorderThickness = new Thickness(1),
                        VerticalAlignment = VerticalAlignment.Top,
                        Margin = new Thickness(0, 1, 8, 0),
                        Child = idxText
                    };
                    Grid.SetColumn(indexBadge, 0);
                    optGrid.Children.Add(indexBadge);

                    var optTextStack = new StackPanel();
                    if (isRecommended)
                    {
                        var labelWrap = new StackPanel { Orientation = Orientation.Vertical };
                        var recBadge = Chip(Strings.Get("RecommendedBadge"), "#8ED7A5", "#246FB982", new Thickness(0, 0, 0, 3));
                        recBadge.HorizontalAlignment = HorizontalAlignment.Left;
                        labelWrap.Children.Add(recBadge);
                        labelWrap.Children.Add(new TextBlock
                        {
                            Text = displayLabel,
                            Foreground = B("#F2F6FC"),
                            FontSize = 11.5,
                            TextWrapping = TextWrapping.Wrap,
                            LineHeight = 16
                        });
                        optTextStack.Children.Add(labelWrap);
                    }
                    else
                    {
                        optTextStack.Children.Add(new TextBlock
                        {
                            Text = displayLabel,
                            Foreground = B("#DCE3ED"),
                            FontSize = 11.5,
                            TextWrapping = TextWrapping.Wrap,
                            LineHeight = 16
                        });
                    }

                    if (!string.IsNullOrWhiteSpace(opt.Description))
                    {
                        optTextStack.Children.Add(new TextBlock
                        {
                            Text = opt.Description,
                            Foreground = B("#8A95A6"),
                            FontSize = 10.5,
                            TextWrapping = TextWrapping.Wrap,
                            Margin = new Thickness(0, 2, 0, 0)
                        });
                    }
                    Grid.SetColumn(optTextStack, 1);
                    optGrid.Children.Add(optTextStack);

                    var optCard = new Border
                    {
                        Background = B(isRecommended ? "#1C6EA7FF" : "#12FFFFFF"),
                        BorderBrush = B(isRecommended ? "#556EA7FF" : "#22FFFFFF"),
                        BorderThickness = new Thickness(1),
                        CornerRadius = new CornerRadius(8),
                        Padding = new Thickness(9, 7, 10, 7),
                        Margin = new Thickness(0, 0, 0, 5),
                        Cursor = Cursors.Hand,
                        Child = optGrid
                    };
                    qList.Add((optCard, idxText, opt.Label, isRecommended));

                    var capturedQIdx = qIdx;
                    var capturedOIdx = oIdx;
                    var capturedQ = qItem;
                    var capturedOpt = opt;
                    var baseBg = isRecommended ? "#1C6EA7FF" : "#12FFFFFF";
                    var hoverBg = isRecommended ? "#2B6EA7FF" : "#20FFFFFF";

                    optCard.MouseEnter += (_, _) =>
                    {
                        bool isSel = selectedByQuestion.TryGetValue(capturedQIdx, out var sel) && sel.Contains(capturedOpt.Label);
                        if (!isSel) AnimateRowBackground(optCard, hoverBg, 110);
                    };
                    optCard.MouseLeave += (_, _) =>
                    {
                        bool isSel = selectedByQuestion.TryGetValue(capturedQIdx, out var sel) && sel.Contains(capturedOpt.Label);
                        if (!isSel) AnimateRowBackground(optCard, baseBg, 150);
                    };

                    optCard.MouseLeftButtonUp += (_, e) =>
                    {
                        e.Handled = true;
                        if (!req.IsBlocking)
                        {
                            // Observational question (e.g. Google Antigravity ask_question / /grill-me, Codex rollout, DSH):
                            // focus the host app AND collapse the island out of the way so the user can click directly there.
                            JumpAndCollapse(session);
                            return;
                        }

                        if (!requiresExplicitSubmit)
                        {
                            _app.Bridge.ResolveQuestion(req, new Dictionary<string, string> { [capturedQ.Question] = capturedOpt.Label });
                            AfterApprovalResolved();
                            return;
                        }

                        if (!selectedByQuestion.TryGetValue(capturedQIdx, out var selected))
                        {
                            selected = new List<string>();
                            selectedByQuestion[capturedQIdx] = selected;
                        }

                        if (capturedQ.MultiSelect)
                        {
                            if (selected.Contains(capturedOpt.Label)) selected.Remove(capturedOpt.Label);
                            else selected.Add(capturedOpt.Label);
                        }
                        else
                        {
                            selected.Clear();
                            selected.Add(capturedOpt.Label);
                        }

                        // Refresh visual selection states for this question
                        if (optionCardsByQuestion.TryGetValue(capturedQIdx, out var cards))
                        {
                            for (int ci = 0; ci < cards.Count; ci++)
                            {
                                var (cBorder, cIdx, cLabel, cRec) = cards[ci];
                                bool active = selected.Contains(cLabel);
                                cBorder.Background = B(active ? "#336FB982" : (cRec ? "#1C6EA7FF" : "#12FFFFFF"));
                                cBorder.BorderBrush = B(active ? "#886FB982" : (cRec ? "#556EA7FF" : "#22FFFFFF"));
                                cIdx.Text = active ? "✓" : (ci + 1).ToString();
                            }
                        }
                    };
                    optionsList.Children.Add(optCard);
                }
                questionsBody.Children.Add(optionsList);
            }
        }

        var questionsScroll = new ScrollViewer
        {
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            MaxHeight = 290,
            Focusable = false,
            Content = questionsBody
        };
        stack.Children.Add(questionsScroll);

        if (req.IsBlocking)
        {
            var inputRow = new DockPanel { Margin = new Thickness(0, 5, 0, 0) };
            var sendBtn = new Button
            {
                Content = Strings.Get(requiresExplicitSubmit ? "SubmitSelected" : "SubmitAnswer"),
                Style = (Style)FindResource("ApproveButton"),
                Padding = new Thickness(13, 5, 13, 5),
                Margin = new Thickness(7, 0, 0, 0)
            };
            DockPanel.SetDock(sendBtn, Dock.Right);
            var answerBox = new TextBox
            {
                Style = (Style)FindResource("DarkAnswerBox"),
                MinHeight = 30
            };
            sendBtn.Click += (_, _) =>
            {
                var answers = new Dictionary<string, string>();
                for (int qi = 0; qi < questions.Count; qi++)
                {
                    if (selectedByQuestion.TryGetValue(qi, out var chosen) && chosen.Count > 0)
                    {
                        answers[questions[qi].Question] = string.Join(", ", chosen);
                    }
                }
                var typed = answerBox.Text.Trim();
                if (!string.IsNullOrEmpty(typed))
                {
                    answers[firstQ.Question ?? "answer"] = typed;
                }
                if (answers.Count == 0) return;
                _app.Bridge.ResolveQuestion(req, answers);
                AfterApprovalResolved();
            };
            answerBox.KeyDown += (_, e) =>
            {
                if (e.Key == Key.Enter) sendBtn.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            };
            inputRow.Children.Add(sendBtn);
            inputRow.Children.Add(answerBox);
            stack.Children.Add(inputRow);
        }
        else
        {
            var footer = new Grid { Margin = new Thickness(0, 6, 0, 0) };
            footer.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            footer.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            var hintText = new TextBlock
            {
                Text = Strings.Get("ClickOptionToJumpHint", meta.DisplayName),
                Foreground = B("#89A5C4"),
                FontSize = 10.5,
                TextWrapping = TextWrapping.Wrap,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(0, 0, 8, 0)
            };
            Grid.SetColumn(hintText, 0);
            footer.Children.Add(hintText);

            var actionButtons = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
            var openBtn = new Button
            {
                Content = Strings.Get("OpenToAnswer", meta.DisplayName),
                Style = (Style)FindResource("ApproveButton"),
                Padding = new Thickness(11, 5, 11, 5),
                Margin = new Thickness(0, 0, 6, 0)
            };
            openBtn.Click += (_, _) => JumpAndCollapse(session);

            var dismissBtn = new Button
            {
                Content = Strings.Get("IgnoreReminder"),
                Style = (Style)FindResource("PillButton"),
                Padding = new Thickness(10, 5, 10, 5)
            };
            dismissBtn.Click += (_, _) =>
            {
                _app.Reducer.ClearPendingQuestion(session.SessionId, req.RequestId);
                AfterApprovalResolved();
            };

            actionButtons.Children.Add(openBtn);
            actionButtons.Children.Add(dismissBtn);
            Grid.SetColumn(actionButtons, 1);
            footer.Children.Add(actionButtons);
            stack.Children.Add(footer);
        }

        card.Child = stack;
        if (animateIn) AnimateCardIn(card);
        return card;
    }

    private static (string Label, bool IsRecommended) StripRecommendedPrefix(string raw)
    {
        var trimmed = (raw ?? "").Trim();
        string[] prefixes = ["(Recommended)", "（Recommended）", "（推荐）", "(推荐)", "[Recommended]"];
        foreach (var prefix in prefixes)
        {
            if (trimmed.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            {
                return (trimmed[prefix.Length..].Trim(), true);
            }
        }
        return (trimmed, false);
    }

    private UIElement BuildCompletionCard(CompletionCard card)
    {
        var meta = AgentRegistry.Get(card.Source);
        var accent = card.IsError ? "#F26D6D" : card.IsInterrupt ? "#FFD58A" : "#6FB982";
        var border = new Border
        {
            Background = B("#0E6FB982"),
            BorderBrush = B("#4D6FB982"),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(12),
            Padding = new Thickness(12, 10, 10, 10),
            Margin = new Thickness(0, 0, 0, 8)
        };
        if (card.IsError)
        {
            border.Background = B("#14F26D6D");
            border.BorderBrush = B("#59F26D6D");
        }

        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var icon = IconPath(card.IsError ? "IconAlert" : "IconCheck", 13, accent, margin: new Thickness(0, 2, 8, 0));
        Grid.SetColumn(icon, 0);
        grid.Children.Add(icon);

        var stack = new StackPanel();
        stack.Children.Add(new TextBlock
        {
            Text = $"{meta.DisplayName} · {Strings.Get(card.IsError ? "Error" : "Completed")}",
            Foreground = B("#E9EDF3"),
            FontWeight = FontWeights.SemiBold,
            FontSize = 12
        });
        var detail = !string.IsNullOrWhiteSpace(card.AssistantMessage) ? card.AssistantMessage : card.Title;
        if (!string.IsNullOrWhiteSpace(detail))
        {
            stack.Children.Add(new TextBlock
            {
                Text = detail,
                Foreground = B("#A9B4C4"),
                FontSize = 11.5,
                TextWrapping = TextWrapping.Wrap,
                MaxHeight = 46,
                TextTrimming = TextTrimming.CharacterEllipsis,
                Margin = new Thickness(0, 3, 0, 0)
            });
        }
        Grid.SetColumn(stack, 1);
        grid.Children.Add(stack);

        var closeBtn = new Button
        {
            Style = (Style)FindResource("IconButton"),
            Width = 24,
            Height = 24,
            VerticalAlignment = VerticalAlignment.Top,
            Margin = new Thickness(6, -2, 0, 0),
            Content = new Path
            {
                Data = (Geometry)FindResource("IconClose"),
                Fill = B("#8A95A6"),
                Width = 10,
                Height = 10,
                Stretch = Stretch.Uniform
            }
        };
        closeBtn.Click += (_, _) => _app.Reducer.DismissCompletionCard(card.Id);
        Grid.SetColumn(closeBtn, 2);
        grid.Children.Add(closeBtn);

        border.Child = grid;
        AnimateCardIn(border);
        return border;
    }

    private UIElement BuildSessionRow(AgentSession session)
    {
        var meta = AgentRegistry.Get(session.Source);
        var phaseColor = session.Phase switch
        {
            SessionPhase.Running => "#6EA7FF",
            SessionPhase.WaitingForApproval => "#F4A4A4",
            SessionPhase.WaitingForAnswer => "#FFD58A",
            SessionPhase.Completed => "#6FB982",
            SessionPhase.Error => "#F26D6D",
            _ => "#6F7B8F"
        };

        var row = new Border
        {
            Background = Brushes.Transparent,
            CornerRadius = new CornerRadius(10),
            Padding = new Thickness(9, 7, 9, 7),
            Margin = new Thickness(-3, 0, -3, 2),
            Cursor = Cursors.Hand
        };
        row.MouseEnter += (_, _) => AnimateRowBackground(row, "#12FFFFFF", 130);
        row.MouseLeave += (_, _) => AnimateRowBackground(row, "#00000000", 180);
        row.MouseLeftButtonUp += (_, _) => TerminalJump(session);
        row.ToolTip = Strings.Get("OpenConversation");

        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        // Status indicator
        var dot = new Ellipse
        {
            Width = 8,
            Height = 8,
            Fill = B(phaseColor),
            HorizontalAlignment = HorizontalAlignment.Left,
            VerticalAlignment = VerticalAlignment.Top,
            Margin = new Thickness(1, 5, 0, 0)
        };
        if (session.Phase is SessionPhase.WaitingForApproval or SessionPhase.WaitingForAnswer)
        {
            var pulse = new DoubleAnimation(1.0, 0.35, TimeSpan.FromMilliseconds(700))
            {
                AutoReverse = true,
                RepeatBehavior = RepeatBehavior.Forever,
                EasingFunction = new SineEase { EasingMode = EasingMode.EaseInOut }
            };
            dot.BeginAnimation(OpacityProperty, pulse);
        }
        var dotHost = new Grid { Width = 20, VerticalAlignment = VerticalAlignment.Top };
        dotHost.Children.Add(dot);
        Grid.SetColumn(dotHost, 0);
        grid.Children.Add(dotHost);

        // Text content
        var content = new StackPanel { Margin = new Thickness(2, 0, 0, 0) };

        var titleRow = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        titleRow.Children.Add(new TextBlock
        {
            Text = session.DisplayTitle,
            Foreground = B("#F2F4F8"),
            FontWeight = FontWeights.SemiBold,
            FontSize = 12.5,
            MaxWidth = 230,
            TextTrimming = TextTrimming.CharacterEllipsis,
            VerticalAlignment = VerticalAlignment.Center
        });
        titleRow.Children.Add(Chip(meta.DisplayName, meta.AccentHex, "#14FFFFFF", new Thickness(7, 0, 0, 0)));
        if (session.ActiveSubagentCount > 0)
        {
            titleRow.Children.Add(Chip($"+{session.ActiveSubagentCount} agent", "#A6B0C0", "#0FFFFFFF", new Thickness(5, 0, 0, 0)));
        }
        content.Children.Add(titleRow);

        var activity = session.Phase == SessionPhase.WaitingForAnswer && !string.IsNullOrWhiteSpace(session.StatusSummary)
            ? session.StatusSummary
            : !string.IsNullOrWhiteSpace(session.ActiveToolName)
                ? (string.IsNullOrWhiteSpace(session.ActiveToolPreview)
                    ? session.ActiveToolName
                    : $"{session.ActiveToolName} · {session.ActiveToolPreview}")
                : session.StatusSummary ?? session.LastPrompt ?? session.Cwd ?? "";
        if (!string.IsNullOrWhiteSpace(activity))
        {
            content.Children.Add(new TextBlock
            {
                Text = activity,
                Foreground = B("#8A95A6"),
                FontSize = 11.5,
                TextTrimming = TextTrimming.CharacterEllipsis,
                Margin = new Thickness(0, 2, 0, 0)
            });
        }

        var metaLine = new List<string>();
        if (!string.IsNullOrWhiteSpace(session.Terminal.TerminalApp)) metaLine.Add(session.Terminal.TerminalApp!);
        if (!string.IsNullOrWhiteSpace(session.Terminal.TerminalTitle)) metaLine.Add(session.Terminal.TerminalTitle!);
        if (!string.IsNullOrWhiteSpace(session.Model)) metaLine.Add(session.Model!);
        if (metaLine.Count > 0)
        {
            content.Children.Add(new TextBlock
            {
                Text = string.Join("  ·  ", metaLine),
                Foreground = B("#5F6B7D"),
                FontSize = 10.5,
                TextTrimming = TextTrimming.CharacterEllipsis,
                Margin = new Thickness(0, 1, 0, 0)
            });
        }

        Grid.SetColumn(content, 1);
        grid.Children.Add(content);

        // Right side: elapsed + jump affordance
        var right = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(8, 0, 0, 0)
        };
        right.Children.Add(new TextBlock
        {
            Text = FormatElapsed(session.UpdatedAt),
            FontFamily = (FontFamily)FindResource("FontMono"),
            FontSize = 10,
            Foreground = B("#5F6B7D"),
            VerticalAlignment = VerticalAlignment.Center
        });
        right.Children.Add(new Path
        {
            Data = (Geometry)FindResource("ArrowUpRight"),
            Stroke = B("#6E7A8C"),
            StrokeThickness = 1.6,
            StrokeStartLineCap = PenLineCap.Round,
            StrokeEndLineCap = PenLineCap.Round,
            Width = 11,
            Height = 11,
            Stretch = Stretch.Uniform,
            Margin = new Thickness(7, 0, 2, 0),
            VerticalAlignment = VerticalAlignment.Center,
            ToolTip = Strings.Get("OpenConversation")
        });
        Grid.SetColumn(right, 2);
        grid.Children.Add(right);

        row.Child = grid;
        return row;
    }

    private void TerminalJump(AgentSession session)
    {
        try
        {
            TerminalJumper.JumpToSession(session);
        }
        catch
        {
            // Jumping back is best-effort; never surface an exception in the island.
        }
    }

    // ---------------------------------------------------------------------
    // Small visual helpers
    // ---------------------------------------------------------------------

    private void AnimateCardIn(UIElement element)
    {
        element.Opacity = 0;
        var translate = new TranslateTransform(0, -7);
        element.RenderTransform = translate;
        element.BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(210))
        {
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut },
            FillBehavior = FillBehavior.HoldEnd
        });
        translate.BeginAnimation(TranslateTransform.YProperty, new DoubleAnimation(-7, 0, TimeSpan.FromMilliseconds(240))
        {
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut },
            FillBehavior = FillBehavior.HoldEnd
        });
    }

    private static void AnimateRowBackground(Border border, string targetHex, int milliseconds)
    {
        var target = (Color)ColorConverter.ConvertFromString(targetHex);
        var current = border.Background is SolidColorBrush sb ? sb.Color : Colors.Transparent;
        var brush = new SolidColorBrush(current);
        border.Background = brush;
        var animation = new ColorAnimation(current, target, TimeSpan.FromMilliseconds(milliseconds))
        {
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut },
            FillBehavior = FillBehavior.HoldEnd
        };
        brush.BeginAnimation(SolidColorBrush.ColorProperty, animation);
    }

    private Path IconPath(string geometryKey, double size, string fillHex, Thickness margin = default)
    {
        return new Path
        {
            Data = (Geometry)FindResource(geometryKey),
            Fill = B(fillHex),
            Width = size,
            Height = size,
            Stretch = Stretch.Uniform,
            Margin = margin,
            VerticalAlignment = VerticalAlignment.Center
        };
    }

    private static Border Chip(string text, string foregroundHex, string backgroundHex, Thickness margin)
    {
        return new Border
        {
            Background = B(backgroundHex),
            CornerRadius = new CornerRadius(6),
            Padding = new Thickness(6, 1, 6, 2),
            Margin = margin,
            VerticalAlignment = VerticalAlignment.Center,
            Child = new TextBlock
            {
                Text = text,
                Foreground = B(foregroundHex),
                FontSize = 10,
                FontWeight = FontWeights.SemiBold
            }
        };
    }

    private static SolidColorBrush B(string hex)
        => new((Color)ColorConverter.ConvertFromString(hex));

    private static string FormatElapsed(DateTimeOffset timestamp)
    {
        var delta = DateTimeOffset.Now - timestamp;
        if (delta.TotalSeconds < 60) return Strings.Get("JustNow");
        if (delta.TotalMinutes < 60) return $"{(int)delta.TotalMinutes}m";
        if (delta.TotalHours < 24) return $"{(int)delta.TotalHours}h";
        return $"{(int)delta.TotalDays}d";
    }

    private void RefreshBtn_Click(object sender, RoutedEventArgs e) => _app.RunDiscoveryPass();

    private void SettingsBtn_Click(object sender, RoutedEventArgs e) => _app.OpenSettings();
}


