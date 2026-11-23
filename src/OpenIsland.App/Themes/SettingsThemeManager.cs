using System.Windows;
using System.Windows.Media;
using Microsoft.Win32;
using OpenIsland.Core.Diagnostics;
using OpenIsland.Core.Models;

namespace OpenIsland.App.Themes;

/// <summary>
/// Manages the Light / Dark / Follow-System appearance theme for the Settings window
/// and auxiliary dialogs (AgentConnectWindow, UpdateWindow), leaving IslandWindow untouched.
/// Updates semantic brushes in <see cref="Application.Current"/> resources live and raises
/// <see cref="ThemeChanged"/> so dynamically constructed UI rows/cards re-theme immediately.
/// </summary>
public static class SettingsThemeManager
{
    private const string PersonalizeSubKey = @"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize";
    private const string AppsUseLightThemeValue = "AppsUseLightTheme";

    private static bool _initialized;
    private static bool _hasAppliedOnce;
    private static readonly object SyncLock = new();

    public static SettingsAppearanceMode CurrentMode { get; private set; } = SettingsAppearanceMode.System;

    /// <summary>True when the effective active theme for Settings/dialogs is Dark; false for Light.</summary>
    public static bool IsDarkActive { get; private set; }

    /// <summary>
    /// Raised on the UI thread whenever the effective Light/Dark theme changes.
    /// The boolean argument equals <see cref="IsDarkActive"/>.
    /// </summary>
    public static event Action<bool>? ThemeChanged;

    public static void Initialize(SettingsAppearanceMode mode)
    {
        lock (SyncLock)
        {
            if (!_initialized)
            {
                SystemEvents.UserPreferenceChanged += OnUserPreferenceChanged;
                _initialized = true;
            }
        }

        Apply(mode, forceNotify: false);
    }

    public static void Shutdown()
    {
        lock (SyncLock)
        {
            if (!_initialized) return;
            SystemEvents.UserPreferenceChanged -= OnUserPreferenceChanged;
            _initialized = false;
        }
    }

    /// <summary>
    /// Applies the requested appearance mode immediately to <see cref="Application.Current"/>
    /// resources and notifies subscribers when the effective Light/Dark theme changes.
    /// </summary>
    public static void Apply(SettingsAppearanceMode mode) => Apply(mode, forceNotify: false);

    private static void Apply(SettingsAppearanceMode mode, bool forceNotify)
    {
        CurrentMode = mode;
        var resolvedDark = ResolveIsDark(mode);
        var changed = !_hasAppliedOnce || IsDarkActive != resolvedDark;

        IsDarkActive = resolvedDark;
        _hasAppliedOnce = true;

        var appResources = Application.Current?.Resources;
        if (appResources is not null)
        {
            ApplyToResources(appResources, resolvedDark);
        }

        if (changed || forceNotify)
        {
            RaiseThemeChanged(resolvedDark);
        }
    }

    /// <summary>
    /// Resolves whether the given mode maps to Dark (true) or Light (false).
    /// </summary>
    public static bool ResolveIsDark(SettingsAppearanceMode mode) => mode switch
    {
        SettingsAppearanceMode.Dark => true,
        SettingsAppearanceMode.Light => false,
        _ => DetectWindowsIsDarkTheme()
    };

