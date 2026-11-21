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
using OpenIsland.Core.Updates;
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
    private bool _isManualUpdateChecking;
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

        _appVersion = _app.Updater?.CurrentVersion
            ?? UpdateService.GetCurrentAppVersion(Assembly.GetExecutingAssembly());

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
        UpdateAboutUpdateCard();

        _app.UpdateCheckCompleted += OnUpdateCheckCompleted;
        _app.AgentInstallStateChanged += OnAgentInstallStateChanged;

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
        _app.UpdateCheckCompleted -= OnUpdateCheckCompleted;
        _app.AgentInstallStateChanged -= OnAgentInstallStateChanged;
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
            case "about":
                UpdateAboutUpdateCard();
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
        ScanConnectAgentsText.Text = Strings.Get("ScanAndConnectAgents");
        ScanConnectAgentsButton.ToolTip = Strings.Get("ScanAndConnectAgents");
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
        AutoCheckUpdatesLabel.Text = Strings.Get("AutoCheckUpdates");
        AutoCheckUpdatesHintText.Text = Strings.Get("AutoCheckUpdatesHint");
        AutoDetectAgentsLabel.Text = Strings.Get("AutoDetectAgentsLabel");
        AutoDetectAgentsHintText.Text = Strings.Get("AutoDetectAgentsHint");
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
        AboutUpdateTitle.Text = Strings.Get("SoftwareUpdate");
        AboutOpenUpdateText.Text = Strings.Get("UpdateNow");
        WinRepoLabel.Text = Strings.Get("WinRepoLabel");
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
        WinRepoLinkButton.ToolTip = $"{Strings.Get("OpenRepoWindows")} ({UpdateService.RepositoryUrl})";
        UpstreamLinkButton.ToolTip = $"{Strings.Get("ViewSource")} (https://github.com/Octane0411/open-vibe-island)";
        CopyDataDirButton.ToolTip = Strings.Get("Copy");
        UpdateAboutUpdateCard();
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
        AutoCheckUpdatesToggle.IsChecked = s.AutoCheckForUpdates;
        AutoDetectAgentsToggle.IsChecked = s.AutoDetectAgentsOnStartup;
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
            AutoCheckForUpdates = AutoCheckUpdatesToggle.IsChecked == true,
            SkippedUpdateVersion = previous.SkippedUpdateVersion,
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
            AutoInstallOpenCodePlugin = previous.AutoInstallOpenCodePlugin,
            AutoDetectAgentsOnStartup = AutoDetectAgentsToggle.IsChecked == true,
            DismissedAutoConnectAgents = previous.DismissedAutoConnectAgents is null
                ? new List<string>()
                : new List<string>(previous.DismissedAutoConnectAgents)
        };

        string? applyError = null;
        try
        {
            _app.ApplySettings(updated);
        }
        catch (Exception ex)
        {
            applyError = ex.Message;
        }

        ApplyLocalizedLabels();
        LoadSettingsIntoControls();
        RefreshAgentRows();
        RefreshUsageCards();
        UpdateConnectionPage();
        UpdateAppearancePreview();
        UpdateAboutUpdateCard();

        if (applyError is null)
        {
            ShowSaveToast();
        }
        else
        {
            ShowSaveToast(applyError, ok: false);
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
        SaveToastIcon.Data = (Geometry)FindResource(ok ? "IconCheck" : "IconAlert");
        SaveToastIcon.Fill = new SolidColorBrush(ok ? GreenColor : DangerColor);
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

    private void ScanConnectAgentsButton_Click(object sender, RoutedEventArgs e)
    {
        if (!_app.PromptConnectDetectedAgents(includeDismissed: true))
        {
            ShowAgentsFeedback(Strings.Get("NoNewAgentsDetected"), ok: true);
        }
    }

    private void OnAgentInstallStateChanged()
    {
        if (!Dispatcher.CheckAccess())
        {
            Dispatcher.BeginInvoke(OnAgentInstallStateChanged);
            return;
        }
        RefreshAgentRows();
        RefreshUsageCards();
        UpdateConnectionPage();
    }

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

        // Surface installed, partial, and locally detected agents at the top of the list.
        statuses = statuses
            .OrderBy(s => s.Status == HookInstallStatus.Installed ? 0
                : s.Status == HookInstallStatus.Partial ? 1
                : s.IsDetectedOnMachine ? 2 : 3)
            .ToList();

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

        if (status.IsDetectedOnMachine && status.Status == HookInstallStatus.NotInstalled)
        {
            var detectedChip = new Border
            {
                Style = (Style)FindResource("StatusChip"),
                Background = AlphaBrush(AccentColor, 20),
                BorderBrush = AlphaBrush(AccentColor, 66),
                Margin = new Thickness(6, 0, 0, 0),
                Padding = new Thickness(6, 1, 6, 1),
                ToolTip = status.DetectionSource
            };
            detectedChip.Child = new TextBlock
            {
                Text = Strings.Get("DetectedOnPc"),
                FontSize = 10,
                FontWeight = FontWeights.SemiBold,
                Foreground = new SolidColorBrush(AccentColor),
                VerticalAlignment = VerticalAlignment.Center
            };
            headerRow.Children.Add(detectedChip);
        }

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
            RefreshUsageCards();
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
            usages = TranscriptDiscovery.ReadUsageWindows(_app.SetupManager);
        }
        catch
        {
            usages = new List<UsageWindowInfo>();
        }

        var hasAnyConnectedData = usages.Any(u => u.IsConnected && (u.HasQuotaData || u.HasTokenData || u.LastUpdated.HasValue));
        UsageHintBanner.Visibility = hasAnyConnectedData ? Visibility.Collapsed : Visibility.Visible;

        foreach (var usage in usages
                     .OrderByDescending(u => u.IsConnected)
                     .ThenByDescending(u => u.HasQuotaData || u.HasTokenData)
                     .ThenByDescending(u => u.LastUpdated ?? DateTimeOffset.MinValue))
        {
            UsageCardsHost.Children.Add(BuildUsageCard(usage));
        }
    }

    private static (Color Accent, string Monogram) ResolveUsageVisuals(UsageWindowInfo usage, Color defaultAccent)
    {
        var source = usage.Source;
        if (source == AgentSource.Unknown)
        {
            if (usage.AgentName.Contains("Claude", StringComparison.OrdinalIgnoreCase))
                source = AgentSource.Claude;
            else if (usage.AgentName.Contains("Codex", StringComparison.OrdinalIgnoreCase))
                source = AgentSource.Codex;
            else if (usage.AgentName.Contains("Antigravity", StringComparison.OrdinalIgnoreCase))
                source = AgentSource.Antigravity;
            else if (usage.AgentName.Contains("DeepSeek", StringComparison.OrdinalIgnoreCase))
                source = AgentSource.DeepSeekHarness;
        }

        return source switch
        {
            AgentSource.Claude => (ColorFromHex(AgentRegistry.Get(AgentSource.Claude).AccentHex), "C"),
            AgentSource.Codex => (ColorFromHex(AgentRegistry.Get(AgentSource.Codex).AccentHex), "X"),
            AgentSource.Antigravity => (ColorFromHex(AgentRegistry.Get(AgentSource.Antigravity).AccentHex), "A"),
            AgentSource.DeepSeekHarness => (ColorFromHex(AgentRegistry.Get(AgentSource.DeepSeekHarness).AccentHex), "D"),
            _ => (defaultAccent, "U")
        };
    }

    private FrameworkElement BuildUsageCard(UsageWindowInfo usage)
    {
        var (accent, monogram) = ResolveUsageVisuals(usage, AccentColor);
        var hasUpdatedTime = usage.LastUpdated.HasValue;

        var card = new Border
        {
            Style = (Style)FindResource("Card"),
            Margin = new Thickness(0, 0, 0, 12),
            Padding = new Thickness(18),
            Tag = usage.Source.ToString()
        };
        var stack = new StackPanel();

        // ---- Card Header ----
        var header = new Grid();
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var headerLeft = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        var iconTile = new Border
        {
            Width = 34,
            Height = 34,
            CornerRadius = new CornerRadius(9),
            Background = AlphaBrush(accent, (byte)(usage.IsConnected ? 30 : 18)),
            BorderBrush = AlphaBrush(accent, (byte)(usage.IsConnected ? 62 : 36)),
            BorderThickness = new Thickness(1),
            Margin = new Thickness(0, 0, 11, 0)
        };
        iconTile.Child = new TextBlock
        {
            Text = monogram,
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
            Foreground = (Brush)FindResource("BrushTextPrimary"),
            VerticalAlignment = VerticalAlignment.Center
        });

        var chipColor = usage.IsConnected ? GreenColor : TertiaryColor;
        var chip = new Border
        {
            Style = (Style)FindResource("StatusChip"),
            Background = AlphaBrush(chipColor, 20),
            BorderBrush = AlphaBrush(chipColor, 64),
            Margin = new Thickness(9, 0, 0, 0),
            Padding = new Thickness(7, 1.5, 7, 1.5)
        };
        var chipRow = new StackPanel { Orientation = Orientation.Horizontal };
        chipRow.Children.Add(new Ellipse
        {
            Width = 5,
            Height = 5,
            Fill = new SolidColorBrush(chipColor),
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 0, 5, 0)
        });
        chipRow.Children.Add(new TextBlock
        {
            Text = Strings.Get(usage.IsConnected ? "UsageConnectedBadge" : "UsageNotConnectedBadge"),
            FontSize = 10,
            FontWeight = FontWeights.SemiBold,
            Foreground = new SolidColorBrush(chipColor),
            VerticalAlignment = VerticalAlignment.Center
        });
        chip.Child = chipRow;
        headerLeft.Children.Add(chip);
        header.Children.Add(headerLeft);

        var updated = new TextBlock
        {
            Text = !usage.IsConnected
                ? Strings.Get("UsageNotConnectedBadge")
                : hasUpdatedTime
                    ? string.Format(Strings.Get("UpdatedAt"), usage.LastUpdated!.Value.LocalDateTime.ToString("HH:mm"))
                    : Strings.Get("UsageWaiting"),
            FontSize = 10.5,
            Foreground = new SolidColorBrush(TertiaryColor),
            VerticalAlignment = VerticalAlignment.Center,
            HorizontalAlignment = HorizontalAlignment.Right
        };
        Grid.SetColumn(updated, 1);
        header.Children.Add(updated);
        stack.Children.Add(header);

        // ---- Unconnected State ----
        if (!usage.IsConnected)
        {
            stack.Children.Add(BuildUnconnectedUsageBody(usage));
            card.Child = stack;
            return card;
        }

        // ---- Connected State (Unified Sections) ----
        stack.Children.Add(BuildConnectedUsageBody(usage, accent));
        card.Child = stack;
        return card;
    }

    private FrameworkElement BuildUnconnectedUsageBody(UsageWindowInfo usage)
    {
        var tile = new Border
        {
            Style = (Style)FindResource("InnerTile"),
            Margin = new Thickness(0, 12, 0, 0),
            Padding = new Thickness(14, 11, 14, 11)
        };

        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var descKey = usage.Source == AgentSource.DeepSeekHarness
            ? "UsageNotConnectedDshDesc"
            : "UsageNotConnectedDesc";

        var descText = new TextBlock
        {
            Text = Strings.Get(descKey),
            FontSize = 11,
            Foreground = new SolidColorBrush(ColorFromHex("#8996A9")),
            TextWrapping = TextWrapping.Wrap,
            LineHeight = 16,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 0, 12, 0)
        };
        Grid.SetColumn(descText, 0);
        grid.Children.Add(descText);

        var connectBtn = MakeSmallButton(Strings.Get("UsageGoToConnect"), "GhostButton");
        connectBtn.Margin = new Thickness(0);
        connectBtn.VerticalAlignment = VerticalAlignment.Center;
        connectBtn.Click += (_, _) => SelectPage("agents", animate: true);
        Grid.SetColumn(connectBtn, 1);
        grid.Children.Add(connectBtn);

        tile.Child = grid;
        return tile;
    }

    private FrameworkElement BuildConnectedUsageBody(UsageWindowInfo usage, Color accent)
    {
        var outer = new StackPanel { Margin = new Thickness(0, 12, 0, 0) };

        // 1. Remaining Quota section (Claude Code, Codex, Google Antigravity; omitted for DeepSeek Harness)
        if (usage.SupportsRemainingQuota)
        {
            outer.Children.Add(BuildUsageSectionHeader(Strings.Get("UsageSectionRemainingQuota"), accent, topMargin: 0));
            outer.Children.Add(BuildRemainingQuotaSection(usage));
        }

        // 2. Used Model Tokens section (all connected platforms)
        outer.Children.Add(BuildUsageSectionHeader(
            Strings.Get("UsageSectionUsedTokens"),
            accent,
            topMargin: usage.SupportsRemainingQuota ? 13 : 0));

        var tokenGrid = new Grid();
        tokenGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        tokenGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

        var fiveTile = BuildTokenWindowTile(Strings.Get("FiveHourShort"), usage.FiveHourTokens, showValues: true, accent);
        fiveTile.Margin = new Thickness(0, 0, 6, 0);
        Grid.SetColumn(fiveTile, 0);
        tokenGrid.Children.Add(fiveTile);

        var sevenTile = BuildTokenWindowTile(Strings.Get("SevenDayShort"), usage.SevenDayTokens, showValues: true, accent);
        sevenTile.Margin = new Thickness(6, 0, 0, 0);
        Grid.SetColumn(sevenTile, 1);
        tokenGrid.Children.Add(sevenTile);

        outer.Children.Add(tokenGrid);

        // 3. 7-Day Model Usage Distribution section (all connected platforms)
        outer.Children.Add(BuildUsageSectionHeader(Strings.Get("UsageSectionModelDistribution"), accent, topMargin: 13));
        outer.Children.Add(BuildModelDistributionSection(usage, accent));

        return outer;
    }

    private FrameworkElement BuildUsageSectionHeader(string title, Color accent, double topMargin)
    {
        var row = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Margin = new Thickness(0, topMargin, 0, 7)
        };
        row.Children.Add(new Border
        {
            Width = 3,
            Height = 11,
            CornerRadius = new CornerRadius(1.5),
            Background = new SolidColorBrush(accent),
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 0, 7, 0)
        });
        row.Children.Add(new TextBlock
        {
            Text = title,
            FontSize = 11,
            FontWeight = FontWeights.SemiBold,
            Foreground = new SolidColorBrush(ColorFromHex("#B5C0D0")),
            VerticalAlignment = VerticalAlignment.Center
        });
        return row;
    }

    private FrameworkElement BuildRemainingQuotaSection(UsageWindowInfo usage)
    {
        var tile = new Border
        {
            Style = (Style)FindResource("InnerTile"),
            Padding = new Thickness(14, 11, 14, 11)
        };

        var body = new Grid();
        body.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        body.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        body.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        body.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

        var five = BuildRemainingQuotaRing(
            Strings.Get("UsageFiveHourRemaining"),
            usage.FiveHourRemainingPercent,
            usage.FiveHourPercent,
            usage.HasQuotaData,
            usage.FiveHourResetText);
        five.Margin = new Thickness(0, 0, 16, 0);
        Grid.SetColumn(five, 0);
        body.Children.Add(five);

        var seven = BuildRemainingQuotaRing(
            Strings.Get("UsageSevenDayRemaining"),
            usage.SevenDayRemainingPercent,
            usage.SevenDayPercent,
            usage.HasQuotaData,
            usage.SevenDayResetText);
        seven.Margin = new Thickness(0, 0, 16, 0);
        Grid.SetColumn(seven, 1);
        body.Children.Add(seven);

        var divider = new Border
        {
            Width = 1,
            Background = new SolidColorBrush(ColorFromHex("#202632")),
            Margin = new Thickness(0, 4, 16, 4)
        };
        Grid.SetColumn(divider, 2);
        body.Children.Add(divider);

        var info = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        var quotaTitle = usage.HasQuotaData
            ? Strings.Get("UsageQuotaSyncedTitle")
            : usage.Source == AgentSource.Antigravity
                ? Strings.Get("UsageQuotaCloudTitle")
                : Strings.Get("UsageQuotaWaitingTitle");

        var quotaDesc = usage.HasQuotaData
            ? Strings.Get("UsageQuotaSyncedDesc",
                usage.FiveHourPercent,
                usage.FiveHourRemainingPercent,
                usage.SevenDayPercent,
                usage.SevenDayRemainingPercent)
            : (!string.IsNullOrWhiteSpace(usage.Note) ? usage.Note : Strings.Get("UsageNoDataHint"));

        info.Children.Add(new TextBlock
        {
            Text = quotaTitle,
            FontFamily = (FontFamily)FindResource("FontDisplay"),
            FontSize = 12,
            FontWeight = FontWeights.SemiBold,
            Foreground = new SolidColorBrush(ColorFromHex("#E6ECF5"))
        });
        info.Children.Add(new TextBlock
        {
            Text = quotaDesc,
            FontSize = 10.5,
            Foreground = new SolidColorBrush(ColorFromHex("#8996A9")),
            TextWrapping = TextWrapping.Wrap,
            LineHeight = 15.5,
            MaxWidth = 310,
            HorizontalAlignment = HorizontalAlignment.Left,
            Margin = new Thickness(0, 4, 0, 0)
        });
        Grid.SetColumn(info, 3);
        body.Children.Add(info);

        tile.Child = body;
        return tile;
    }

    private FrameworkElement BuildModelDistributionSection(UsageWindowInfo usage, Color accent)
    {
        var tile = new Border
        {
            Style = (Style)FindResource("InnerTile"),
            Padding = new Thickness(13, 10, 13, 10)
        };

        if (usage.TopModels.Count == 0)
        {
            tile.Child = new TextBlock
            {
                Text = Strings.Get("UsageNoModelCalls7d"),
                FontSize = 10.5,
                Foreground = new SolidColorBrush(TertiaryColor)
            };
            return tile;
        }

        var stack = new StackPanel();
        long totalModelTokens = Math.Max(1, usage.TopModels.Sum(m => Math.Max(0, m.SevenDayTokens)));

        for (int i = 0; i < usage.TopModels.Count; i++)
        {
            var m = usage.TopModels[i];
            double sharePct = Math.Clamp(m.SevenDayTokens * 100.0 / totalModelTokens, 0.0, 100.0);

            var itemStack = new StackPanel
            {
                Margin = new Thickness(0, i == 0 ? 0 : 8, 0, 0)
            };

            var row = new Grid();
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            var leftRow = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
            leftRow.Children.Add(new Ellipse
            {
                Width = 6,
                Height = 6,
                Fill = new SolidColorBrush(accent),
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(0, 0, 7, 0)
            });
            leftRow.Children.Add(new TextBlock
            {
                Text = m.Model,
                FontFamily = (FontFamily)FindResource("FontMono"),
                FontSize = 10.5,
                FontWeight = FontWeights.SemiBold,
                Foreground = new SolidColorBrush(ColorFromHex("#D5DEEC")),
                VerticalAlignment = VerticalAlignment.Center
            });
            leftRow.Children.Add(new TextBlock
            {
                Text = $"  ·  {Strings.Get("UsageRequestsFormat", m.RequestCount)}",
                FontSize = 10,
                Foreground = new SolidColorBrush(TertiaryColor),
                VerticalAlignment = VerticalAlignment.Center
            });
            Grid.SetColumn(leftRow, 0);
            row.Children.Add(leftRow);

            var rightRow = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
            rightRow.Children.Add(new TextBlock
            {
                Text = FormatTokenCount(m.SevenDayTokens),
                FontFamily = (FontFamily)FindResource("FontMono"),
                FontSize = 10.5,
                FontWeight = FontWeights.SemiBold,
                Foreground = new SolidColorBrush(ColorFromHex("#F2F4F8")),
                VerticalAlignment = VerticalAlignment.Center
            });
            rightRow.Children.Add(new TextBlock
            {
                Text = $"  ({sharePct:0.#}%)",
                FontFamily = (FontFamily)FindResource("FontMono"),
                FontSize = 10,
                FontWeight = FontWeights.SemiBold,
                Foreground = new SolidColorBrush(accent),
                VerticalAlignment = VerticalAlignment.Center
            });
            Grid.SetColumn(rightRow, 1);
            row.Children.Add(rightRow);

            itemStack.Children.Add(row);

            // Proportional horizontal distribution bar
            var barTrack = new Border
            {
                Height = 4,
                CornerRadius = new CornerRadius(2),
                Background = new SolidColorBrush(ColorFromHex("#161B24")),
                Margin = new Thickness(0, 4, 0, 0),
                ClipToBounds = true
            };
            var barGrid = new Grid();
            double fillWeight = Math.Max(1.5, sharePct);
            double emptyWeight = Math.Max(0.0, 100.0 - fillWeight);
            barGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(fillWeight, GridUnitType.Star) });
            barGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(emptyWeight, GridUnitType.Star) });

            var barFill = new Border
            {
                CornerRadius = new CornerRadius(2),
                Background = new SolidColorBrush(accent)
            };
            Grid.SetColumn(barFill, 0);
            barGrid.Children.Add(barFill);
            barTrack.Child = barGrid;

            itemStack.Children.Add(barTrack);
            stack.Children.Add(itemStack);
        }

        tile.Child = stack;
        return tile;
    }

    private FrameworkElement BuildTokenWindowTile(string label, UsageTokenWindow window, bool showValues, Color accent)
    {
        var tile = new Border
        {
            Style = (Style)FindResource("InnerTile"),
            Padding = new Thickness(13, 10, 13, 10)
        };
        var stack = new StackPanel();

        var topRow = new Grid();
        topRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        topRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        topRow.Children.Add(new TextBlock
        {
            Text = label,
            FontSize = 11,
            FontWeight = FontWeights.SemiBold,
            Foreground = new SolidColorBrush(ColorFromHex("#A6B0C0")),
            VerticalAlignment = VerticalAlignment.Center
        });
        var reqText = new TextBlock
        {
            Text = showValues ? Strings.Get("UsageRequestsFormat", window.RequestCount) : "—",
            FontSize = 10,
            Foreground = new SolidColorBrush(TertiaryColor),
            VerticalAlignment = VerticalAlignment.Center
        };
        Grid.SetColumn(reqText, 1);
        topRow.Children.Add(reqText);
        stack.Children.Add(topRow);

        stack.Children.Add(new TextBlock
        {
            Text = showValues ? FormatTokenCount(window.TotalTokens) : "—",
            FontFamily = (FontFamily)FindResource("FontDisplay"),
            FontSize = 20,
            FontWeight = FontWeights.SemiBold,
            Foreground = new SolidColorBrush(showValues ? ColorFromHex("#F2F4F8") : TertiaryColor),
            Margin = new Thickness(0, 3, 0, 4)
        });

        var breakdown = showValues
            ? $"{Strings.Get("UsageInputLabel")} {FormatTokenCount(window.InputTokens)}  ·  {Strings.Get("UsageCacheLabel")} {FormatTokenCount(window.CacheReadTokens)}  ·  {Strings.Get("UsageOutputLabel")} {FormatTokenCount(window.OutputTokens)}"
            : $"{Strings.Get("UsageInputLabel")} —  ·  {Strings.Get("UsageCacheLabel")} —  ·  {Strings.Get("UsageOutputLabel")} —";
        stack.Children.Add(new TextBlock
        {
            Text = breakdown,
            FontFamily = (FontFamily)FindResource("FontMono"),
            FontSize = 9.5,
            Foreground = new SolidColorBrush(showValues ? ColorFromHex("#8D9BB0") : TertiaryColor),
            TextTrimming = TextTrimming.CharacterEllipsis,
            ToolTip = breakdown
        });

        tile.Child = stack;
        return tile;
    }

    private static string FormatTokenCount(long tokens)
    {
        if (tokens >= 1_000_000_000) return $"{tokens / 1_000_000_000.0:0.##}B";
        if (tokens >= 1_000_000) return $"{tokens / 1_000_000.0:0.##}M";
        if (tokens >= 1_000) return $"{tokens / 1_000.0:0.#}K";
        return tokens.ToString(CultureInfo.InvariantCulture);
    }

    private FrameworkElement BuildRemainingQuotaRing(
        string label,
        double remainingPercent,
        double usedPercent,
        bool hasData,
        string? resetText)
    {
        var stack = new StackPanel { HorizontalAlignment = HorizontalAlignment.Center };
        var ringGrid = new Grid { Width = 88, Height = 88 };

        var track = new Ellipse
        {
            Width = 76,
            Height = 76,
            Stroke = new SolidColorBrush(ColorFromHex("#1A1F29")),
            StrokeThickness = 6.5,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center
        };
        ringGrid.Children.Add(track);

        var clampedRemaining = Math.Clamp(remainingPercent, 0, 100);
        var color = clampedRemaining <= 15 ? DangerColor : clampedRemaining <= 40 ? AmberColor : GreenColor;
        if (hasData && clampedRemaining > 0.05)
        {
            var progress = new Path
            {
                Data = BuildRingGeometry(clampedRemaining, 44, 44, 38),
                Stroke = new SolidColorBrush(color),
                StrokeThickness = 6.5,
                StrokeStartLineCap = PenLineCap.Round,
                StrokeEndLineCap = PenLineCap.Round,
                Width = 88,
                Height = 88,
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
            Text = hasData ? $"{clampedRemaining:0.#}%" : "—",
            FontFamily = (FontFamily)FindResource("FontDisplay"),
            FontSize = 17,
            FontWeight = FontWeights.SemiBold,
            HorizontalAlignment = HorizontalAlignment.Center,
            Foreground = new SolidColorBrush(hasData ? ColorFromHex("#F2F4F8") : TertiaryColor)
        });
        center.Children.Add(new TextBlock
        {
            Text = label,
            FontSize = 8.5,
            Foreground = new SolidColorBrush(TertiaryColor),
            HorizontalAlignment = HorizontalAlignment.Center,
            Margin = new Thickness(0, 1.5, 0, 0)
        });
        ringGrid.Children.Add(center);
        stack.Children.Add(ringGrid);

        var reset = ResetText(resetText);
        if (hasData)
        {
            var subText = !string.IsNullOrWhiteSpace(reset)
                ? string.Format(Strings.Get("ResetsAt"), reset)
                : Strings.Get("UsageUsedPercentFormat", Math.Clamp(usedPercent, 0, 100));
            stack.Children.Add(new TextBlock
            {
                Text = subText,
                FontSize = 9,
                Foreground = new SolidColorBrush(TertiaryColor),
                HorizontalAlignment = HorizontalAlignment.Center,
                TextAlignment = TextAlignment.Center,
                Margin = new Thickness(0, 1, 0, 0),
                MaxWidth = 118,
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

    private void OnUpdateCheckCompleted(UpdateCheckResult _)
    {
        Dispatcher.BeginInvoke(new Action(UpdateAboutUpdateCard));
    }

    private void UpdateAboutUpdateCard()
    {
        if (AboutUpdateStatusChip is null || AboutUpdateDetailText is null) return;

        var mode = _app.LastUpdateCheck?.InstallMode
            ?? _app.Updater?.InstallMode
            ?? UpdateService.DetectInstallMode();
        var modeLabel = FormatInstallMode(mode);

        if (_isManualUpdateChecking || _app.IsCheckingForUpdates)
        {
            SetAboutUpdateChip(Strings.Get("UpdateChecking"), AccentColor);
            AboutUpdateDetailText.Text = $"{Strings.Get("CurrentVersionLabel")} v{_appVersion}  ·  {modeLabel}";
            AboutOpenUpdateButton.Visibility = Visibility.Collapsed;
            CheckUpdatesButton.IsEnabled = false;
            CheckUpdatesButtonText.Text = Strings.Get("UpdateChecking");
            return;
        }

        CheckUpdatesButton.IsEnabled = true;
        CheckUpdatesButtonText.Text = Strings.Get("CheckForUpdates");

        var last = _app.LastUpdateCheck;
        if (last is { IsUpdateAvailable: true, Release: not null })
        {
            SetAboutUpdateChip(Strings.Get("UpdateAvailableSummary", last.Release.Version), AccentColor);
            AboutUpdateDetailText.Text =
                $"{Strings.Get("UpdateAvailableSubtitle", _appVersion, last.Release.Version)}  ·  {modeLabel}";
            AboutOpenUpdateButton.Visibility = Visibility.Visible;
            AboutOpenUpdateText.Text = Strings.Get("UpdateNow");
        }
        else if (last is { Status: UpdateCheckStatus.UpToDate })
        {
            SetAboutUpdateChip(Strings.Get("UpdateUpToDate"), GreenColor);
            AboutUpdateDetailText.Text =
                $"{Strings.Get("UpdateUpToDateMessage", _appVersion)}  ·  {modeLabel}";
            AboutOpenUpdateButton.Visibility = Visibility.Collapsed;
        }
        else if (last is { Status: UpdateCheckStatus.Failed })
        {
            SetAboutUpdateChip(Strings.Get("UpdateCheckFailed"), DangerColor);
            AboutUpdateDetailText.Text = string.IsNullOrWhiteSpace(last.ErrorMessage)
                ? Strings.Get("UpdateCheckFailed")
                : last.ErrorMessage;
            AboutOpenUpdateButton.Visibility = Visibility.Collapsed;
        }
        else
        {
            SetAboutUpdateChip("v" + _appVersion, TertiaryColor);
            AboutUpdateDetailText.Text = $"{Strings.Get("CurrentVersionLabel")} v{_appVersion}  ·  {modeLabel}";
            AboutOpenUpdateButton.Visibility = Visibility.Collapsed;
        }
    }

    private void SetAboutUpdateChip(string text, Color color)
    {
        AboutUpdateStatusChipText.Text = text;
        AboutUpdateStatusChipText.Foreground = new SolidColorBrush(color);
        AboutUpdateStatusDot.Fill = new SolidColorBrush(color);
        AboutUpdateStatusChip.Background = AlphaBrush(color, 20);
        AboutUpdateStatusChip.BorderBrush = AlphaBrush(color, 68);
    }

    private static string FormatInstallMode(UpdateInstallMode mode) => mode switch
    {
        UpdateInstallMode.Installed => Strings.Get("InstallModeInstalled"),
        UpdateInstallMode.Portable => Strings.Get("InstallModePortable"),
        _ => Strings.Get("InstallModeDev")
    };

    private async void CheckUpdatesButton_Click(object sender, RoutedEventArgs e)
    {
        if (_isManualUpdateChecking) return;
        _isManualUpdateChecking = true;
        UpdateAboutUpdateCard();
        try
        {
            await _app.CheckForUpdatesManualAsync(showNoUpdateDialog: false);
        }
        catch (OperationCanceledException)
        {
            // App shutting down during check.
        }
        catch
        {
            // UpdateService already converts errors to UpdateCheckResult.Failed.
        }
        finally
        {
            _isManualUpdateChecking = false;
            if (IsLoaded)
            {
                UpdateAboutUpdateCard();
            }
        }
    }

    private void AboutOpenUpdateButton_Click(object sender, RoutedEventArgs e)
    {
        if (_app.LastUpdateCheck is { IsUpdateAvailable: true, Release: not null } last)
        {
            _app.ShowUpdateWindow(last.Release);
        }
    }

    private void WinRepoLinkButton_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = UpdateService.RepositoryUrl,
                UseShellExecute = true
            });
        }
        catch
        {
            // Opening the browser must never crash settings.
        }
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
