using System.Diagnostics;
using System.Globalization;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Shapes;
using System.Windows.Threading;
using OpenIsland.Core.Discovery;
using OpenIsland.Core.Localization;
using OpenIsland.Core.Models;
using OpenIsland.Core.Setup;
using Forms = System.Windows.Forms;

namespace OpenIsland.App;

public partial class SettingsWindow : Window
{
    private readonly App _app;
    private readonly DispatcherTimer _connectionTimer;
    private readonly DispatcherTimer _saveToastTimer;
    private readonly DispatcherTimer _agentsFeedbackTimer;
    private readonly DispatcherTimer _copiedTimer;

    private readonly string _appVersion;
    private bool _controlsReady;
    private string _currentPageKey = "connection";
    private ScrollViewer? _activePage;

    private static readonly Color AccentColor = ColorFromHex("#6EA7FF");
    private static readonly Color GreenColor = ColorFromHex("#6FB982");
    private static readonly Color AmberColor = ColorFromHex("#E7A762");
    private static readonly Color DangerColor = ColorFromHex("#F26D6D");
    private static readonly Color TertiaryColor = ColorFromHex("#6F7B8F");

    public SettingsWindow(App app)
    {
        InitializeComponent();
        _app = app;

        var version = Assembly.GetExecutingAssembly().GetName().Version;
        _appVersion = version is null
            ? "1.0.0"
            : $"{version.Major}.{version.Minor}.{Math.Max(0, version.Build)}";

        _connectionTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(2) };
        _connectionTimer.Tick += (_, _) =>
        {
            if (IsLoaded && _currentPageKey == "connection")
                UpdateConnectionPage();
        };

