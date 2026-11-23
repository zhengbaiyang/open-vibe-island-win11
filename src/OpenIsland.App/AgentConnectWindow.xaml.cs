using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using OpenIsland.App.Controls;
using OpenIsland.App.Themes;
using OpenIsland.Core.Localization;
using OpenIsland.Core.Setup;

namespace OpenIsland.App;

public partial class AgentConnectWindow : Window
{
    private readonly App _app;
    private List<AgentInstallState> _candidates = new();
    private readonly List<(AgentInstallState State, CheckBox Toggle)> _rows = new();
    private bool _completed;
    private bool _isClosed;
    private bool _lastResultAllOk = true;

    public AgentConnectWindow(App app, IReadOnlyList<AgentInstallState> candidates)
    {
        InitializeComponent();
        _app = app;
        SettingsThemeManager.ThemeChanged += OnSettingsThemeChanged;
        Closed += (_, _) =>
        {
            _isClosed = true;
            SettingsThemeManager.ThemeChanged -= OnSettingsThemeChanged;
        };
        LoadCandidates(candidates);
    }

    private void OnSettingsThemeChanged(bool _)
    {
        if (!Dispatcher.CheckAccess())
        {
            Dispatcher.BeginInvoke(new Action(() => OnSettingsThemeChanged(_)));
            return;
        }
        if (_isClosed) return;

        // Preserve toggle checked states while rebuilding dynamically styled candidate rows.
        var checkedByKey = _rows.ToDictionary(
            r => r.State.Agent.CliKey,
            r => r.Toggle.IsChecked == true,
            StringComparer.OrdinalIgnoreCase);

        _rows.Clear();
        CandidatesHost.Children.Clear();
        foreach (var candidate in _candidates)
        {
            var isChecked = !checkedByKey.TryGetValue(candidate.Agent.CliKey, out var prev) || prev;
            var (card, toggle) = BuildCandidateRow(candidate, isChecked, !_completed);
            _rows.Add((candidate, toggle));
            CandidatesHost.Children.Add(card);
        }

        if (_completed && ResultBanner.Visibility == Visibility.Visible)
        {
            ApplyResultBannerTheme(_lastResultAllOk);
        }

        UpdateConfirmButton();
    }

    public void LoadCandidates(IReadOnlyList<AgentInstallState> candidates)
    {
        _completed = false;
        _candidates = candidates.ToList();
        _rows.Clear();
        CandidatesHost.Children.Clear();

        Title = Strings.Get("AgentConnectWindowTitle");
        TitleBarText.Text = Strings.Get("AgentConnectWindowTitle");
        CloseButton.ToolTip = Strings.Get("Close");
        HeaderTitleText.Text = Strings.Get("AgentConnectTitle");
        HeaderSubtitleText.Text = Strings.Get("AgentConnectSubtitle", _candidates.Count);
        DontAskButton.Content = Strings.Get("AgentConnectDontAsk");
        LaterButton.Content = Strings.Get("AgentConnectLater");
        DoneButton.Content = Strings.Get("AgentConnectDone");

        ResultBanner.Visibility = Visibility.Collapsed;
        DontAskButton.Visibility = Visibility.Visible;
        LaterButton.Visibility = Visibility.Visible;
        ConfirmButton.Visibility = Visibility.Visible;
        DoneButton.Visibility = Visibility.Collapsed;

        foreach (var candidate in _candidates)
        {
            var (card, toggle) = BuildCandidateRow(candidate, isChecked: true, isEnabled: true);
            _rows.Add((candidate, toggle));
            CandidatesHost.Children.Add(card);
        }

        UpdateConfirmButton();
    }

    private (Border Card, CheckBox Toggle) BuildCandidateRow(AgentInstallState state, bool isChecked, bool isEnabled)
    {
        var agent = state.Agent;

        var card = new Border
        {
            Style = (Style)FindResource("AgentCard"),
            Margin = new Thickness(0, 0, 0, 8),
            Padding = new Thickness(12, 10, 12, 10)
        };

        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var iconTile = AgentIconView.Create(
            agent.Source,
            size: 34,
            isDark: SettingsThemeManager.IsDarkActive);
        iconTile.Margin = new Thickness(0, 0, 12, 0);
        Grid.SetColumn(iconTile, 0);
        grid.Children.Add(iconTile);

        var infoStack = new StackPanel { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 12, 0) };
        var headerRow = new StackPanel { Orientation = Orientation.Horizontal };
        headerRow.Children.Add(new TextBlock
        {
            Text = agent.DisplayName,
            FontSize = 13,
            FontWeight = FontWeights.SemiBold,
            Foreground = SettingsThemeManager.TextPrimaryBrush,
            VerticalAlignment = VerticalAlignment.Center
        });

        var isPartial = state.Status == HookInstallStatus.Partial;
        var chipColor = isPartial ? SettingsThemeManager.AmberColor : SettingsThemeManager.AccentColor;
        var chipLabel = isPartial ? Strings.Get("Partial") : Strings.Get("DetectedOnPc");
        var chip = new Border
        {
            Style = (Style)FindResource("StatusChip"),
            Background = SettingsThemeManager.AlphaBrush(chipColor, darkAlpha: 22, lightAlpha: 24),
            BorderBrush = SettingsThemeManager.AlphaBrush(chipColor, darkAlpha: 68, lightAlpha: 72),
            Margin = new Thickness(8, 0, 0, 0),
            Padding = new Thickness(6, 1, 6, 1)
        };
        chip.Child = new TextBlock
        {
            Text = chipLabel,
            FontSize = 10,
            FontWeight = FontWeights.SemiBold,
            Foreground = new SolidColorBrush(chipColor)
        };
        headerRow.Children.Add(chip);
        infoStack.Children.Add(headerRow);

