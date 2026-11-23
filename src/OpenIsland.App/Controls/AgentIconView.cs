using System.IO.Packaging;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Shapes;
using OpenIsland.Core.Models;

namespace OpenIsland.App.Controls;

/// <summary>
/// Renders a bespoke, theme-harmonized vector badge for any <see cref="AgentSource"/>.
/// Loads <c>Themes/AgentIcons.xaml</c> lazily on first use so it works cleanly in any
/// window or standalone view without requiring changes to <c>App.xaml</c>.
/// </summary>
public sealed class AgentIconView : Border
{
    private const string IconsPackUri = "/OpenIsland;component/Themes/AgentIcons.xaml";

    private static ResourceDictionary? _iconDictionary;

    public static readonly DependencyProperty SourceProperty = DependencyProperty.Register(
        nameof(Source),
        typeof(AgentSource),
        typeof(AgentIconView),
        new PropertyMetadata(AgentSource.Unknown, OnVisualPropertyChanged));

    public static readonly DependencyProperty IconSizeProperty = DependencyProperty.Register(
        nameof(IconSize),
        typeof(double),
        typeof(AgentIconView),
        new PropertyMetadata(34.0, OnVisualPropertyChanged));

    public static readonly DependencyProperty IsDarkThemeProperty = DependencyProperty.Register(
        nameof(IsDarkTheme),
        typeof(bool),
        typeof(AgentIconView),
        new PropertyMetadata(false, OnVisualPropertyChanged));

    public static readonly DependencyProperty IsMutedProperty = DependencyProperty.Register(
        nameof(IsMuted),
        typeof(bool),
        typeof(AgentIconView),
        new PropertyMetadata(false, OnVisualPropertyChanged));

    public AgentSource Source
    {
        get => (AgentSource)GetValue(SourceProperty);
        set => SetValue(SourceProperty, value);
    }

    public double IconSize
    {
        get => (double)GetValue(IconSizeProperty);
        set => SetValue(IconSizeProperty, value);
    }

    public bool IsDarkTheme
    {
        get => (bool)GetValue(IsDarkThemeProperty);
        set => SetValue(IsDarkThemeProperty, value);
    }

    public bool IsMuted
    {
        get => (bool)GetValue(IsMutedProperty);
        set => SetValue(IsMutedProperty, value);
    }

    public AgentIconView()
    {
        BorderThickness = new Thickness(1);
        SnapsToDevicePixels = true;
        UseLayoutRounding = true;
        HorizontalAlignment = HorizontalAlignment.Center;
        VerticalAlignment = VerticalAlignment.Center;
        UpdateVisual();
    }

    /// <summary>
    /// Creates a configured squircle badge for the specified agent.
    /// </summary>
    public static AgentIconView Create(
        AgentSource source,
        double size = 34,
        bool isDark = false,
        bool isMuted = false)
    {
        var view = new AgentIconView
        {
            Source = source,
            IconSize = size,
            IsDarkTheme = isDark,
            IsMuted = isMuted
        };
        return view;
    }

    /// <summary>
    /// Returns the lazily loaded <c>AgentIcons.xaml</c> resource dictionary.
    /// </summary>
    public static ResourceDictionary GetIconDictionary()
    {
        if (_iconDictionary is not null)
            return _iconDictionary;

        // Ensure the WPF pack:// URI scheme is registered even in standalone STA hosts.
        _ = PackUriHelper.UriSchemePack;

        var loaded = Application.LoadComponent(new Uri(IconsPackUri, UriKind.Relative)) as ResourceDictionary
            ?? throw new InvalidOperationException($"Failed to load resource dictionary '{IconsPackUri}'.");

        _iconDictionary = loaded;
        return _iconDictionary;
    }

