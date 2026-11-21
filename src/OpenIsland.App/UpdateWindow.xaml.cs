using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using OpenIsland.Core.Diagnostics;
using OpenIsland.Core.Localization;
using OpenIsland.Core.Updates;

namespace OpenIsland.App;

public partial class UpdateWindow : Window
{
    private enum ViewState
    {
        Available,
        Downloading,
        Verifying,
        Preparing,
        Restarting,
        DownloadFailed,
        StatusUpToDate,
        StatusFailed,
        StatusChecking
    }

    private readonly App _app;
    private UpdateReleaseInfo? _release;
    private UpdateCheckResult? _checkResult;
    private ViewState _state;
    private CancellationTokenSource? _operationCts;
    private bool _hasCentered;
    private bool _allowCloseDuringRestart;

    private static readonly Color AccentColor = ColorFromHex("#6EA7FF");
    private static readonly Color GreenColor = ColorFromHex("#6FB982");
    private static readonly Color AmberColor = ColorFromHex("#E7A762");
    private static readonly Color DangerColor = ColorFromHex("#F26D6D");

    public bool IsBusy => _state is ViewState.Downloading
        or ViewState.Verifying
        or ViewState.Preparing
        or ViewState.Restarting
        or ViewState.StatusChecking;

    private bool IsActivelyInstalling => _state is ViewState.Downloading
        or ViewState.Verifying
        or ViewState.Preparing
        or ViewState.Restarting;

    public UpdateWindow(App app, UpdateReleaseInfo release)
    {
        InitializeComponent();
        _app = app;
        ClampMaxHeightToWorkArea();
        Loaded += (_, _) => CenterOnWorkAreaOnce();
        ShowRelease(release);
    }

    public UpdateWindow(App app, UpdateCheckResult checkResult)
    {
        InitializeComponent();
        _app = app;
        ClampMaxHeightToWorkArea();
        Loaded += (_, _) => CenterOnWorkAreaOnce();
        ShowCheckResult(checkResult);
    }

    public void ShowRelease(UpdateReleaseInfo release)
    {
        ArgumentNullException.ThrowIfNull(release);
        if (IsActivelyInstalling) return;

        CancelPendingCheck();
        _release = release;
        _checkResult = null;
        Width = 580;

        ApplyLocalizedLabels();
        PopulateReleaseDetails(release);
        TransitionTo(ViewState.Available);
        RecenterIfLoaded();
    }

    public void ShowCheckResult(UpdateCheckResult result)
    {
        ArgumentNullException.ThrowIfNull(result);
        if (IsActivelyInstalling) return;

        if (result.IsUpdateAvailable && result.Release is not null)
        {
            ShowRelease(result.Release);
            return;
        }

        CancelPendingCheck();
        _release = null;
        _checkResult = result;
        Width = 480;

        ApplyLocalizedLabels();
        PopulateStatusDetails(result);
        TransitionTo(result.Status == UpdateCheckStatus.UpToDate
            ? ViewState.StatusUpToDate
            : ViewState.StatusFailed);
        RecenterIfLoaded();
    }

    // ================================================================
    // Layout & localization
    // ================================================================

    private void ClampMaxHeightToWorkArea()
    {
        var work = SystemParameters.WorkArea;
        if (work.Height > 0)
        {
            MaxHeight = Math.Max(380, work.Height - 40);
        }
    }

    private void CenterOnWorkAreaOnce()
    {
        if (_hasCentered) return;
        _hasCentered = true;
        CenterOnWorkArea();
    }