    /// <summary>
    /// Detects Windows app theme via HKCU\...\Personalize\AppsUseLightTheme (0 = Dark, 1 = Light; defaults to Light).
    /// </summary>
    public static bool DetectWindowsIsDarkTheme()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(PersonalizeSubKey, writable: false);
            var raw = key?.GetValue(AppsUseLightThemeValue);
            if (raw is int intVal)
            {
                return intVal == 0;
            }
            if (raw is not null && int.TryParse(raw.ToString(), out var parsed))
            {
                return parsed == 0;
            }
        }
        catch (Exception ex)
        {
            AppLog.Warn($"failed to read Windows AppsUseLightTheme registry key: {ex.Message}");
        }

        // Default to Light if missing or unreadable.
        return false;
    }

    private static void OnUserPreferenceChanged(object? sender, UserPreferenceChangedEventArgs e)
    {
        if (e.Category != UserPreferenceCategory.General) return;
        if (CurrentMode != SettingsAppearanceMode.System) return;

        var dispatcher = Application.Current?.Dispatcher;
        if (dispatcher is null)
        {
            Apply(SettingsAppearanceMode.System, forceNotify: false);
            return;
        }

        if (dispatcher.CheckAccess())
        {
            Apply(SettingsAppearanceMode.System, forceNotify: false);
        }
        else
        {
            dispatcher.BeginInvoke(new Action(() =>
            {
                if (CurrentMode == SettingsAppearanceMode.System)
                {
                    Apply(SettingsAppearanceMode.System, forceNotify: false);
                }
            }));
        }
    }

    private static void RaiseThemeChanged(bool isDark)
    {
        var handlers = ThemeChanged;
        if (handlers is null) return;

        foreach (var d in handlers.GetInvocationList())
        {
            try
            {
                ((Action<bool>)d).Invoke(isDark);
            }
            catch (Exception ex)
            {
                AppLog.Warn($"SettingsThemeManager.ThemeChanged subscriber error: {ex.Message}");
            }
        }
    }

    // ================================================================
    // Semantic Colors & Brushes for code-behind consumers
    // ================================================================

    public static Color AccentColor => IsDarkActive ? Hex("#6EA7FF") : Hex("#2F6FED");
    public static Color GreenColor => IsDarkActive ? Hex("#6FB982") : Hex("#1D8A56");
    public static Color AmberColor => IsDarkActive ? Hex("#E7A762") : Hex("#B86B14");
    public static Color DangerColor => IsDarkActive ? Hex("#F26D6D") : Hex("#CF3B43");

    public static Color TextPrimaryColor => IsDarkActive ? Hex("#F2F4F8") : Hex("#1B1E24");
    public static Color TextSecondaryColor => IsDarkActive ? Hex("#A6B1C2") : Hex("#525B6B");
    public static Color TextTertiaryColor => IsDarkActive ? Hex("#6E7B90") : Hex("#737D8F");
    public static Color MonoTextColor => IsDarkActive ? Hex("#94A2B7") : Hex("#4F5A6D");
    public static Color TrackColor => IsDarkActive ? Hex("#222836") : Hex("#E2DFD6");
    public static Color DividerColor => IsDarkActive ? Hex("#232A38") : Hex("#E6E3DA");

    public static SolidColorBrush TextPrimaryBrush => FrozenBrush(TextPrimaryColor);
    public static SolidColorBrush TextSecondaryBrush => FrozenBrush(TextSecondaryColor);
    public static SolidColorBrush TextTertiaryBrush => FrozenBrush(TextTertiaryColor);
    public static SolidColorBrush MonoTextBrush => FrozenBrush(MonoTextColor);
    public static SolidColorBrush TrackBrush => FrozenBrush(TrackColor);
    public static SolidColorBrush DividerBrush => FrozenBrush(DividerColor);

    public static SolidColorBrush AccentBrush => FrozenBrush(AccentColor);
    public static SolidColorBrush GreenBrush => FrozenBrush(GreenColor);
    public static SolidColorBrush AmberBrush => FrozenBrush(AmberColor);
    public static SolidColorBrush DangerBrush => FrozenBrush(DangerColor);

    public static SolidColorBrush SuccessBannerBgBrush => FrozenBrush(IsDarkActive ? Hex("#13241C") : Hex("#ECF8F1"));
    public static SolidColorBrush SuccessBannerBorderBrush => FrozenBrush(IsDarkActive ? Hex("#2B5642") : Hex("#B5E0C8"));
    public static SolidColorBrush SuccessBannerTextBrush => FrozenBrush(IsDarkActive ? Hex("#A4DBB5") : Hex("#166841"));

    public static SolidColorBrush DangerBannerBgBrush => FrozenBrush(IsDarkActive ? Hex("#241418") : Hex("#FDF0F1"));
    public static SolidColorBrush DangerBannerBorderBrush => FrozenBrush(IsDarkActive ? Hex("#5E2D35") : Hex("#F0C2C5"));
    public static SolidColorBrush DangerBannerTextBrush => FrozenBrush(IsDarkActive ? Hex("#F5B2B6") : Hex("#A42830"));

    public static SolidColorBrush StatusHaloBrush(bool ok) =>
        ok ? SuccessBannerBgBrush : DangerBannerBgBrush;

    /// <summary>
    /// Creates a theme-balanced translucent chip/badge fill for an arbitrary accent color.
    /// </summary>
    public static SolidColorBrush AlphaBrush(Color color, byte darkAlpha, byte lightAlpha) =>
        FrozenBrush(Color.FromArgb(IsDarkActive ? darkAlpha : lightAlpha, color.R, color.G, color.B));

    /// <summary>
    /// Returns a readable foreground version of an accent hue on the active card surface.
    /// In Light mode, very bright pastel hues are slightly deepened so they keep crisp contrast on white cards.
    /// </summary>
    public static Color ReadableAccent(Color accent)
    {
        if (IsDarkActive) return accent;

        // Relative luminance approximation
        double lum = (0.299 * accent.R + 0.587 * accent.G + 0.114 * accent.B) / 255.0;
        if (lum <= 0.62) return accent;

        // Darken bright pastels for white-surface readability while keeping hue intact.
        double factor = 0.62 / lum;
        return Color.FromRgb(
            (byte)Math.Clamp(Math.Round(accent.R * factor), 0, 255),
            (byte)Math.Clamp(Math.Round(accent.G * factor), 0, 255),
            (byte)Math.Clamp(Math.Round(accent.B * factor), 0, 255));
    }

    // ================================================================
    // ResourceDictionary Palette Application
    // ================================================================

    /// <summary>
    /// Populates all semantic Settings theme resources on the target <see cref="ResourceDictionary"/>.
    /// </summary>
    public static void ApplyToResources(ResourceDictionary resources, bool isDark)
    {
        ArgumentNullException.ThrowIfNull(resources);

        if (isDark)
        {
            // Window canvas & chrome
            resources["SettingsWindowBackground"] = FrozenGradient(
                Hex("#151922"), 0.0,
                Hex("#12151C"), 0.55,
                Hex("#101319"), 1.0,
                new Point(0, 0), new Point(1, 1));
            resources["SettingsWindowShadowFill"] = FrozenBrush("#12151C");
            resources["SettingsWindowBorder"] = FrozenBrush("#2A3242");

            // Surfaces & borders
            resources["SettingsSurface"] = FrozenBrush("#1A1F29");
            resources["SettingsSurfaceHover"] = FrozenBrush("#222836");
            resources["SettingsSurfaceRaised"] = FrozenBrush("#1E2430");
            resources["SettingsAgentCardBg"] = FrozenBrush("#181D27");
            resources["SettingsAgentCardHoverBg"] = FrozenBrush("#222836");
            resources["SettingsInset"] = FrozenBrush("#131720");
            resources["SettingsInsetBorder"] = FrozenBrush("#242B3A");
            resources["SettingsStroke"] = FrozenBrush("#2A3242");
            resources["SettingsStrokeBright"] = FrozenBrush("#394357");
            resources["SettingsDividerBrush"] = FrozenBrush("#232A38");
            resources["SettingsTrackBg"] = FrozenBrush("#222836");

            // Typography
            resources["SettingsTextPrimary"] = FrozenBrush("#F2F4F8");
            resources["SettingsTextSecondary"] = FrozenBrush("#A6B1C2");
            resources["SettingsTextTertiary"] = FrozenBrush("#6E7B90");
            resources["SettingsMonoText"] = FrozenBrush("#94A2B7");

            // Accents & status
            resources["SettingsAccent"] = FrozenBrush("#6EA7FF");
            resources["SettingsAccentHover"] = FrozenBrush("#A6C9FF");
            resources["SettingsAccentText"] = FrozenBrush("#93BEFF");
            resources["SettingsAccentTintBg"] = FrozenBrush("#162133");
            resources["SettingsAccentBadgeBg"] = FrozenBrush("#1C2C47");
            resources["SettingsAccentTintBorder"] = FrozenBrush("#2B4163");
            resources["SettingsIconAccent"] = FrozenBrush("#7FA8DF");
            resources["SettingsIconMuted"] = FrozenBrush("#9AA6B8");

            resources["AccentGradientBrush"] = FrozenLinear("#6EA7FF", "#8B7CF6", new Point(0, 0), new Point(1, 1));
            resources["AccentGradientHoverBrush"] = FrozenLinear("#82B5FF", "#A08FFF", new Point(0, 0), new Point(1, 1));
            resources["SettingsProgressIndeterminateBrush"] = FrozenGradient(
                Hex("#006EA7FF"), 0.0,
                Hex("#6EA7FF"), 0.35,
                Hex("#8B7CF6"), 0.70,
                Hex("#008B7CF6"), 1.0,
                new Point(0, 0), new Point(1, 0));

            resources["SettingsGreen"] = FrozenBrush("#6FB982");
            resources["SettingsAmber"] = FrozenBrush("#E7A762");
            resources["SettingsDanger"] = FrozenBrush("#F26D6D");

            resources["SettingsSuccessBg"] = FrozenBrush("#13241C");
            resources["SettingsSuccessBorder"] = FrozenBrush("#2B5642");
            resources["SettingsSuccessText"] = FrozenBrush("#A4DBB5");

            resources["SettingsWarningBg"] = FrozenBrush("#231B12");
            resources["SettingsWarningBorder"] = FrozenBrush("#523C22");
            resources["SettingsWarningText"] = FrozenBrush("#F0C58B");

            resources["SettingsDangerBg"] = FrozenBrush("#241418");
            resources["SettingsDangerBorder"] = FrozenBrush("#5E2D35");
            resources["SettingsDangerText"] = FrozenBrush("#F5B2B6");

            // Brand mark & chips
            resources["SettingsBrandMarkBg"] = FrozenBrush("#1E2430");
            resources["SettingsBrandMarkBorder"] = FrozenBrush("#313A4C");
            resources["SettingsBrandMarkBars"] = FrozenBrush("#F1EAD9");
            resources["SettingsChipBg"] = FrozenBrush("#141923");
            resources["SettingsChipBorder"] = FrozenBrush("#2D3648");
            resources["SettingsFooterChipBg"] = FrozenBrush("#161B25");
            resources["SettingsFooterChipBorder"] = FrozenBrush("#2A3242");

            // Navigation
            resources["NavSelectedBrush"] = FrozenLinear("#1CFFFFFF", "#0AFFFFFF", new Point(0, 0), new Point(1, 0));
            resources["SettingsNavHoverBg"] = FrozenBrush("#1E2431");
            resources["SettingsNavIcon"] = FrozenBrush("#8290A5");
            resources["SettingsNavIconHover"] = FrozenBrush("#C2CCE0");
            resources["SettingsNavSelectedText"] = FrozenBrush("#FFFFFF");

            // Window & standard buttons
            resources["SettingsWindowBtnFg"] = FrozenBrush("#9AA6B8");
            resources["SettingsWindowBtnHoverBg"] = FrozenBrush("#1AFFFFFF");
            resources["SettingsWindowBtnHoverFg"] = FrozenBrush("#FFFFFF");
            resources["SettingsWindowBtnPressedBg"] = FrozenBrush("#0DFFFFFF");
            resources["SettingsCloseBtnHoverBg"] = FrozenBrush("#C93B43");
            resources["SettingsCloseBtnPressedBg"] = FrozenBrush("#9E2B32");

            resources["SettingsGhostBg"] = FrozenBrush("#1F2533");
            resources["SettingsGhostBorder"] = FrozenBrush("#313A4D");
            resources["SettingsGhostHoverBg"] = FrozenBrush("#262E3F");
            resources["SettingsGhostHoverBorder"] = FrozenBrush("#434F67");
            resources["SettingsGhostPressedBg"] = FrozenBrush("#171C26");

            resources["SettingsDangerGhostBg"] = FrozenBrush("#22161A");
            resources["SettingsDangerGhostBorder"] = FrozenBrush("#4A2B31");
            resources["SettingsDangerGhostHoverBg"] = FrozenBrush("#2F1B20");
            resources["SettingsDangerGhostHoverBorder"] = FrozenBrush("#753A43");
            resources["SettingsDangerGhostPressedBg"] = FrozenBrush("#1A1114");

            // Toggle switch
            resources["SettingsToggleOffBg"] = FrozenBrush("#2D3546");
            resources["SettingsToggleOffBorder"] = FrozenBrush("#3C475C");
            resources["SettingsToggleOnBg"] = FrozenBrush("#6EA7FF");
            resources["SettingsToggleOnBorder"] = FrozenBrush("#9BC4FF");
            resources["SettingsToggleHoverOverlay"] = FrozenBrush("#14FFFFFF");
            resources["SettingsToggleHoverBorder"] = FrozenBrush("#26FFFFFF");
            resources["SettingsToggleThumb"] = FrozenBrush("#F2F4F8");

            // Segmented control
            resources["SettingsSegmentBg"] = FrozenBrush("#131720");
            resources["SettingsSegmentBorder"] = FrozenBrush("#2A3242");
            resources["SettingsSegmentPillBg"] = FrozenBrush("#253249");
            resources["SettingsSegmentPillBorder"] = FrozenBrush("#3B5785");
            resources["SettingsSegmentSelectedText"] = FrozenBrush("#FFFFFF");

            // Selectable card radio
            resources["SettingsCardRadioBg"] = FrozenBrush("#141923");
            resources["SettingsCardRadioBorder"] = FrozenBrush("#2A3242");
            resources["SettingsCardRadioHoverBg"] = FrozenBrush("#1B2230");
            resources["SettingsCardRadioHoverBorder"] = FrozenBrush("#3E4A60");
            resources["SettingsCardRadioSelectedBg"] = FrozenBrush("#17253B");
            resources["SettingsCardRadioSelectedBorder"] = FrozenBrush("#43669E");
            resources["SettingsCardRadioCheckIcon"] = FrozenBrush("#08111F");

            // ComboBox
            resources["SettingsComboBg"] = FrozenBrush("#141923");
            resources["SettingsComboBorder"] = FrozenBrush("#2A3242");
            resources["SettingsComboHoverBorder"] = FrozenBrush("#3E4A60");
            resources["SettingsComboOpenBorder"] = FrozenBrush("#5680BD");
            resources["SettingsComboArrow"] = FrozenBrush("#8E9AAD");
            resources["SettingsComboArrowHover"] = FrozenBrush("#C7D0DF");
            resources["SettingsComboPopupBg"] = FrozenBrush("#1A1F29");
            resources["SettingsComboPopupBorder"] = FrozenBrush("#313A4D");
            resources["SettingsComboItemHoverBg"] = FrozenBrush("#222836");
            resources["SettingsComboItemSelectedBg"] = FrozenBrush("#25344E");
            resources["SettingsComboItemSelectedFg"] = FrozenBrush("#FFFFFF");

            // Slider, ToolTip, ScrollBar
            resources["SettingsSliderThumbBg"] = FrozenBrush("#12151C");
            resources["SettingsToolTipBg"] = FrozenBrush("#1E2430");
            resources["SettingsToolTipBorder"] = FrozenBrush("#343E52");
            resources["SettingsScrollThumb"] = FrozenBrush("#36FFFFFF");
        }
        else
        {
            // Light editorial paper palette — warm off-white canvas, crisp white elevated cards, rich ink
            resources["SettingsWindowBackground"] = FrozenGradient(
                Hex("#FAF9F5"), 0.0,
                Hex("#F6F5F0"), 0.55,
                Hex("#F2F0EA"), 1.0,
                new Point(0, 0), new Point(1, 1));
            resources["SettingsWindowShadowFill"] = FrozenBrush("#F6F5F0");
            resources["SettingsWindowBorder"] = FrozenBrush("#DEDBD2");

            // Surfaces & borders
            resources["SettingsSurface"] = FrozenBrush("#FFFFFF");
            resources["SettingsSurfaceHover"] = FrozenBrush("#FCFBF9");
            resources["SettingsSurfaceRaised"] = FrozenBrush("#FFFFFF");
            resources["SettingsAgentCardBg"] = FrozenBrush("#FFFFFF");
            resources["SettingsAgentCardHoverBg"] = FrozenBrush("#FAF9F6");
            resources["SettingsInset"] = FrozenBrush("#F2F0EA");
            resources["SettingsInsetBorder"] = FrozenBrush("#E4E1D7");
            resources["SettingsStroke"] = FrozenBrush("#E5E2D9");
            resources["SettingsStrokeBright"] = FrozenBrush("#CFCBC0");
            resources["SettingsDividerBrush"] = FrozenBrush("#E6E3DA");
            resources["SettingsTrackBg"] = FrozenBrush("#E2DFD6");

            // Typography
            resources["SettingsTextPrimary"] = FrozenBrush("#1B1E24");
            resources["SettingsTextSecondary"] = FrozenBrush("#525B6B");
            resources["SettingsTextTertiary"] = FrozenBrush("#737D8F");
            resources["SettingsMonoText"] = FrozenBrush("#4F5A6D");

            // Accents & status
            resources["SettingsAccent"] = FrozenBrush("#2F6FED");
            resources["SettingsAccentHover"] = FrozenBrush("#1E5CD8");
            resources["SettingsAccentText"] = FrozenBrush("#2259C7");
            resources["SettingsAccentTintBg"] = FrozenBrush("#EEF4FF");
            resources["SettingsAccentBadgeBg"] = FrozenBrush("#E1ECFF");
            resources["SettingsAccentTintBorder"] = FrozenBrush("#C7DAFC");
            resources["SettingsIconAccent"] = FrozenBrush("#2F6FED");
            resources["SettingsIconMuted"] = FrozenBrush("#5E687A");

            resources["AccentGradientBrush"] = FrozenLinear("#3B7BF2", "#6A5AE6", new Point(0, 0), new Point(1, 1));
            resources["AccentGradientHoverBrush"] = FrozenLinear("#2A6BE8", "#5948DC", new Point(0, 0), new Point(1, 1));
            resources["SettingsProgressIndeterminateBrush"] = FrozenGradient(
                Hex("#003B7BF2"), 0.0,
                Hex("#3B7BF2"), 0.35,
                Hex("#6A5AE6"), 0.70,
                Hex("#006A5AE6"), 1.0,
                new Point(0, 0), new Point(1, 0));

            resources["SettingsGreen"] = FrozenBrush("#1D8A56");
            resources["SettingsAmber"] = FrozenBrush("#B86B14");
            resources["SettingsDanger"] = FrozenBrush("#CF3B43");

            resources["SettingsSuccessBg"] = FrozenBrush("#ECF8F1");
            resources["SettingsSuccessBorder"] = FrozenBrush("#B5E0C8");
            resources["SettingsSuccessText"] = FrozenBrush("#166841");

            resources["SettingsWarningBg"] = FrozenBrush("#FEF6EB");
            resources["SettingsWarningBorder"] = FrozenBrush("#F0CFA0");
            resources["SettingsWarningText"] = FrozenBrush("#8A4E0A");

            resources["SettingsDangerBg"] = FrozenBrush("#FDF0F1");
            resources["SettingsDangerBorder"] = FrozenBrush("#F0C2C5");
            resources["SettingsDangerText"] = FrozenBrush("#A42830");

            // Brand mark & chips
            resources["SettingsBrandMarkBg"] = FrozenBrush("#1B1E24");
            resources["SettingsBrandMarkBorder"] = FrozenBrush("#2E3440");
            resources["SettingsBrandMarkBars"] = FrozenBrush("#F5EFE1");
            resources["SettingsChipBg"] = FrozenBrush("#F2F0EA");
            resources["SettingsChipBorder"] = FrozenBrush("#DFDCD3");
            resources["SettingsFooterChipBg"] = FrozenBrush("#EFECE4");
            resources["SettingsFooterChipBorder"] = FrozenBrush("#DFDCD2");

            // Navigation
            resources["NavSelectedBrush"] = FrozenLinear("#182F6FED", "#082F6FED", new Point(0, 0), new Point(1, 0));
            resources["SettingsNavHoverBg"] = FrozenBrush("#EAE7DF");
            resources["SettingsNavIcon"] = FrozenBrush("#6F7889");
            resources["SettingsNavIconHover"] = FrozenBrush("#2A3140");
            resources["SettingsNavSelectedText"] = FrozenBrush("#161B24");

            // Window & standard buttons
            resources["SettingsWindowBtnFg"] = FrozenBrush("#667082");
            resources["SettingsWindowBtnHoverBg"] = FrozenBrush("#14000000");
            resources["SettingsWindowBtnHoverFg"] = FrozenBrush("#1B1E24");
            resources["SettingsWindowBtnPressedBg"] = FrozenBrush("#1F000000");
            resources["SettingsCloseBtnHoverBg"] = FrozenBrush("#E0434B");
            resources["SettingsCloseBtnPressedBg"] = FrozenBrush("#BE3138");

            resources["SettingsGhostBg"] = FrozenBrush("#FFFFFF");
            resources["SettingsGhostBorder"] = FrozenBrush("#D9D6CC");
            resources["SettingsGhostHoverBg"] = FrozenBrush("#F5F3EE");
            resources["SettingsGhostHoverBorder"] = FrozenBrush("#BFBBB0");
            resources["SettingsGhostPressedBg"] = FrozenBrush("#EAE7DF");

            resources["SettingsDangerGhostBg"] = FrozenBrush("#FFF5F5");
            resources["SettingsDangerGhostBorder"] = FrozenBrush("#EFC6C9");
            resources["SettingsDangerGhostHoverBg"] = FrozenBrush("#FDE8E9");
            resources["SettingsDangerGhostHoverBorder"] = FrozenBrush("#DC8C92");
            resources["SettingsDangerGhostPressedBg"] = FrozenBrush("#FAD8DA");

            // Toggle switch
            resources["SettingsToggleOffBg"] = FrozenBrush("#D5D2C8");
            resources["SettingsToggleOffBorder"] = FrozenBrush("#C4C0B5");
            resources["SettingsToggleOnBg"] = FrozenBrush("#2F6FED");
            resources["SettingsToggleOnBorder"] = FrozenBrush("#255FD3");
            resources["SettingsToggleHoverOverlay"] = FrozenBrush("#0E000000");
            resources["SettingsToggleHoverBorder"] = FrozenBrush("#1A000000");
            resources["SettingsToggleThumb"] = FrozenBrush("#FFFFFF");

            // Segmented control
            resources["SettingsSegmentBg"] = FrozenBrush("#ECE9E1");
            resources["SettingsSegmentBorder"] = FrozenBrush("#DFDCD2");
            resources["SettingsSegmentPillBg"] = FrozenBrush("#FFFFFF");
            resources["SettingsSegmentPillBorder"] = FrozenBrush("#D2CEC3");
            resources["SettingsSegmentSelectedText"] = FrozenBrush("#1B1E24");

            // Selectable card radio
            resources["SettingsCardRadioBg"] = FrozenBrush("#F6F5F0");
            resources["SettingsCardRadioBorder"] = FrozenBrush("#E2DFD6");
            resources["SettingsCardRadioHoverBg"] = FrozenBrush("#EFECE4");
            resources["SettingsCardRadioHoverBorder"] = FrozenBrush("#CCC8BC");
            resources["SettingsCardRadioSelectedBg"] = FrozenBrush("#EEF4FF");
            resources["SettingsCardRadioSelectedBorder"] = FrozenBrush("#6B9BF5");
            resources["SettingsCardRadioCheckIcon"] = FrozenBrush("#FFFFFF");

            // ComboBox
            resources["SettingsComboBg"] = FrozenBrush("#FFFFFF");
            resources["SettingsComboBorder"] = FrozenBrush("#D8D5CB");
            resources["SettingsComboHoverBorder"] = FrozenBrush("#B8B4A8");
            resources["SettingsComboOpenBorder"] = FrozenBrush("#2F6FED");
            resources["SettingsComboArrow"] = FrozenBrush("#667082");
            resources["SettingsComboArrowHover"] = FrozenBrush("#1B1E24");
            resources["SettingsComboPopupBg"] = FrozenBrush("#FFFFFF");
            resources["SettingsComboPopupBorder"] = FrozenBrush("#D8D5CB");
            resources["SettingsComboItemHoverBg"] = FrozenBrush("#F2F0EA");
            resources["SettingsComboItemSelectedBg"] = FrozenBrush("#E8F0FE");
            resources["SettingsComboItemSelectedFg"] = FrozenBrush("#1B1E24");

            // Slider, ToolTip, ScrollBar
            resources["SettingsSliderThumbBg"] = FrozenBrush("#FFFFFF");
            resources["SettingsToolTipBg"] = FrozenBrush("#FFFFFF");
            resources["SettingsToolTipBorder"] = FrozenBrush("#D8D5CB");
            resources["SettingsScrollThumb"] = FrozenBrush("#301B1E24");
        }
    }

    private static Color Hex(string hex) => (Color)ColorConverter.ConvertFromString(hex);

    private static SolidColorBrush FrozenBrush(string hex) => FrozenBrush(Hex(hex));

    private static SolidColorBrush FrozenBrush(Color color)
    {
        var brush = new SolidColorBrush(color);
        brush.Freeze();
        return brush;
    }

    private static LinearGradientBrush FrozenLinear(string fromHex, string toHex, Point start, Point end)
    {
        var brush = new LinearGradientBrush(Hex(fromHex), Hex(toHex), start, end);
        brush.Freeze();
        return brush;
    }

    private static LinearGradientBrush FrozenGradient(
        Color c0, double o0,
        Color c1, double o1,
        Color c2, double o2,
        Point start, Point end)
    {
        var brush = new LinearGradientBrush { StartPoint = start, EndPoint = end };
        brush.GradientStops.Add(new GradientStop(c0, o0));
        brush.GradientStops.Add(new GradientStop(c1, o1));
        brush.GradientStops.Add(new GradientStop(c2, o2));
        brush.Freeze();
        return brush;
    }

    private static LinearGradientBrush FrozenGradient(
        Color c0, double o0,
        Color c1, double o1,
        Color c2, double o2,
        Color c3, double o3,
        Point start, Point end)
    {
        var brush = new LinearGradientBrush { StartPoint = start, EndPoint = end };
        brush.GradientStops.Add(new GradientStop(c0, o0));
        brush.GradientStops.Add(new GradientStop(c1, o1));
        brush.GradientStops.Add(new GradientStop(c2, o2));
        brush.GradientStops.Add(new GradientStop(c3, o3));
        brush.Freeze();
        return brush;
    }
}