    /// <summary>
    /// Maps each <see cref="AgentSource"/> to its resource key in <c>AgentIcons.xaml</c>.
    /// </summary>
    public static string GetResourceKey(AgentSource source) => source switch
    {
        AgentSource.Claude => "AgentIconClaude",
        AgentSource.Codex => "AgentIconCodex",
        AgentSource.OpenCode => "AgentIconOpenCode",
        AgentSource.Cursor => "AgentIconCursor",
        AgentSource.Gemini => "AgentIconGemini",
        AgentSource.Antigravity => "AgentIconAntigravity",
        AgentSource.Kimi => "AgentIconKimi",
        AgentSource.Grok => "AgentIconGrok",
        AgentSource.Hermes => "AgentIconHermes",
        AgentSource.Pi => "AgentIconPi",
        AgentSource.OhMyPi => "AgentIconOhMyPi",
        AgentSource.Qoder => "AgentIconQoder",
        AgentSource.Qwen => "AgentIconQwen",
        AgentSource.Factory => "AgentIconFactory",
        AgentSource.CodeBuddy => "AgentIconCodeBuddy",
        AgentSource.ZCode => "AgentIconZCode",
        AgentSource.DeepSeekHarness => "AgentIconDeepSeekHarness",
        _ => "AgentIconUnknown"
    };

    /// <summary>
    /// Returns the 24x24 vector <see cref="Geometry"/> for the specified agent.
    /// </summary>
    public static Geometry GetIconGeometry(AgentSource source)
    {
        var dict = GetIconDictionary();
        var key = GetResourceKey(source);
        if (dict[key] is Geometry geometry)
        {
            if (geometry.CanFreeze && !geometry.IsFrozen)
                geometry.Freeze();
            return geometry;
        }

        if (dict["AgentIconUnknown"] is Geometry fallback)
        {
            if (fallback.CanFreeze && !fallback.IsFrozen)
                fallback.Freeze();
            return fallback;
        }

        return Geometry.Empty;
    }

    /// <summary>
    /// Builds a crisp standalone vector glyph preserving its intentional 24x24 optical frame,
    /// suitable for compact headers, inline chips, or usage cards.
    /// </summary>
    public static FrameworkElement CreateGlyph(AgentSource source, double glyphSize, Brush foreground)
    {
        var size = Math.Max(8.0, glyphSize);
        var path = new Path
        {
            Data = GetIconGeometry(source),
            Fill = foreground,
            SnapsToDevicePixels = false
        };

        var canvas = new Canvas
        {
            Width = 24,
            Height = 24,
            ClipToBounds = false
        };
        canvas.Children.Add(path);

        return new Viewbox
        {
            Width = size,
            Height = size,
            Stretch = Stretch.Uniform,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            Child = canvas
        };
    }

    /// <summary>
    /// Parses the canonical accent color for an agent from <see cref="AgentRegistry"/>.
    /// </summary>
    public static Color GetAccentColor(AgentSource source)
    {
        var hex = AgentRegistry.Get(source).AccentHex;
        try
        {
            if (ColorConverter.ConvertFromString(hex) is Color c)
                return c;
        }
        catch
        {
            // Fall through to neutral slate.
        }
        return Color.FromRgb(0x94, 0xA3, 0xB8);
    }

    /// <summary>
    /// Computes a contrast-harmonized foreground color for the agent glyph so it stays
    /// crisp and unmistakable on both warm-paper Light surfaces and dark-slate Dark surfaces.
    /// </summary>
    public static Color GetHarmonizedForegroundColor(AgentSource source, bool isDark)
    {
        var accent = GetAccentColor(source);
        return isDark ? HarmonizeForDark(accent) : HarmonizeForLight(accent);
    }