        _saveToastTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(2.4) };
        _saveToastTimer.Tick += (_, _) => HideSaveToast();

        _agentsFeedbackTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(5) };
        _agentsFeedbackTimer.Tick += (_, _) => HideAgentsFeedback();

        _copiedTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(3) };
        _copiedTimer.Tick += (_, _) =>
        {
            _copiedTimer.Stop();
            CopiedFeedbackText.Visibility = Visibility.Collapsed;
        };

        Title = $"Open Island — {Strings.Get("Settings")}";
        TitleVersionText.Text = "v" + _appVersion;

        FitWindowToWorkArea();
        ApplyLocalizedLabels();
        LoadSettingsIntoControls();
        RefreshAgentRows();
        RefreshUsageCards();
        UpdateConnectionPage();
        UpdateAppearancePreview();

        MonitorCombo.SelectionChanged += (_, _) =>
        {
            if (_controlsReady) UpdateAppearancePreview();
        };

        Loaded += (_, _) =>
        {
            _connectionTimer.Start();
            RootFrame.Opacity = 1;
            RootFrame.RenderTransform = new TranslateTransform(0, 0);
        };

        // Select the first page after everything is wired up.
        NavConnection.IsChecked = true;
        SelectPage("connection", animate: false);
        _controlsReady = true;
        UpdateAppearancePreview();

        // Sliding segmented pill for the language selector.
        LanguageSegmentHost.Loaded += (_, _) => ScheduleLanguageSegmentPillSync();
        LanguageSegmentHost.IsVisibleChanged += (_, e) =>
        {
            if (e.NewValue is true)
                ScheduleLanguageSegmentPillSync();
        };
        LanguageSegmentHost.SizeChanged += (_, _) => UpdateLanguageSegmentPill(animate: false);
    }

    private bool _languagePillInitialized;
    private bool _languagePillSyncPending;

    private void LanguageSegment_Checked(object sender, RoutedEventArgs e)
    {
        UpdateLanguageSegmentPill(animate: _languagePillInitialized && _controlsReady);
    }

    private void ScheduleLanguageSegmentPillSync()
    {
        if (_languagePillSyncPending || LanguageSegmentHost is null) return;
        _languagePillSyncPending = true;
        Dispatcher.BeginInvoke(DispatcherPriority.Loaded, new Action(() =>
        {
            _languagePillSyncPending = false;
            UpdateLanguageSegmentPill(animate: false, allowDefer: false);
        }));
    }

    /// <summary>
    /// Moves the highlighted pill behind the 中文 / English segmented button instead of
    /// fading a per-button background in place, so changing the selection has visible motion.
    /// </summary>
    private void UpdateLanguageSegmentPill(bool animate, bool allowDefer = true)
    {
        if (LanguageSegmentHost is null || LanguageSegmentPill is null) return;

        var selected = LanguageEnSegment.IsChecked == true
            ? LanguageEnSegment
            : LanguageZhSegment.IsChecked == true ? LanguageZhSegment : null;
        if (selected is null)
        {
            LanguageSegmentPill.Visibility = Visibility.Collapsed;
            _languagePillInitialized = false;
            return;
        }

        if (!LanguageSegmentHost.IsLoaded || !LanguageSegmentHost.IsVisible)
            return;

        if (selected.RenderSize.Width <= 0 || selected.RenderSize.Height <= 0 ||
            !LanguageSegmentHost.IsAncestorOf(selected))
        {
            if (allowDefer)
                ScheduleLanguageSegmentPillSync();
            return;
        }

        var rect = selected.TransformToAncestor(LanguageSegmentHost)
            .TransformBounds(new Rect(new Point(0, 0), selected.RenderSize));
        if (rect.Width <= 0 || rect.Height <= 0)
        {
            if (allowDefer)
                ScheduleLanguageSegmentPillSync();
            return;
        }

        bool canAnimate = animate
            && _languagePillInitialized
            && LanguageSegmentPill.Visibility == Visibility.Visible
            && LanguageSegmentPill.ActualWidth > 0;

        double fromWidth = LanguageSegmentPill.ActualWidth > 0 ? LanguageSegmentPill.ActualWidth : rect.Width;
        double fromX = LanguageSegmentPillTransform.X;

        LanguageSegmentPill.Visibility = Visibility.Visible;
        LanguageSegmentPill.Width = rect.Width;
        LanguageSegmentPill.Height = rect.Height;
        LanguageSegmentPillTransform.Y = rect.Y;
        // Keep the base value at the new position: the animation then starts from the old
        // rendered position and, with FillBehavior.Stop, lands on this base value.
        LanguageSegmentPillTransform.X = rect.X;
        _languagePillInitialized = true;

        if (!canAnimate || (Math.Abs(fromX - rect.X) < 0.5 && Math.Abs(fromWidth - rect.Width) < 0.5))
        {
            LanguageSegmentPill.BeginAnimation(WidthProperty, null);
            LanguageSegmentPillTransform.BeginAnimation(TranslateTransform.XProperty, null);
            return;
        }

        var ease = new CubicEase { EasingMode = EasingMode.EaseOut };
        LanguageSegmentPill.BeginAnimation(WidthProperty,
            new DoubleAnimation(fromWidth, rect.Width, TimeSpan.FromMilliseconds(200))
            {
                EasingFunction = ease,
                FillBehavior = FillBehavior.Stop
            }, HandoffBehavior.SnapshotAndReplace);
        LanguageSegmentPillTransform.BeginAnimation(TranslateTransform.XProperty,
            new DoubleAnimation(fromX, rect.X, TimeSpan.FromMilliseconds(200))
            {
                EasingFunction = ease,
                FillBehavior = FillBehavior.Stop
            }, HandoffBehavior.SnapshotAndReplace);
    }

    // ================================================================
    // Window chrome
    // ================================================================

    private void FitWindowToWorkArea()
    {
        var work = SystemParameters.WorkArea;
        Width = Math.Min(1040, Math.Max(880, work.Width - 24));
        Height = Math.Min(720, Math.Max(620, work.Height - 24));
        Left = work.Left + (work.Width - Width) / 2;
        Top = work.Top + (work.Height - Height) / 2;
        WindowStartupLocation = WindowStartupLocation.Manual;
    }

    private void TitleBar_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ButtonState != MouseButtonState.Pressed) return;
        if (e.OriginalSource is DependencyObject source && FindAncestor<Button>(source) is not null) return;
        try
        {
            DragMove();
        }
        catch
        {
            // DragMove can throw if the mouse is released at the wrong moment; ignore.
        }
    }

    private static T? FindAncestor<T>(DependencyObject? node) where T : DependencyObject
    {
        while (node is not null)
        {
            if (node is T match) return match;
            node = VisualTreeHelper.GetParent(node);
        }
        return null;
    }

    private void MinimizeButton_Click(object sender, RoutedEventArgs e) =>
        WindowState = WindowState.Minimized;

    private void CloseButton_Click(object sender, RoutedEventArgs e) => Close();

    private void Window_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
        {
            e.Handled = true;
            Close();
        }
    }

    private void Window_Closing(object? sender, System.ComponentModel.CancelEventArgs e)
    {
        _connectionTimer.Stop();
        _saveToastTimer.Stop();
        _agentsFeedbackTimer.Stop();
        _copiedTimer.Stop();
    }

    // ================================================================
    // Navigation / page transitions
    // ================================================================

    private void NavItem_Checked(object sender, RoutedEventArgs e)
    {
        if (sender is RadioButton { IsChecked: true } rb && !string.IsNullOrWhiteSpace(rb.Uid))
            SelectPage(rb.Uid, animate: _controlsReady);
    }

    private void SelectPage(string key, bool animate)
    {
        ScrollViewer? page = key switch
        {
            "connection" => PageConnection,
            "agents" => PageAgents,
            "usage" => PageUsage,
            "appearance" => PageAppearance,
            "general" => PageGeneral,
            "about" => PageAbout,
            _ => null
        };
        if (page is null) return;
        if (ReferenceEquals(page, _activePage))
        {
            UpdatePageHeader(key);
            return;
        }

        if (_activePage is not null) _activePage.Visibility = Visibility.Collapsed;
        page.Visibility = Visibility.Visible;
        page.BeginAnimation(OpacityProperty, null);

        var translate = page.RenderTransform as TranslateTransform ?? new TranslateTransform();
        page.RenderTransform = translate;

        if (animate)
        {
            page.Opacity = 0;
            translate.Y = 10;
            page.BeginAnimation(OpacityProperty,
                new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(180))
                {
                    EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
                });
            translate.BeginAnimation(TranslateTransform.YProperty,
                new DoubleAnimation(10, 0, TimeSpan.FromMilliseconds(180))
                {
                    EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
                });
        }
        else
        {
            page.Opacity = 1;
            translate.Y = 0;
        }

        _activePage = page;
        _currentPageKey = key;
        UpdatePageHeader(key);

        switch (key)
        {
            case "connection":
                UpdateConnectionPage();
                break;
            case "agents":
                RefreshAgentRows();
                break;
            case "usage":
                RefreshUsageCards();
                break;
            case "appearance":
                UpdateAppearancePreview();
                break;
            case "general":
                ScheduleLanguageSegmentPillSync();
                break;
        }
    }

    private void UpdatePageHeader(string key)
    {
        (string Title, string Desc, string Index) info = key switch
        {
            "agents" => ("AgentSetupTitle", "AgentSetupDesc", "02"),
            "usage" => ("UsageTitle", "UsageDesc", "03"),
            "appearance" => ("AppearanceTitle", "AppearanceDesc", "04"),
            "general" => ("GeneralTitle", "GeneralDesc", "05"),
            "about" => ("AboutTitle", "AboutDesc", "06"),
            _ => ("ConnectionTitle", "ConnectionDesc", "01")
        };
        PageTitleText.Text = Strings.Get(info.Title);
        PageSubtitleText.Text = Strings.Get(info.Desc);
        PageIndexText.Text = info.Index;
    }

    // ================================================================
    // Localisation
    // ================================================================

    private void ApplyLocalizedLabels()
    {
        Title = $"Open Island — {Strings.Get("Settings")}";
        if (SelfTestFeedback is not null)
        {
            SelfTestFeedback.Visibility = Visibility.Collapsed;
            AgentsFeedback.Visibility = Visibility.Collapsed;
        }
        MinimizeButton.ToolTip = Strings.Get("Minimize");
        CloseButton.ToolTip = Strings.Get("Close");

        SidebarSectionLabel.Text = Strings.Get("SettingsSection");
        NavConnection.Content = Strings.Get("NavConnection");
        NavAgents.Content = Strings.Get("NavAgents");
        NavUsage.Content = Strings.Get("NavUsage");
        NavAppearance.Content = Strings.Get("NavAppearance");
        NavGeneral.Content = Strings.Get("NavGeneral");
        NavAbout.Content = Strings.Get("NavAbout");
        SidebarHotkeyLabel.Text = Strings.Get("HotkeyLabel");
        SidebarHotkeyText.Text = FormatHotkey(_app.Settings.ToggleHotkey);
        SidebarLogsLabel.Text = Strings.Get("OpenLogFolder");

        ApplyHintText.Text = Strings.Get("SettingsApplyHint");
        SaveButtonText.Text = Strings.Get("SaveSettings");
        SaveToastText.Text = Strings.Get("Saved");
        UpdatePageHeader(_currentPageKey);

        // Connection -------------------------------------------------
        ConnPipeLabel.Text = Strings.Get("PipeAddress");
        ActiveSessionsLabel.Text = Strings.Get("ActiveSessionCount");
        InstalledAgentsLabel.Text = Strings.Get("InstalledAgentCount");
        EventCountLabel.Text = Strings.Get("EventCount");
        LastEventLabel.Text = Strings.Get("LastHookEvent");
        NoEventsTitle.Text = Strings.Get("NoEventsYet");
        NoEventsDesc.Text = Strings.Get("NoEventsHintDesc");
        SelfTestTitle.Text = Strings.Get("ConnectionSelfTest");
        SelfTestDesc.Text = Strings.Get("ConnectionSelfTestDesc");
        RunSelfTestText.Text = Strings.Get("RunConnectionSelfTest");
        OpenLogsButtonText.Text = Strings.Get("OpenLogFolder");
        HooksTitle.Text = Strings.Get("HooksBinary");
        OpenLogsButton.ToolTip = Strings.Get("OpenLogFolder");

        // Agents -----------------------------------------------------
        AgentSummaryHint.Text = Strings.Get("AgentSummaryHint");
        RefreshAgentsText.Text = Strings.Get("RefreshAgents");
        InstallClaudeStatuslineText.Text = Strings.Get("InstallClaudeStatusline");
        RestartAgentHintText.Text = Strings.Get("RestartAgentHint");
        InstallClaudeStatuslineButton.ToolTip = Strings.Get("InstallClaudeStatusline");

        // Usage ------------------------------------------------------
        RefreshUsageText.Text = Strings.Get("RefreshUsage");
        UsagePrivacyText.Text = Strings.Get("UsagePrivacy");
        UsageHintTitle.Text = Strings.Get("UsageWaiting");
        UsageHintDesc.Text = Strings.Get("UsageNoDataHint");
        UsageFooterHint.Text = Strings.Get("UsageFooterHint");
        RefreshUsageButton.ToolTip = Strings.Get("RefreshUsage");

        // Appearance -------------------------------------------------
        MonitorCardTitle.Text = Strings.Get("MonitorLabel");
        MonitorCardDesc.Text = Strings.Get("MonitorHint");
        PositionCardTitle.Text = Strings.Get("Position");
        PositionHintText.Text = Strings.Get("PositionHint");
        PosTopLabel.Text = Strings.Get("TopCenter");
        PosBottomLabel.Text = Strings.Get("BottomCenter");
        ThemeCardTitle.Text = Strings.Get("Theme");
        ThemeHintText.Text = Strings.Get("ThemeHint");
        ThemeInkLabel.Text = Strings.Get("ThemePureBlack");
        ThemeInkDescText.Text = Strings.Get("ThemePureBlackDesc");
        ThemeGraphiteLabel.Text = Strings.Get("ThemeGraphite");
        ThemeGraphiteDescText.Text = Strings.Get("ThemeGraphiteDesc");
        PreviewCardTitle.Text = Strings.Get("IslandPreview");
        PreviewHintText.Text = Strings.Get("PreviewHint");

        // General ----------------------------------------------------
        SystemTitle.Text = Strings.Get("SystemSection");
        LanguageLabel.Text = Strings.Get("LanguageLabel");
        LanguageHintText.Text = Strings.Get("LanguageHint");
        StartupLabel.Text = Strings.Get("LaunchAtStartup");
        StartupHintText.Text = Strings.Get("StartupHint");
        NotificationTitle.Text = Strings.Get("NotificationSection");
        SoundLabel.Text = Strings.Get("EnableSound");
        SoundHintText.Text = Strings.Get("SoundHint");
        SoundSchemeLabel.Text = Strings.Get("SoundSchemeLabel");
        PreviewSoundText.Text = Strings.Get("PreviewSound");
        ApplySoundOptionLabels();
        AutoExpandApprovalLabel.Text = Strings.Get("AutoExpandApproval");
        AutoExpandApprovalHintText.Text = Strings.Get("AutoExpandApprovalHint");
        BehaviorTitle.Text = Strings.Get("BehaviorSection");
        AutoExpandCompleteLabel.Text = Strings.Get("AutoExpandComplete");
        AutoExpandCompleteHintText.Text = Strings.Get("AutoExpandCompleteHint");
        CoalesceRecurringLabel.Text = Strings.Get("CoalesceRecurring");
        CoalesceRecurringHintText.Text = Strings.Get("CoalesceRecurringHint");
        AutoHideIslandLabel.Text = Strings.Get("AutoHideIsland");
        AutoHideIslandHintText.Text = Strings.Get("AutoHideIslandHint");
        AutoHideDelayLabel.Text = Strings.Get("AutoHideDelay");
        AutoHideDelayHintText.Text = Strings.Get("AutoHideDelayHint");
        CompletionDurationLabel.Text = Strings.Get("CompletionCardDuration");
        CompletionDurationHintText.Text = Strings.Get("CompletionDurationHint");
        PollIntervalLabel.Text = Strings.Get("PollInterval");
        PollIntervalHintText.Text = Strings.Get("PollIntervalHint");
        CodexAppServerLabel.Text = Strings.Get("CodexAppServer");
        CodexAppServerHintText.Text = Strings.Get("CodexAppServerHint");
        PreviewSoundButton.ToolTip = Strings.Get("PreviewSound");
        UpdateSliderLabels();

        // About ------------------------------------------------------
        AboutVersionText.Text = string.Format(Strings.Get("VersionFormat"), _appVersion);
        AboutTaglineText.Text = Strings.Get("AboutTagline");
        AboutTechText.Text = Strings.Get("AboutTech");
        UpstreamLabel.Text = Strings.Get("AboutUpstream");
        LicenseLabel.Text = Strings.Get("AboutLicense");
        LicenseValueText.Text = Strings.Get("LicenseValue");
        HotkeyCardLabel.Text = Strings.Get("AboutHotkey");
        HotkeyValueText.Text = FormatHotkey(_app.Settings.ToggleHotkey);
        DataDirLabel.Text = Strings.Get("AboutDataDir");
        CopyDataDirLabel.Text = Strings.Get("Copy");
        CopiedFeedbackText.Text = Strings.Get("Copied");
        PrivacyTitleText.Text = Strings.Get("PrivacyZeroTelemetry");
        PrivacyDescText.Text = Strings.Get("PrivacyDesc");
        DataDirPathText.Text = BridgeConstants.AppDataDir;
        DataDirPathText.ToolTip = BridgeConstants.AppDataDir;
        UpstreamLinkButton.ToolTip = Strings.Get("ViewSource");
        CopyDataDirButton.ToolTip = Strings.Get("Copy");
    }

    private static string FormatHotkey(string? hotkey)
    {
        if (string.IsNullOrWhiteSpace(hotkey)) return "Ctrl Alt I";
        return hotkey.Replace("+", " ").Replace("  ", " ").Trim();
    }

    private void ApplySoundOptionLabels()
    {
        var keys = new[]
        {
            "SoundAsterisk", "SoundExclamation", "SoundBeep", "SoundHand",
            "SoundNotify", "SoundDing", "SoundChimes", "SoundTada", "SoundWindowsNotify"
        };
        for (var i = 0; i < SoundCombo.Items.Count && i < keys.Length; i++)
        {
            if (SoundCombo.Items[i] is ComboBoxItem item)
            {
                item.Content = Strings.Get(keys[i]);
            }
        }
    }

    // ================================================================
    // Settings load / save
    // ================================================================

    private void LoadSettingsIntoControls()
    {
        _controlsReady = false;
        var s = _app.Settings;

        var isEnglish = !s.Language.StartsWith("zh", StringComparison.OrdinalIgnoreCase);
        LanguageZhSegment.IsChecked = !isEnglish;
        LanguageEnSegment.IsChecked = isEnglish;

        StartupToggle.IsChecked = s.LaunchAtStartup;
        SoundToggle.IsChecked = s.SoundEnabled;
        AutoExpandApprovalToggle.IsChecked = s.AutoExpandOnApproval;
        AutoExpandCompleteToggle.IsChecked = s.AutoExpandOnCompletion;
        CoalesceRecurringToggle.IsChecked = s.CoalesceRecurringSessions;
        AutoHideIslandToggle.IsChecked = s.AutoHideIsland;
        AutoHideDelaySlider.Value = Math.Clamp(s.AutoHideDelaySeconds, 5, 120);
        CodexAppServerToggle.IsChecked = s.EnableCodexAppServer;

        var soundIndex = 0;
        for (var i = 0; i < SoundCombo.Items.Count; i++)
        {
            if (SoundCombo.Items[i] is ComboBoxItem item &&
                string.Equals(item.Tag?.ToString(), s.NotificationSound, StringComparison.OrdinalIgnoreCase))
            {
                soundIndex = i;
                break;
            }
        }
        SoundCombo.SelectedIndex = soundIndex;

        CompletionDurationSlider.Value = Math.Clamp(s.CompletionCardSeconds, 3, 30);
        PollSlider.Value = Math.Clamp(s.PollIntervalMs, 1000, 15000);

        PopulateMonitorCombo(s.MonitorIndex);

        PosTopRadio.IsChecked = s.EdgePosition != IslandEdgePosition.BottomCenter;
        PosBottomRadio.IsChecked = s.EdgePosition == IslandEdgePosition.BottomCenter;

        ThemeInkRadio.IsChecked = s.ThemeStyle != IslandThemeStyle.Graphite;
        ThemeGraphiteRadio.IsChecked = s.ThemeStyle == IslandThemeStyle.Graphite;

        UpdateSliderLabels();
        _controlsReady = true;
        UpdateAppearancePreview();
        if (LanguageSegmentHost?.IsVisible == true)
            ScheduleLanguageSegmentPillSync();
    }

    private void PopulateMonitorCombo(int selectedIndex)
    {
        MonitorCombo.Items.Clear();
        var screens = Forms.Screen.AllScreens;
        if (screens.Length == 0)
        {
            MonitorCombo.Items.Add(new ComboBoxItem
            {
                Content = string.Format(Strings.Get("DisplayNumber"), 1),
                Tag = 0,
                Style = (Style)FindResource("DarkComboBoxItem")
            });
            MonitorCombo.SelectedIndex = 0;
            return;
        }

        for (var i = 0; i < screens.Length; i++)
        {
            var screen = screens[i];
            var label = string.Format(Strings.Get("DisplayNumber"), i + 1)
                        + $"  ·  {screen.Bounds.Width}×{screen.Bounds.Height}";
            if (screen.Primary) label += "  ·  " + Strings.Get("PrimaryDisplayShort");
            MonitorCombo.Items.Add(new ComboBoxItem
            {
                Content = label,
                Tag = i,
                Style = (Style)FindResource("DarkComboBoxItem")
            });
        }

        MonitorCombo.SelectedIndex = Math.Clamp(selectedIndex, 0, screens.Length - 1);
    }

    private void SaveButton_Click(object sender, RoutedEventArgs e)
    {
        var previous = _app.Settings;
        var updated = new AppSettings
        {
            Language = LanguageEnSegment.IsChecked == true ? "en" : "zh-CN",
            MonitorIndex = Math.Max(0, MonitorCombo.SelectedIndex),
            EdgePosition = PosBottomRadio.IsChecked == true
                ? IslandEdgePosition.BottomCenter
                : IslandEdgePosition.TopCenter,
            ThemeStyle = ThemeGraphiteRadio.IsChecked == true
                ? IslandThemeStyle.Graphite
                : IslandThemeStyle.PureBlack,
            LaunchAtStartup = StartupToggle.IsChecked == true,
            SoundEnabled = SoundToggle.IsChecked == true,
            NotificationSound = (SoundCombo.SelectedItem as ComboBoxItem)?.Tag?.ToString()
                                ?? previous.NotificationSound,
            AutoExpandOnApproval = AutoExpandApprovalToggle.IsChecked == true,
            AutoExpandOnCompletion = AutoExpandCompleteToggle.IsChecked == true,
            CoalesceRecurringSessions = CoalesceRecurringToggle.IsChecked == true,
            AutoHideIsland = AutoHideIslandToggle.IsChecked == true,
            AutoHideDelaySeconds = (int)Math.Round(AutoHideDelaySlider.Value),
            EnableCodexAppServer = CodexAppServerToggle.IsChecked == true,
            PollIntervalMs = (int)Math.Round(PollSlider.Value),
            CompletionCardSeconds = (int)Math.Round(CompletionDurationSlider.Value),
            ToggleHotkey = previous.ToggleHotkey,
            AutoInstallOpenCodePlugin = previous.AutoInstallOpenCodePlugin
        };

        try
        {
            _app.ApplySettings(updated);
            ApplyLocalizedLabels();
            LoadSettingsIntoControls();
            RefreshAgentRows();
            RefreshUsageCards();
            UpdateConnectionPage();
            UpdateAppearancePreview();
            ShowSaveToast();
        }
        catch (Exception ex)
        {
            ShowSaveToast(ex.Message, ok: false);
        }
    }

    private void ShowSaveToast(string? message = null, bool ok = true)
    {
        SaveToastText.Text = string.IsNullOrWhiteSpace(message) ? Strings.Get("Saved") : message;
        SaveToast.Background = new SolidColorBrush(ok
            ? Color.FromArgb(255, 19, 36, 28)
            : Color.FromArgb(255, 40, 20, 24));
        SaveToast.BorderBrush = new SolidColorBrush(ok
            ? Color.FromArgb(255, 44, 88, 67)
            : Color.FromArgb(255, 110, 48, 58));
        SaveToastText.Foreground = new SolidColorBrush(ok ? ColorFromHex("#8ED7A5") : DangerColor);
        SaveToast.Visibility = Visibility.Visible;
        SaveToast.Opacity = 0;
        var tt = SaveToast.RenderTransform as TranslateTransform;
        if (tt is null)
        {
            tt = new TranslateTransform();
            SaveToast.RenderTransform = tt;
        }
        tt.Y = 5;
        SaveToast.BeginAnimation(OpacityProperty,
            new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(180))
            {
                EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
            });
        tt.BeginAnimation(TranslateTransform.YProperty,
            new DoubleAnimation(5, 0, TimeSpan.FromMilliseconds(220))
            {
                EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
            });
        _saveToastTimer.Stop();
        _saveToastTimer.Start();
    }

    private void HideSaveToast()
    {
        _saveToastTimer.Stop();
        var fade = new DoubleAnimation(1, 0, TimeSpan.FromMilliseconds(180));
        fade.Completed += (_, _) => SaveToast.Visibility = Visibility.Collapsed;
        SaveToast.BeginAnimation(OpacityProperty, fade);
    }

    private void UpdateSliderLabels()
    {
        if (CompletionDurationValue is null || PollIntervalValue is null) return;
        CompletionDurationValue.Text =
            $"{(int)Math.Round(CompletionDurationSlider.Value)} {Strings.Get("Seconds")}";
        PollIntervalValue.Text =
            $"{(int)Math.Round(PollSlider.Value)} {Strings.Get("Milliseconds")}";
        AutoHideDelayValue.Text =
            $"{(int)Math.Round(AutoHideDelaySlider.Value)} {Strings.Get("Seconds")}";
    }

    private void Slider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (!_controlsReady) return;
        UpdateSliderLabels();
    }

    private void AppearanceControl_Changed(object sender, RoutedEventArgs e)
    {
        if (!_controlsReady) return;
        UpdateAppearancePreview();
    }

    private void UpdateAppearancePreview()
    {
        if (PreviewPill is null) return;

        var graphite = ThemeGraphiteRadio.IsChecked == true;
        PreviewPill.Background = new SolidColorBrush(ColorFromHex(graphite ? "#232832" : "#08090B"));
        PreviewPill.BorderBrush = new SolidColorBrush(ColorFromHex(graphite ? "#4A5262" : "#333A46"));

        var top = PosTopRadio.IsChecked != false;
        PreviewPill.VerticalAlignment = top ? VerticalAlignment.Top : VerticalAlignment.Bottom;
        PreviewPill.Margin = top ? new Thickness(0, 12, 0, 0) : new Thickness(0, 0, 0, 12);
        PreviewPill.BeginAnimation(OpacityProperty,
            new DoubleAnimation(0.55, 1, TimeSpan.FromMilliseconds(180)));

        var active = _app.Reducer.GetActiveSessions().Count;
        PreviewStatusText.Text = active > 0
            ? string.Format(Strings.Get("PreviewStatus"), active)
            : Strings.Get("PreviewIdle");

        var screens = Forms.Screen.AllScreens;
        var index = MonitorCombo.SelectedIndex;
        if (index >= 0 && index < screens.Length)
        {
            var screen = screens[index];
            PreviewMetaText.Text = $"{screen.Bounds.Width} × {screen.Bounds.Height}";
        }
    }

    // ================================================================
    // Connection page
    // ================================================================

    private void UpdateConnectionPage()
    {
        if (ConnStatusTitle is null) return;
        ConnectionDiagnostics d;
        try
        {
            d = _app.GetConnectionDiagnostics();
        }
        catch
        {
            return;
        }

        var running = d.BridgeRunning;
        ConnStatusTitle.Text = Strings.Get(running ? "BridgeRunningTitle" : "BridgeStoppedTitle");
        ConnStatusSubtitle.Text = Strings.Get(running ? "BridgeRunningDesc" : "BridgeStoppedDesc");
        ConnStatusDot.Fill = new SolidColorBrush(running ? GreenColor : DangerColor);
        ConnPulseRing.Fill = new SolidColorBrush(running ? GreenColor : DangerColor);
        ConnPulseRing.Visibility = running ? Visibility.Visible : Visibility.Collapsed;

        ConnPipePathText.Text = d.PipePath;
        ConnPipePathText.ToolTip = d.PipePath;
        ActiveSessionsValue.Text = d.ActiveSessions.ToString();
        InstalledAgentsValue.Text = $"{d.InstalledAgents}/{d.TotalAgents}";
        EventCountValue.Text = d.EventCount.ToString();
        LastEventValue.Text = FormatRelativeTime(d.LastEventAt);
        LastEventValue.ToolTip = d.LastEventAt is null
            ? Strings.Get("NoEventsYet")
            : $"{d.LastEventName ?? "event"} · {d.LastEventAt.Value.LocalDateTime:yyyy-MM-dd HH:mm:ss}";

        NoEventsHint.Visibility = d.HasReceivedEvents ? Visibility.Collapsed : Visibility.Visible;

        HooksPathText.Text = d.HooksBinaryPath;
        HooksPathText.ToolTip = d.HooksBinaryPath;
        if (d.HooksBinaryPresent)
        {
            HooksStatusText.Text = Strings.Get("HooksBinaryOk");
            HooksStatusText.Foreground = new SolidColorBrush(GreenColor);
            HooksStatusChip.Background = AlphaBrush(GreenColor, 24);
            HooksStatusChip.BorderBrush = AlphaBrush(GreenColor, 78);
            HooksVersionText.Text = string.Format(Strings.Get("HookVersionFormat"), d.HooksBinaryVersion);
        }
        else
        {
            HooksStatusText.Text = Strings.Get("HooksBinaryMissing");
            HooksStatusText.Foreground = new SolidColorBrush(DangerColor);
            HooksStatusChip.Background = AlphaBrush(DangerColor, 22);
            HooksStatusChip.BorderBrush = AlphaBrush(DangerColor, 72);
            HooksVersionText.Text = string.Empty;
        }
    }

    private string FormatRelativeTime(DateTimeOffset? at)
    {
        if (at is null) return Strings.Get("Never");
        var delta = DateTimeOffset.Now - at.Value;
        if (delta.TotalSeconds < 60) return Strings.Get("JustNow");
        if (delta.TotalMinutes < 60)
            return string.Format(Strings.Get("MinutesAgo"), Math.Max(1, (int)delta.TotalMinutes));
        if (delta.TotalHours < 24)
            return string.Format(Strings.Get("HoursAgo"), Math.Max(1, (int)delta.TotalHours));
        return string.Format(Strings.Get("DaysAgo"), Math.Max(1, (int)delta.TotalDays));
    }

    private void RunSelfTestButton_Click(object sender, RoutedEventArgs e)
    {
        var result = _app.SendTestEvent();
        SelfTestFeedback.Visibility = Visibility.Visible;
        SelfTestFeedback.Text = result.Message;
        SelfTestFeedback.Foreground = new SolidColorBrush(result.Ok ? GreenColor : DangerColor);
        UpdateConnectionPage();
    }

    private void OpenLogsButton_Click(object sender, RoutedEventArgs e) => _app.OpenLogFolder();

    // ================================================================
    // Agents page
    // ================================================================

    private void RefreshAgentsButton_Click(object sender, RoutedEventArgs e) => RefreshAgentRows();

    private void InstallClaudeStatuslineButton_Click(object sender, RoutedEventArgs e) =>
        RunAgentAction(() => _app.SetupManager.InstallClaudeStatusLine());

    private void RefreshAgentRows()
    {
        if (AgentsListHost is null) return;
        AgentsListHost.Children.Clear();

        List<AgentInstallState> statuses;
        try
        {
            statuses = _app.SetupManager.GetAllStatuses();
        }
        catch (Exception ex)
        {
            ShowAgentsFeedback(ex.Message, ok: false);
            return;
        }

        var installed = statuses.Count(s => s.Status == HookInstallStatus.Installed);
        AgentSummaryText.Text = string.Format(Strings.Get("AgentSummary"), installed, statuses.Count);
        AgentSummaryHint.Text = Strings.Get("AgentSummaryHint");

        foreach (var status in statuses)
            AgentsListHost.Children.Add(BuildAgentCard(status));
    }

    private FrameworkElement BuildAgentCard(AgentInstallState status)
    {
        var agent = status.Agent;
        var accent = ColorFromHex(agent.AccentHex);
        var card = new Border { Style = (Style)FindResource("AgentCard") };

        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var iconTile = new Border
        {
            Width = 38,
            Height = 38,
            CornerRadius = new CornerRadius(10),
            Background = AlphaBrush(accent, 32),
            BorderBrush = AlphaBrush(accent, 66),
            BorderThickness = new Thickness(1),
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 0, 12, 0)
        };
        var initial = string.IsNullOrWhiteSpace(agent.DisplayName)
            ? "A"
            : agent.DisplayName.Trim()[..1].ToUpperInvariant();
        iconTile.Child = new TextBlock
        {
            Text = initial,
            FontFamily = (FontFamily)FindResource("FontDisplay"),
            FontSize = 15,
            FontWeight = FontWeights.SemiBold,
            Foreground = new SolidColorBrush(accent),
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center
        };
        Grid.SetColumn(iconTile, 0);
        grid.Children.Add(iconTile);

        var textStack = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        var headerRow = new StackPanel { Orientation = Orientation.Horizontal };
        headerRow.Children.Add(new TextBlock
        {
            Text = agent.DisplayName,
            FontSize = 13,
            FontWeight = FontWeights.SemiBold,
            Foreground = (Brush)FindResource("BrushTextPrimary"),
            VerticalAlignment = VerticalAlignment.Center
        });

        var (statusColor, statusText) = status.Status switch
        {
            HookInstallStatus.Installed => (GreenColor, Strings.Get("Installed")),
            HookInstallStatus.Partial => (AmberColor, Strings.Get("Partial")),
            _ => (TertiaryColor, Strings.Get("NotInstalled"))
        };
        var chip = new Border
        {
            Style = (Style)FindResource("StatusChip"),
            Background = AlphaBrush(statusColor, 20),
            BorderBrush = AlphaBrush(statusColor, 66),
            Margin = new Thickness(8, 0, 0, 0),
            Padding = new Thickness(6, 1, 6, 1)
        };
        var chipStack = new StackPanel { Orientation = Orientation.Horizontal };
        chipStack.Children.Add(new Ellipse
        {
            Width = 5,
            Height = 5,
            Fill = new SolidColorBrush(statusColor),
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 0, 5, 0)
        });
        chipStack.Children.Add(new TextBlock
        {
            Text = statusText,
            FontSize = 10,
            FontWeight = FontWeights.SemiBold,
            Foreground = new SolidColorBrush(statusColor),
            VerticalAlignment = VerticalAlignment.Center
        });
        chip.Child = chipStack;
        headerRow.Children.Add(chip);
        textStack.Children.Add(headerRow);

        var path = new TextBlock
        {
            Text = status.ConfigPath,
            FontFamily = (FontFamily)FindResource("FontMono"),
            FontSize = 10.5,
            Foreground = new SolidColorBrush(ColorFromHex("#677386")),
            Margin = new Thickness(0, 4, 0, 0),
            TextTrimming = TextTrimming.CharacterEllipsis,
            ToolTip = status.ConfigPath
        };
        textStack.Children.Add(path);
        Grid.SetColumn(textStack, 1);
        grid.Children.Add(textStack);

        var actions = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(12, 0, 0, 0)
        };
        if (agent.IsAutoDetected)
        {
            actions.Children.Add(new TextBlock
            {
                Text = Strings.Get("AutoDetected"),
                FontSize = 11,
                Foreground = new SolidColorBrush(GreenColor),
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(4, 0, 0, 0)
            });
        }
        else if (status.Status == HookInstallStatus.Installed)
        {
            var repair = MakeSmallButton(Strings.Get("RepairShort"), "GhostButton");
            repair.Click += (_, _) => RunAgentAction(() => _app.SetupManager.Install(agent.Source));
            var uninstall = MakeSmallButton(Strings.Get("Uninstall"), "DangerGhostButton");
            uninstall.Click += (_, _) => RunAgentAction(() => _app.SetupManager.Uninstall(agent.Source));
            actions.Children.Add(repair);
            actions.Children.Add(uninstall);
        }
        else if (status.Status == HookInstallStatus.Partial)
        {
            var repair = MakeSmallButton(Strings.Get("RepairShort"), "GhostButton");
            repair.Click += (_, _) => RunAgentAction(() => _app.SetupManager.Install(agent.Source));
            var uninstall = MakeSmallButton(Strings.Get("Uninstall"), "DangerGhostButton");
            uninstall.Click += (_, _) => RunAgentAction(() => _app.SetupManager.Uninstall(agent.Source));
            actions.Children.Add(repair);
            actions.Children.Add(uninstall);
        }
        else
        {
            var install = MakeSmallButton(Strings.Get("Install"), "AccentButton");
            install.Padding = new Thickness(14, 5, 14, 5);
            install.Click += (_, _) => RunAgentAction(() => _app.SetupManager.Install(agent.Source));
            actions.Children.Add(install);
        }
        Grid.SetColumn(actions, 2);
        grid.Children.Add(actions);

        card.Child = grid;
        return card;
    }

    private Button MakeSmallButton(string text, string styleKey)
    {
        var style = (Style)FindResource(styleKey);
        var button = new Button
        {
            Content = text,
            Style = style,
            FontSize = 11,
            Padding = new Thickness(13, 5, 13, 5),
            Margin = new Thickness(0, 0, 6, 0),
            MinWidth = 54
        };
        return button;
    }

    private void RunAgentAction(Func<(bool Ok, string Message)> action)
    {
        try
        {
            var result = action();
            ShowAgentsFeedback(result.Message, result.Ok);
            RefreshAgentRows();
            UpdateConnectionPage();
        }
        catch (Exception ex)
        {
            ShowAgentsFeedback(ex.Message, ok: false);
        }
    }

    private void ShowAgentsFeedback(string message, bool ok)
    {
        if (AgentsFeedback is null) return;
        var color = ok ? GreenColor : DangerColor;
        AgentsFeedbackDot.Fill = new SolidColorBrush(color);
        AgentsFeedbackText.Text = message;
        AgentsFeedbackText.Foreground = new SolidColorBrush(ok ? ColorFromHex("#A9D9B9") : ColorFromHex("#F3B3B3"));
        AgentsFeedback.Background = new SolidColorBrush(ok
            ? Color.FromArgb(255, 17, 26, 32)
            : Color.FromArgb(255, 32, 18, 21));
        AgentsFeedback.BorderBrush = new SolidColorBrush(ok
            ? Color.FromArgb(255, 42, 64, 80)
            : Color.FromArgb(255, 86, 42, 49));
        AgentsFeedback.Visibility = Visibility.Visible;
        AgentsFeedback.BeginAnimation(OpacityProperty,
            new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(160)));
        _agentsFeedbackTimer.Stop();
        _agentsFeedbackTimer.Start();
    }

    private void HideAgentsFeedback()
    {
        _agentsFeedbackTimer.Stop();
        var fade = new DoubleAnimation(1, 0, TimeSpan.FromMilliseconds(180));
        fade.Completed += (_, _) => AgentsFeedback.Visibility = Visibility.Collapsed;
        AgentsFeedback.BeginAnimation(OpacityProperty, fade);
    }

    // ================================================================
    // Usage page
    // ================================================================

    private void RefreshUsageButton_Click(object sender, RoutedEventArgs e) => RefreshUsageCards();

    private void RefreshUsageCards()
    {
        if (UsageCardsHost is null) return;
        UsageCardsHost.Children.Clear();

        List<UsageWindowInfo> usages;
        try
        {
            usages = TranscriptDiscovery.ReadUsageWindows();
        }
        catch
        {
            usages = new List<UsageWindowInfo>();
        }

        var hasAnyData = usages.Any(u => u.LastUpdated.HasValue);
        UsageHintBanner.Visibility = hasAnyData ? Visibility.Collapsed : Visibility.Visible;

        foreach (var usage in usages)
            UsageCardsHost.Children.Add(BuildUsageCard(usage));
    }

    private FrameworkElement BuildUsageCard(UsageWindowInfo usage)
    {
        var hasData = usage.LastUpdated.HasValue;
        var accent = usage.AgentName.Contains("Claude", StringComparison.OrdinalIgnoreCase)
            ? ColorFromHex(AgentRegistry.Get(AgentSource.Claude).AccentHex)
            : usage.AgentName.Contains("Codex", StringComparison.OrdinalIgnoreCase)
                ? ColorFromHex(AgentRegistry.Get(AgentSource.Codex).AccentHex)
                : AccentColor;

        var card = new Border
        {
            Style = (Style)FindResource("Card"),
            Margin = new Thickness(0, 0, 0, 12),
            Padding = new Thickness(18)
        };
        var stack = new StackPanel();

        var header = new Grid();
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var headerLeft = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        var iconTile = new Border
        {
            Width = 34,
            Height = 34,
            CornerRadius = new CornerRadius(9),
            Background = AlphaBrush(accent, 30),
            BorderBrush = AlphaBrush(accent, 60),
            BorderThickness = new Thickness(1),
            Margin = new Thickness(0, 0, 11, 0)
        };
        iconTile.Child = new TextBlock
        {
            Text = usage.AgentName.Contains("Claude", StringComparison.OrdinalIgnoreCase) ? "C" : "X",
            FontFamily = (FontFamily)FindResource("FontDisplay"),
            FontSize = 14,
            FontWeight = FontWeights.SemiBold,
            Foreground = new SolidColorBrush(accent),
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center
        };
        headerLeft.Children.Add(iconTile);
        headerLeft.Children.Add(new TextBlock
        {
            Text = usage.AgentName,
            FontFamily = (FontFamily)FindResource("FontDisplay"),
            FontSize = 14,
            FontWeight = FontWeights.SemiBold,
            VerticalAlignment = VerticalAlignment.Center
        });

        var chipColor = hasData ? GreenColor : TertiaryColor;
        var chip = new Border
        {
            Style = (Style)FindResource("StatusChip"),
            Background = AlphaBrush(chipColor, 20),
            BorderBrush = AlphaBrush(chipColor, 60),
            Margin = new Thickness(9, 0, 0, 0),
            Padding = new Thickness(6, 1, 6, 1)
        };
        chip.Child = new TextBlock
        {
            Text = Strings.Get(hasData ? "UsageSynced" : "UsageWaiting"),
            FontSize = 10,
            FontWeight = FontWeights.SemiBold,
            Foreground = new SolidColorBrush(chipColor)
        };
        headerLeft.Children.Add(chip);
        header.Children.Add(headerLeft);

        var updated = new TextBlock
        {
            Text = hasData
                ? string.Format(Strings.Get("UpdatedAt"), usage.LastUpdated!.Value.LocalDateTime.ToString("HH:mm"))
                : string.Empty,
            FontSize = 10.5,
            Foreground = new SolidColorBrush(TertiaryColor),
            VerticalAlignment = VerticalAlignment.Center,
            HorizontalAlignment = HorizontalAlignment.Right
        };
        Grid.SetColumn(updated, 1);
        header.Children.Add(updated);
        stack.Children.Add(header);

        var body = new Grid { Margin = new Thickness(0, 14, 0, 0) };
        body.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        body.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        body.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        body.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

        var five = BuildUsageRing(Strings.Get("FiveHourShort"), usage.FiveHourPercent,
            hasData, usage.FiveHourResetText);
        five.Margin = new Thickness(0, 0, 18, 0);
        Grid.SetColumn(five, 0);
        body.Children.Add(five);

        var seven = BuildUsageRing(Strings.Get("SevenDayShort"), usage.SevenDayPercent,
            hasData, usage.SevenDayResetText);
        seven.Margin = new Thickness(0, 0, 18, 0);
        Grid.SetColumn(seven, 1);
        body.Children.Add(seven);

        var divider = new Border
        {
            Width = 1,
            Background = new SolidColorBrush(ColorFromHex("#202632")),
            Margin = new Thickness(0, 4, 18, 4)
        };
        Grid.SetColumn(divider, 2);
        body.Children.Add(divider);

        var info = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        info.Children.Add(new TextBlock
        {
            Text = hasData ? Strings.Get("UsageSynced") : Strings.Get("UsageQuotaTitle"),
            FontFamily = (FontFamily)FindResource("FontDisplay"),
            FontSize = 12.5,
            FontWeight = FontWeights.SemiBold
        });
        info.Children.Add(new TextBlock
        {
            Text = hasData ? Strings.Get("UsageLocalCache") : Strings.Get("UsageNoDataHint"),
            FontSize = 11,
            Foreground = new SolidColorBrush(ColorFromHex("#8996A9")),
            TextWrapping = TextWrapping.Wrap,
            LineHeight = 16,
            MaxWidth = 280,
            Margin = new Thickness(0, 4, 0, 0)
        });
        if (hasData)
        {
            if (!string.IsNullOrWhiteSpace(usage.Note))
            {
                info.Children.Add(new TextBlock
                {
                    Text = usage.Note,
                    FontSize = 10.5,
                    Foreground = new SolidColorBrush(ColorFromHex("#8996A9")),
                    TextWrapping = TextWrapping.Wrap,
                    MaxWidth = 280,
                    Margin = new Thickness(0, 5, 0, 0)
                });
            }
            info.Children.Add(new TextBlock
            {
                Text = string.Format(Strings.Get("UpdatedAt"),
                    usage.LastUpdated!.Value.LocalDateTime.ToString("yyyy-MM-dd HH:mm")),
                FontSize = 10,
                Foreground = new SolidColorBrush(TertiaryColor),
                Margin = new Thickness(0, 8, 0, 0)
            });
        }
        Grid.SetColumn(info, 3);
        body.Children.Add(info);

        stack.Children.Add(body);
        card.Child = stack;
        return card;
    }

    private FrameworkElement BuildUsageRing(string label, double percent, bool hasData, string? resetText)
    {
        var stack = new StackPanel { HorizontalAlignment = HorizontalAlignment.Center };
        var ringGrid = new Grid { Width = 96, Height = 96 };

        var track = new Ellipse
        {
            Width = 84,
            Height = 84,
            Stroke = new SolidColorBrush(ColorFromHex("#1A1F29")),
            StrokeThickness = 7,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center
        };
        ringGrid.Children.Add(track);

        var clamped = Math.Clamp(percent, 0, 100);
        var color = clamped >= 85 ? DangerColor : clamped >= 60 ? AmberColor : GreenColor;
        if (hasData && clamped > 0.05)
        {
            var progress = new Path
            {
                Data = BuildRingGeometry(clamped, 48, 48, 42),
                Stroke = new SolidColorBrush(color),
                StrokeThickness = 7,
                StrokeStartLineCap = PenLineCap.Round,
                StrokeEndLineCap = PenLineCap.Round,
                Width = 96,
                Height = 96,
                Stretch = Stretch.None
            };
            ringGrid.Children.Add(progress);
        }

        var center = new StackPanel
        {
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center
        };
        center.Children.Add(new TextBlock
        {
            Text = hasData ? $"{clamped:0.#}%" : "—",
            FontFamily = (FontFamily)FindResource("FontDisplay"),
            FontSize = 19,
            FontWeight = FontWeights.SemiBold,
            HorizontalAlignment = HorizontalAlignment.Center,
            Foreground = new SolidColorBrush(hasData ? ColorFromHex("#F2F4F8") : TertiaryColor)
        });
        center.Children.Add(new TextBlock
        {
            Text = label,
            FontSize = 9,
            Foreground = new SolidColorBrush(TertiaryColor),
            HorizontalAlignment = HorizontalAlignment.Center,
            Margin = new Thickness(0, 2, 0, 0)
        });
        ringGrid.Children.Add(center);
        stack.Children.Add(ringGrid);

        var reset = ResetText(resetText);
        if (hasData && !string.IsNullOrWhiteSpace(reset))
        {
            stack.Children.Add(new TextBlock
            {
                Text = string.Format(Strings.Get("ResetsAt"), reset),
                FontSize = 9.5,
                Foreground = new SolidColorBrush(TertiaryColor),
                HorizontalAlignment = HorizontalAlignment.Center,
                TextAlignment = TextAlignment.Center,
                Margin = new Thickness(0, 1, 0, 0),
                MaxWidth = 124,
                TextWrapping = TextWrapping.Wrap
            });
        }
        return stack;
    }

    private static Geometry BuildRingGeometry(double percent, double cx, double cy, double radius)
    {
        var geometry = new PathGeometry();
        var figure = new PathFigure
        {
            StartPoint = new Point(cx, cy - radius),
            IsClosed = false,
            IsFilled = false
        };
        var pct = Math.Clamp(percent, 0, 100);
        if (pct >= 99.95)
        {
            figure.Segments.Add(new ArcSegment(new Point(cx, cy + radius), new Size(radius, radius),
                0, false, SweepDirection.Clockwise, true));
            figure.Segments.Add(new ArcSegment(new Point(cx, cy - radius), new Size(radius, radius),
                0, false, SweepDirection.Clockwise, true));
        }
        else
        {
            var angle = pct / 100 * Math.PI * 2;
            var end = new Point(cx + radius * Math.Sin(angle), cy - radius * Math.Cos(angle));
            figure.Segments.Add(new ArcSegment(end, new Size(radius, radius), 0,
                angle > Math.PI, SweepDirection.Clockwise, true));
        }
        geometry.Figures.Add(figure);
        return geometry;
    }

    private static string? ResetText(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return null;
        if (DateTimeOffset.TryParse(raw, CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var parsed))
        {
            return parsed.ToLocalTime().ToString("MM-dd HH:mm");
        }
        return raw.Length > 22 ? raw[..22] : raw;
    }

    // ================================================================
    // General page / about page actions
    // ================================================================

    private void PreviewSoundButton_Click(object sender, RoutedEventArgs e)
    {
        // Audition the sound that is currently selected in the combo box (not the saved one),
        // and do it even when notifications are toggled off so users can pick a scheme first.
        var selected = (SoundCombo.SelectedItem as ComboBoxItem)?.Tag?.ToString();
        _app.PlayNotificationSound(isUrgent: false, soundOverride: selected, ignoreEnabled: true);
    }

    private void UpstreamLinkButton_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = "https://github.com/Octane0411/open-vibe-island",
                UseShellExecute = true
            });
        }
        catch
        {
            // Opening the browser must never crash settings.
        }
    }

    private void CopyDataDirButton_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            Clipboard.SetText(BridgeConstants.AppDataDir);
            CopiedFeedbackText.Visibility = Visibility.Visible;
            CopiedFeedbackText.Opacity = 0;
            CopiedFeedbackText.BeginAnimation(OpacityProperty,
                new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(160)));
            _copiedTimer.Stop();
            _copiedTimer.Start();
        }
        catch
        {
            // Clipboard can be locked by another process; silently ignore.
        }
    }

    // ================================================================
    // Helpers
    // ================================================================

    private static Color ColorFromHex(string hex)
    {
        try
        {
            return (Color)ColorConverter.ConvertFromString(hex);
        }
        catch
        {
            return Colors.Gray;
        }
    }

    private static SolidColorBrush AlphaBrush(Color color, byte alpha) =>
        new(Color.FromArgb(alpha, color.R, color.G, color.B));
}