    private void RecenterIfLoaded()
    {
        if (!IsLoaded) return;
        Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Loaded, new Action(CenterOnWorkArea));
    }

    private void CenterOnWorkArea()
    {
        var work = SystemParameters.WorkArea;
        if (work.Width <= 0 || work.Height <= 0) return;

        var w = ActualWidth > 0 ? ActualWidth : Width;
        var h = ActualHeight > 0 ? ActualHeight : Math.Min(500, work.Height - 40);
        Left = work.Left + Math.Max(0, (work.Width - w) / 2);
        Top = work.Top + Math.Max(0, (work.Height - h) / 2);
        WindowStartupLocation = WindowStartupLocation.Manual;
    }

    private string GetCurrentVersionString()
    {
        if (!string.IsNullOrWhiteSpace(_checkResult?.CurrentVersion))
            return _checkResult.CurrentVersion;
        if (!string.IsNullOrWhiteSpace(_app.Updater?.CurrentVersion))
            return _app.Updater.CurrentVersion;
        return UpdateService.GetCurrentAppVersion(Assembly.GetExecutingAssembly());
    }

    private void ApplyLocalizedLabels()
    {
        var windowTitle = Strings.Get("UpdateWindowTitle");
        Title = windowTitle;
        TitleBarText.Text = windowTitle;
        CloseButton.ToolTip = Strings.Get("Close");

        HeroTitleText.Text = Strings.Get("UpdateAvailableTitle");
        DevWarningText.Text = Strings.Get("DevBuildUpdateHint");
        ReleaseNotesHeaderText.Text = Strings.Get("ReleaseNotesLabel");
        ReleaseNotesGitHubText.Text = Strings.Get("ViewOnGitHub");
        FooterGitHubLinkText.Text = Strings.Get("ViewOnGitHub");
        DevGitHubPrimaryText.Text = Strings.Get("ViewOnGitHub");
        FailureTitleText.Text = Strings.Get("UpdateFailedTitle");
        StatusCheckingText.Text = Strings.Get("UpdateChecking");

        SkipVersionButton.Content = Strings.Get("SkipThisVersion");
        RemindLaterButton.Content = Strings.Get("RemindLater");
        UpdateNowButton.Content = Strings.Get("UpdateNow");
        CancelDownloadButton.Content = Strings.Get("Cancel");
        CloseFooterButton.Content = Strings.Get("CloseWindow");
        ClosePrimaryButton.Content = Strings.Get("CloseWindow");
        RetryButton.Content = Strings.Get("Retry");
    }

    private void PopulateReleaseDetails(UpdateReleaseInfo release)
    {
        var currentVersion = GetCurrentVersionString();
        HeroSubtitleText.Text = Strings.Get("UpdateAvailableSubtitle", currentVersion, release.Version);
        VersionDiffBadgeText.Text = $"v{currentVersion} → v{release.Version}";

        if (release.PublishedAt.HasValue)
        {
            var formattedDate = release.PublishedAt.Value.ToLocalTime()
                .ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
            PublishedAtText.Text = Strings.Get("PublishedAtFormat", formattedDate);
            PublishedAtText.Visibility = Visibility.Visible;
        }
        else
        {
            PublishedAtText.Text = string.Empty;
            PublishedAtText.Visibility = Visibility.Collapsed;
        }

        if (release.AssetSizeBytes > 0 && release.InstallMode != UpdateInstallMode.Development)
        {
            AssetSizeText.Text = $"·  {FormatBytes(release.AssetSizeBytes)}";
            AssetSizeText.Visibility = Visibility.Visible;
        }
        else
        {
            AssetSizeText.Text = string.Empty;
            AssetSizeText.Visibility = Visibility.Collapsed;
        }

        ApplyInstallModeChip(release.InstallMode, InstallModeChip, InstallModeText, InstallModeDot);

        var notes = string.IsNullOrWhiteSpace(release.ReleaseNotes)
            ? Strings.Get("NoReleaseNotes")
            : release.ReleaseNotes.Replace("\r\n", "\n").Trim();
        ReleaseNotesText.Text = notes;
        ReleaseNotesScroll.ScrollToTop();
    }

    private void PopulateStatusDetails(UpdateCheckResult result)
    {
        ApplyInstallModeChip(result.InstallMode, StatusInstallModeChip, StatusInstallModeText, dot: null);

        if (result.Status == UpdateCheckStatus.UpToDate)
        {
            StatusIconBadge.Background = AlphaBrush(GreenColor, 26);
            StatusIconBadge.BorderBrush = AlphaBrush(GreenColor, 82);
            StatusIconPath.Data = (Geometry)FindResource("IconCheck");
            StatusIconPath.Fill = new SolidColorBrush(GreenColor);

            StatusTitleText.Text = Strings.Get("UpdateUpToDateTitle");
            StatusMessageText.Text = Strings.Get("UpdateUpToDateMessage", result.CurrentVersion);
            StatusErrorDetailText.Text = string.Empty;
        }
        else
        {
            StatusIconBadge.Background = AlphaBrush(DangerColor, 26);
            StatusIconBadge.BorderBrush = AlphaBrush(DangerColor, 82);
            StatusIconPath.Data = (Geometry)FindResource("IconAlert");
            StatusIconPath.Fill = new SolidColorBrush(DangerColor);

            StatusTitleText.Text = Strings.Get("UpdateCheckFailedTitle");
            StatusMessageText.Text = Strings.Get("UpdateCheckFailed");
            StatusErrorDetailText.Text = string.IsNullOrWhiteSpace(result.ErrorMessage)
                ? Strings.Get("UpdateCheckFailed")
                : result.ErrorMessage.Trim();
        }
    }

    private void ApplyInstallModeChip(UpdateInstallMode mode, Border chip, TextBlock label, FrameworkElement? dot)
    {
        var (textKey, color) = mode switch
        {
            UpdateInstallMode.Installed => ("InstallModeInstalled", GreenColor),
            UpdateInstallMode.Portable => ("InstallModePortable", AccentColor),
            _ => ("InstallModeDev", AmberColor)
        };

        label.Text = Strings.Get(textKey);
        label.Foreground = new SolidColorBrush(color);
        chip.Background = AlphaBrush(color, 22);
        chip.BorderBrush = AlphaBrush(color, 72);
        if (dot is System.Windows.Shapes.Shape shape)
        {
            shape.Fill = new SolidColorBrush(color);
        }
    }

    private void TransitionTo(ViewState state)
    {
        _state = state;

        bool isReleaseMode = state is ViewState.Available
            or ViewState.Downloading
            or ViewState.Verifying
            or ViewState.Preparing
            or ViewState.Restarting
            or ViewState.DownloadFailed;
        bool isDevBuild = _release?.InstallMode == UpdateInstallMode.Development;

        ReleaseView.Visibility = isReleaseMode ? Visibility.Visible : Visibility.Collapsed;
        StatusView.Visibility = isReleaseMode ? Visibility.Collapsed : Visibility.Visible;

        // Release view sections
        DevWarningBanner.Visibility = isReleaseMode && isDevBuild ? Visibility.Visible : Visibility.Collapsed;
        ReleaseNotesCard.Visibility = isReleaseMode ? Visibility.Visible : Visibility.Collapsed;
        ReleaseNotesGitHubButton.Visibility = state == ViewState.Available && !isDevBuild
            ? Visibility.Visible
            : Visibility.Collapsed;
        ProgressCard.Visibility = state is ViewState.Downloading
            or ViewState.Verifying
            or ViewState.Preparing
            or ViewState.Restarting
            ? Visibility.Visible
            : Visibility.Collapsed;
        FailureCard.Visibility = state == ViewState.DownloadFailed ? Visibility.Visible : Visibility.Collapsed;

        // Status view sections
        StatusCheckingPanel.Visibility = state == ViewState.StatusChecking ? Visibility.Visible : Visibility.Collapsed;
        StatusErrorBox.Visibility = state == ViewState.StatusFailed && !string.IsNullOrWhiteSpace(StatusErrorDetailText.Text)
            ? Visibility.Visible
            : Visibility.Collapsed;

        // Title bar close button
        CloseButton.IsEnabled = !IsActivelyInstalling;

        // Footer buttons — reset first, then enable exact set for current state
        SkipVersionButton.Visibility = Visibility.Collapsed;
        FooterGitHubLinkButton.Visibility = Visibility.Collapsed;
        RemindLaterButton.Visibility = Visibility.Collapsed;
        UpdateNowButton.Visibility = Visibility.Collapsed;
        DevGitHubPrimaryButton.Visibility = Visibility.Collapsed;
        CancelDownloadButton.Visibility = Visibility.Collapsed;
        CloseFooterButton.Visibility = Visibility.Collapsed;
        RetryButton.Visibility = Visibility.Collapsed;
        ClosePrimaryButton.Visibility = Visibility.Collapsed;

        switch (state)
        {
            case ViewState.Available:
                SkipVersionButton.Visibility = Visibility.Visible;
                RemindLaterButton.Visibility = Visibility.Visible;
                if (isDevBuild)
                {
                    DevGitHubPrimaryButton.Visibility = Visibility.Visible;
                }
                else
                {
                    UpdateNowButton.Visibility = Visibility.Visible;
                }
                break;

            case ViewState.Downloading:
            case ViewState.Verifying:
                CancelDownloadButton.Visibility = Visibility.Visible;
                CancelDownloadButton.IsEnabled = true;
                break;

            case ViewState.Preparing:
            case ViewState.Restarting:
                CancelDownloadButton.Visibility = Visibility.Visible;
                CancelDownloadButton.IsEnabled = false;
                break;

            case ViewState.DownloadFailed:
                FooterGitHubLinkButton.Visibility = Visibility.Visible;
                CloseFooterButton.Visibility = Visibility.Visible;
                if (!isDevBuild)
                {
                    RetryButton.Visibility = Visibility.Visible;
                    RetryButton.IsEnabled = true;
                }
                break;

            case ViewState.StatusUpToDate:
                FooterGitHubLinkButton.Visibility = Visibility.Visible;
                ClosePrimaryButton.Visibility = Visibility.Visible;
                break;

            case ViewState.StatusFailed:
                FooterGitHubLinkButton.Visibility = Visibility.Visible;
                CloseFooterButton.Visibility = Visibility.Visible;
                RetryButton.Visibility = Visibility.Visible;
                RetryButton.IsEnabled = true;
                break;

            case ViewState.StatusChecking:
                FooterGitHubLinkButton.Visibility = Visibility.Visible;
                CloseFooterButton.Visibility = Visibility.Visible;
                break;
        }
    }

    // ================================================================
    // Actions
    // ================================================================

    private void UpdateNowButton_Click(object sender, RoutedEventArgs e)
    {
        if (_release is null || _release.InstallMode == UpdateInstallMode.Development) return;
        _ = StartDownloadAndInstallAsync(_release);
    }

    private void RetryButton_Click(object sender, RoutedEventArgs e)
    {
        if (_state == ViewState.DownloadFailed && _release is not null)
        {
            if (_release.InstallMode == UpdateInstallMode.Development) return;
            _ = StartDownloadAndInstallAsync(_release);
        }
        else if (_state == ViewState.StatusFailed)
        {
            _ = RetryStatusCheckAsync();
        }
    }

    private async Task StartDownloadAndInstallAsync(UpdateReleaseInfo release)
    {
        if (IsActivelyInstalling) return;

        CancelPendingCheck();
        _operationCts = new CancellationTokenSource();
        var ct = _operationCts.Token;

        DownloadProgressBar.IsIndeterminate = release.AssetSizeBytes <= 0;
        DownloadProgressBar.Value = 0;
        ProgressStageText.Text = Strings.Get("UpdateDownloading");
        ProgressPercentText.Text = release.AssetSizeBytes > 0 ? "0%" : string.Empty;
        ProgressDetailText.Text = release.AssetSizeBytes > 0
            ? $"0 B / {FormatBytes(release.AssetSizeBytes)}"
            : release.AssetName;
        TransitionTo(ViewState.Downloading);

        var progress = new Progress<UpdateDownloadProgress>(OnDownloadProgress);

        try
        {
            var prepared = await _app.Updater.DownloadAndVerifyAsync(release, progress, ct);

            // Switch to Restarting state before handing off to App.ShutdownForUpdate.
            ProgressStageText.Text = Strings.Get("UpdateRestarting");
            ProgressPercentText.Text = string.Empty;
            ProgressDetailText.Text = release.AssetName;
            DownloadProgressBar.IsIndeterminate = true;
            TransitionTo(ViewState.Restarting);

            _allowCloseDuringRestart = true;
            try
            {
                _app.ShutdownForUpdate(prepared);
            }
            catch
            {
                _allowCloseDuringRestart = false;
                throw;
            }
        }
        catch (OperationCanceledException)
        {
            AppLog.Info($"update download cancelled by user for v{release.Version}");
            DownloadProgressBar.IsIndeterminate = false;
            DownloadProgressBar.Value = 0;
            TransitionTo(ViewState.Available);
        }
        catch (Exception ex)
        {
            AppLog.Error($"update download/install failed for v{release.Version}", ex);
            DownloadProgressBar.IsIndeterminate = false;
            FailureMessageText.Text = ex.Message;
            TransitionTo(ViewState.DownloadFailed);
        }
        finally
        {
            _operationCts?.Dispose();
            _operationCts = null;
        }
    }

    private void OnDownloadProgress(UpdateDownloadProgress p)
    {
        if (_state is ViewState.Restarting or ViewState.DownloadFailed or ViewState.Available)
            return;

        switch (p.Stage)
        {
            case UpdateDownloadStage.Downloading:
                if (_state != ViewState.Downloading)
                    TransitionTo(ViewState.Downloading);

                ProgressStageText.Text = Strings.Get("UpdateDownloading");

                if (p.Fraction.HasValue && p.TotalBytes is > 0)
                {
                    DownloadProgressBar.IsIndeterminate = false;
                    var pct = Math.Clamp(p.Fraction.Value * 100.0, 0.0, 100.0);
                    DownloadProgressBar.Value = pct;
                    ProgressPercentText.Text = $"{(int)Math.Round(pct)}%";
                }
                else
                {
                    DownloadProgressBar.IsIndeterminate = true;
                    ProgressPercentText.Text = string.Empty;
                }

                ProgressDetailText.Text = FormatTransferSummary(p.BytesReceived, p.TotalBytes, p.SpeedBytesPerSecond);
                break;

            case UpdateDownloadStage.Verifying:
                if (_state != ViewState.Verifying)
                    TransitionTo(ViewState.Verifying);

                ProgressStageText.Text = Strings.Get("UpdateVerifying");
                ProgressPercentText.Text = string.Empty;
                DownloadProgressBar.IsIndeterminate = true;
                ProgressDetailText.Text = _release?.AssetName ?? string.Empty;
                break;

            case UpdateDownloadStage.Preparing:
                if (_state != ViewState.Preparing)
                    TransitionTo(ViewState.Preparing);

                ProgressStageText.Text = Strings.Get("UpdatePreparing");
                ProgressPercentText.Text = string.Empty;
                DownloadProgressBar.IsIndeterminate = true;
                ProgressDetailText.Text = _release?.AssetName ?? string.Empty;
                break;

            case UpdateDownloadStage.ReadyToInstall:
                ProgressStageText.Text = Strings.Get("UpdateRestarting");
                ProgressPercentText.Text = string.Empty;
                DownloadProgressBar.IsIndeterminate = true;
                break;
        }
    }

    private async Task RetryStatusCheckAsync()
    {
        if (IsBusy) return;

        CancelPendingCheck();
        _operationCts = new CancellationTokenSource();
        var ct = _operationCts.Token;

        StatusIconBadge.Background = AlphaBrush(AccentColor, 26);
        StatusIconBadge.BorderBrush = AlphaBrush(AccentColor, 82);
        StatusIconPath.Data = (Geometry)FindResource("IconRefresh");
        StatusIconPath.Fill = new SolidColorBrush(AccentColor);
        StatusTitleText.Text = Strings.Get("UpdateChecking");
        StatusMessageText.Text = string.Format(
            CultureInfo.CurrentCulture,
            "{0} v{1}",
            Strings.Get("CurrentVersionLabel"),
            GetCurrentVersionString());
        TransitionTo(ViewState.StatusChecking);

        try
        {
            var result = await _app.CheckForUpdatesManualAsync(showNoUpdateDialog: false, ct);
            if (!IsLoaded) return;
            ShowCheckResult(result);
        }
        catch (OperationCanceledException)
        {
            // Window closed while re-checking.
        }
        catch (Exception ex)
        {
            if (!IsLoaded) return;
            var fallback = new UpdateCheckResult(
                UpdateCheckStatus.Failed,
                GetCurrentVersionString(),
                null,
                ex.Message,
                _app.Updater?.InstallMode ?? UpdateInstallMode.Portable);
            ShowCheckResult(fallback);
        }
        finally
        {
            _operationCts?.Dispose();
            _operationCts = null;
        }
    }

    private void CancelDownloadButton_Click(object sender, RoutedEventArgs e)
    {
        if (_state is ViewState.Downloading or ViewState.Verifying)
        {
            CancelDownloadButton.IsEnabled = false;
            try
            {
                _operationCts?.Cancel();
            }
            catch
            {
                // Ignore race if CTS was already disposed.
            }
        }
    }

    private void SkipVersionButton_Click(object sender, RoutedEventArgs e)
    {
        if (_release is not null)
        {
            _app.SkipUpdateVersion(_release.Version);
        }
        Close();
    }

    private void RemindLaterButton_Click(object sender, RoutedEventArgs e) => Close();

    private void ViewOnGitHubButton_Click(object sender, RoutedEventArgs e)
    {
        var url = !string.IsNullOrWhiteSpace(_release?.HtmlUrl)
            ? _release.HtmlUrl
            : UpdateService.ReleasesPageUrl;
        try
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = url,
                UseShellExecute = true
            });
        }
        catch (Exception ex)
        {
            AppLog.Warn($"failed to open GitHub release URL '{url}': {ex.Message}");
        }
    }

    private void CloseButton_Click(object sender, RoutedEventArgs e)
    {
        if (IsActivelyInstalling) return;
        Close();
    }

    // ================================================================
    // Window chrome & lifecycle
    // ================================================================

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

    private void Window_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
        {
            e.Handled = true;
            if (!IsActivelyInstalling)
            {
                Close();
            }
        }
    }

    private void Window_Closing(object? sender, CancelEventArgs e)
    {
        if (IsActivelyInstalling && !_allowCloseDuringRestart)
        {
            e.Cancel = true;
            return;
        }

        CancelPendingCheck();
    }

    private void CancelPendingCheck()
    {
        try
        {
            _operationCts?.Cancel();
        }
        catch
        {
            // Ignore already-disposed CTS.
        }
    }

    // ================================================================
    // Formatting & visual helpers
    // ================================================================

    internal static string FormatBytes(long bytes)
    {
        if (bytes < 0) bytes = 0;
        if (bytes < 1024) return $"{bytes} B";
        if (bytes < 1024L * 1024L) return $"{bytes / 1024.0:0.0} KB";
        if (bytes < 1024L * 1024L * 1024L) return $"{bytes / (1024.0 * 1024.0):0.0} MB";
        return $"{bytes / (1024.0 * 1024.0 * 1024.0):0.00} GB";
    }

    internal static string FormatTransferSummary(long received, long? total, double speedBytesPerSecond)
    {
        var sizePart = total is > 0
            ? $"{FormatBytes(received)} / {FormatBytes(total.Value)}"
            : FormatBytes(received);

        if (speedBytesPerSecond > 1.0)
        {
            return $"{sizePart}  ·  {FormatBytes((long)Math.Round(speedBytesPerSecond))}/s";
        }

        return sizePart;
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
