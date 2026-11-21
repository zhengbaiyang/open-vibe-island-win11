using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;
using OpenIsland.Core.Localization;
using OpenIsland.Core.Setup;

namespace OpenIsland.App;

public partial class AgentConnectWindow : Window
{
    private readonly App _app;
    private List<AgentInstallState> _candidates = new();
    private readonly List<(AgentInstallState State, CheckBox Toggle)> _rows = new();
    private bool _completed;

    private static readonly Color GreenColor = ColorFromHex("#6FB982");
    private static readonly Color AmberColor = ColorFromHex("#E7A762");
    private static readonly Color DangerColor = ColorFromHex("#F26D6D");
    private static readonly Color AccentColor = ColorFromHex("#6EA7FF");

    public AgentConnectWindow(App app, IReadOnlyList<AgentInstallState> candidates)
    {
        InitializeComponent();
        _app = app;
        LoadCandidates(candidates);
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
            var (card, toggle) = BuildCandidateRow(candidate);
            _rows.Add((candidate, toggle));
            CandidatesHost.Children.Add(card);
        }

        UpdateConfirmButton();
    }

    private (Border Card, CheckBox Toggle) BuildCandidateRow(AgentInstallState state)
    {
        var agent = state.Agent;
        var accent = ColorFromHex(agent.AccentHex);

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

        var iconTile = new Border
        {
            Width = 36,
            Height = 36,
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
            FontSize = 14,
            FontWeight = FontWeights.SemiBold,
            Foreground = new SolidColorBrush(accent),
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center
        };
        Grid.SetColumn(iconTile, 0);
        grid.Children.Add(iconTile);

        var infoStack = new StackPanel { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 12, 0) };
        var headerRow = new StackPanel { Orientation = Orientation.Horizontal };
        headerRow.Children.Add(new TextBlock
        {
            Text = agent.DisplayName,
            FontSize = 13,
            FontWeight = FontWeights.SemiBold,
            Foreground = (Brush)FindResource("BrushTextPrimary"),
            VerticalAlignment = VerticalAlignment.Center
        });

        var isPartial = state.Status == HookInstallStatus.Partial;
        var chipColor = isPartial ? AmberColor : AccentColor;
        var chipLabel = isPartial ? Strings.Get("Partial") : Strings.Get("DetectedOnPc");
        var chip = new Border
        {
            Style = (Style)FindResource("StatusChip"),
            Background = AlphaBrush(chipColor, 20),
            BorderBrush = AlphaBrush(chipColor, 66),
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
                Foreground = new SolidColorBrush(ColorFromHex("#7D8A9F")),
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
            Foreground = new SolidColorBrush(ColorFromHex("#677386")),
            Margin = new Thickness(0, 2, 0, 0),
            TextTrimming = TextTrimming.CharacterEllipsis,
            ToolTip = state.ConfigPath
        });

        Grid.SetColumn(infoStack, 1);
        grid.Children.Add(infoStack);

        var toggle = new CheckBox
        {
            Style = (Style)FindResource("ToggleSwitch"),
            IsChecked = true,
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
        ResultBanner.Visibility = Visibility.Visible;
        ResultBanner.Background = new SolidColorBrush(allOk
            ? Color.FromArgb(255, 17, 34, 27)
            : Color.FromArgb(255, 38, 22, 24));
        ResultBanner.BorderBrush = new SolidColorBrush(allOk
            ? Color.FromArgb(255, 40, 80, 62)
            : Color.FromArgb(255, 102, 48, 56));
        ResultIcon.Data = (Geometry)FindResource(allOk ? "IconCheck" : "IconAlert");
        ResultIcon.Fill = new SolidColorBrush(allOk ? GreenColor : DangerColor);
        ResultTitleText.Foreground = new SolidColorBrush(allOk ? ColorFromHex("#A9D9B9") : ColorFromHex("#F5B8B8"));
        ResultTitleText.Text = allOk
            ? Strings.Get("AgentConnectSuccess", succeeded)
            : Strings.Get("AgentConnectPartialFail", succeeded, failures.Count, string.Join("; ", failures));
        ResultHintText.Text = Strings.Get("AgentConnectRestartNote");

        DontAskButton.Visibility = Visibility.Collapsed;
        LaterButton.Visibility = Visibility.Collapsed;
        ConfirmButton.Visibility = Visibility.Collapsed;
        DoneButton.Visibility = Visibility.Visible;
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

    private static Color ColorFromHex(string hex)
    {
        try { return (Color)ColorConverter.ConvertFromString(hex); }
        catch { return Colors.Gray; }
    }

    private static SolidColorBrush AlphaBrush(Color color, byte alpha) =>
        new(Color.FromArgb(alpha, color.R, color.G, color.B));
}