    private static void OnVisualPropertyChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is AgentIconView view)
            view.UpdateVisual();
    }

    private void UpdateVisual()
    {
        var size = double.IsNaN(IconSize) || IconSize <= 0 ? 34.0 : IconSize;
        var isDark = IsDarkTheme;
        var isMuted = IsMuted;
        var accent = GetAccentColor(Source);
        var fgColor = isDark ? HarmonizeForDark(accent) : HarmonizeForLight(accent);

        Width = size;
        Height = size;
        CornerRadius = new CornerRadius(Math.Round(size * 0.27, 1));

        byte topAlpha;
        byte bottomAlpha;
        byte borderAlpha;
        byte fgAlpha;

        if (isDark)
        {
            topAlpha = isMuted ? (byte)24 : (byte)42;
            bottomAlpha = isMuted ? (byte)13 : (byte)22;
            borderAlpha = isMuted ? (byte)46 : (byte)80;
            fgAlpha = isMuted ? (byte)190 : (byte)255;
        }
        else
        {
            topAlpha = isMuted ? (byte)18 : (byte)32;
            bottomAlpha = isMuted ? (byte)10 : (byte)16;
            borderAlpha = isMuted ? (byte)38 : (byte)64;
            fgAlpha = isMuted ? (byte)185 : (byte)255;
        }

        var bg = new LinearGradientBrush(
            Color.FromArgb(topAlpha, accent.R, accent.G, accent.B),
            Color.FromArgb(bottomAlpha, accent.R, accent.G, accent.B),
            new Point(0, 0),
            new Point(1, 1));
        bg.Freeze();
        Background = bg;

        var border = new SolidColorBrush(Color.FromArgb(borderAlpha, fgColor.R, fgColor.G, fgColor.B));
        border.Freeze();
        BorderBrush = border;

        var fg = new SolidColorBrush(Color.FromArgb(fgAlpha, fgColor.R, fgColor.G, fgColor.B));
        fg.Freeze();

        var glyphSize = Math.Round(size * 0.57, 1);
        Child = CreateGlyph(Source, glyphSize, fg);
    }

    private static Color HarmonizeForLight(Color c)
    {
        // Keep light-mode glyphs rich and crisp against warm paper (#F7F6F2 / #FFFFFF).
        // Bright hues (sky cyan, amber, emerald, teal) are gently deepened while keeping their hue.
        var lum = RelativeLuminance(c);
        const double maxLightModeLum = 0.23;
        if (lum <= maxLightModeLum)
            return c;

        // Iteratively darken toward a rich saturated shade (mixing slightly with dark ink #0E1117).
        var factor = Math.Clamp(Math.Sqrt(maxLightModeLum / lum), 0.52, 0.94);
        byte r = (byte)Math.Clamp(Math.Round(c.R * factor), 0, 255);
        byte g = (byte)Math.Clamp(Math.Round(c.G * factor), 0, 255);
        byte b = (byte)Math.Clamp(Math.Round(c.B * factor), 0, 255);
        return Color.FromRgb(r, g, b);
    }

    private static Color HarmonizeForDark(Color c)
    {
        // Lift deeper hues (violet, indigo, royal blue) on dark slate (#12151C / #1A1F29)
        // so every icon has balanced luminous clarity without washing out.
        var lum = RelativeLuminance(c);
        const double minDarkModeLum = 0.37;
        if (lum >= minDarkModeLum)
            return c;

        var lift = Math.Clamp((minDarkModeLum - lum) / (1.0 - lum) * 1.15, 0.14, 0.42);
        byte r = (byte)Math.Clamp(Math.Round(c.R + (255 - c.R) * lift), 0, 255);
        byte g = (byte)Math.Clamp(Math.Round(c.G + (255 - c.G) * lift), 0, 255);
        byte b = (byte)Math.Clamp(Math.Round(c.B + (255 - c.B) * lift), 0, 255);
        return Color.FromRgb(r, g, b);
    }

    private static double RelativeLuminance(Color c)
    {
        static double ToLinear(byte channel)
        {
            var s = channel / 255.0;
            return s <= 0.03928 ? s / 12.92 : Math.Pow((s + 0.055) / 1.055, 2.4);
        }

        return 0.2126 * ToLinear(c.R) + 0.7152 * ToLinear(c.G) + 0.0722 * ToLinear(c.B);
    }
}