        if (!string.IsNullOrWhiteSpace(state.DetectionSource))
        {
            infoStack.Children.Add(new TextBlock
            {
                Text = Strings.Get("AgentConnectDetectedVia", state.DetectionSource),
                FontFamily = (FontFamily)FindResource("FontMono"),
                FontSize = 10,
                Foreground = SettingsThemeManager.MonoTextBrush,
                Margin = new Thickness(0, 3, 0, 0),
                TextTrimming = TextTrimming.CharacterEllipsis,
                ToolTip = state.DetectionSource
            });
        }

        infoStack.Children.Add(new TextBlock
        {
            Text = Strings.Get("AgentConnectTargetCfg", state.ConfigPath),
            FontFamily = (FontFamily)FindResource("FontMono"),
            FontSize = 10,
            Foreground = SettingsThemeManager.TextTertiaryBrush,
            Margin = new Thickness(0, 2, 0, 0),
            TextTrimming = TextTrimming.CharacterEllipsis,
            ToolTip = state.ConfigPath
        });

        Grid.SetColumn(infoStack, 1);
        grid.Children.Add(infoStack);

        var toggle = new CheckBox
        {
            Style = (Style)FindResource("ToggleSwitch"),
            IsChecked = isChecked,
            IsEnabled = isEnabled,
            VerticalAlignment = VerticalAlignment.Center
        };
        toggle.Checked += (_, _) => UpdateConfirmButton();
        toggle.Unchecked += (_, _) => UpdateConfirmButton();
        Grid.SetColumn(toggle, 2);
        grid.Children.Add(toggle);

        card.Child = grid;
        return (card, toggle);
    }

    private void UpdateConfirmButton()
    {
        var selected = _rows.Count(r => r.Toggle.IsChecked == true);
        ConfirmButton.Content = Strings.Get("AgentConnectConfirm", selected);
        ConfirmButton.IsEnabled = selected > 0 && !_completed;
    }

    private void ConfirmButton_Click(object sender, RoutedEventArgs e)
    {
        if (_completed) return;
        var selected = _rows.Where(r => r.Toggle.IsChecked == true).Select(r => r.State).ToList();
        if (selected.Count == 0) return;

        int succeeded = 0;
        var failures = new List<string>();
        foreach (var item in selected)
        {
            var (ok, message) = _app.SetupManager.Install(item.Agent.Source);
            if (ok) succeeded++;
            else failures.Add($"{item.Agent.DisplayName}: {message}");
        }

        // Any agents the user explicitly unchecked stay untouched; clear dismissed flags for installed ones.
        _app.ClearDismissedAutoConnectAgents(selected.Select(s => s.Agent.CliKey));
        _app.NotifyAgentInstallStateChanged();

        _completed = true;
        foreach (var (_, toggle) in _rows)
        {
            toggle.IsEnabled = false;
        }

        bool allOk = failures.Count == 0;
        _lastResultAllOk = allOk;
        ResultBanner.Visibility = Visibility.Visible;
        ApplyResultBannerTheme(allOk);
        ResultTitleText.Text = allOk
            ? Strings.Get("AgentConnectSuccess", succeeded)
            : Strings.Get("AgentConnectPartialFail", succeeded, failures.Count, string.Join("; ", failures));
        ResultHintText.Text = Strings.Get("AgentConnectRestartNote");

        DontAskButton.Visibility = Visibility.Collapsed;
        LaterButton.Visibility = Visibility.Collapsed;
        ConfirmButton.Visibility = Visibility.Collapsed;
        DoneButton.Visibility = Visibility.Visible;
    }

    private void ApplyResultBannerTheme(bool allOk)
    {
        ResultBanner.Background = allOk
            ? SettingsThemeManager.SuccessBannerBgBrush
            : SettingsThemeManager.DangerBannerBgBrush;
        ResultBanner.BorderBrush = allOk
            ? SettingsThemeManager.SuccessBannerBorderBrush
            : SettingsThemeManager.DangerBannerBorderBrush;
        ResultIcon.Data = (Geometry)FindResource(allOk ? "IconCheck" : "IconAlert");
        ResultIcon.Fill = allOk ? SettingsThemeManager.GreenBrush : SettingsThemeManager.DangerBrush;
        ResultTitleText.Foreground = allOk
            ? SettingsThemeManager.SuccessBannerTextBrush
            : SettingsThemeManager.DangerBannerTextBrush;
    }

    private void DontAskButton_Click(object sender, RoutedEventArgs e)
    {
        _app.DismissAutoConnectAgents(_candidates.Select(c => c.Agent.CliKey));
        Close();
    }

    private void LaterButton_Click(object sender, RoutedEventArgs e) => Close();

    private void CloseButton_Click(object sender, RoutedEventArgs e) => Close();

    private void Window_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
        {
            e.Handled = true;
            Close();
        }
    }

    private void TitleBar_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ButtonState != MouseButtonState.Pressed) return;
        if (e.OriginalSource is DependencyObject source && FindAncestor<Button>(source) is not null) return;
        try { DragMove(); } catch { }
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
}
