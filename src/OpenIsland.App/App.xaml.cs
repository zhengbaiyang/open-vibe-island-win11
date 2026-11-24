using System.Diagnostics;
using System.IO;
using System.Media;
using System.Reflection;
using System.Windows;
using System.Windows.Threading;
using OpenIsland.App.Themes;
using OpenIsland.Core.Diagnostics;
using OpenIsland.Core.Discovery;
using OpenIsland.Core.Hooks;
using OpenIsland.Core.IPC;
using OpenIsland.Core.Localization;
using OpenIsland.Core.Models;
using OpenIsland.Core.Platform;
using OpenIsland.Core.Setup;
using OpenIsland.Core.State;
using OpenIsland.Core.Updates;
using Drawing = System.Drawing;
using Forms = System.Windows.Forms;

namespace OpenIsland.App;

/// <summary>Snapshot of the agent-bridge connection state shown in Settings -> Connection.</summary>
public sealed record ConnectionDiagnostics(
    bool BridgeRunning,
    string PipeName,
    string PipePath,
    int ActiveSessions,
    int InstalledAgents,
    int TotalAgents,
    DateTimeOffset? LastEventAt,
    string? LastEventName,
    string? LastEventSource,
    int EventCount,
    string HooksBinaryPath,
    bool HooksBinaryPresent,
    string HooksBinaryVersion,
    string LogPath)
{
    public bool HasReceivedEvents => EventCount > 0;
}

public partial class App : System.Windows.Application
{
    private Mutex? _singleInstanceMutex;
    private Forms.NotifyIcon? _trayIcon;
    private DispatcherTimer? _pollTimer;
    private DispatcherTimer? _selfTestTimer;
    private readonly CancellationTokenSource _shutdownCts = new();
    private readonly SemaphoreSlim _updateCheckLock = new(1, 1);
    private bool _isShuttingDown;
    private volatile UpdateCheckResult? _lastUpdateCheck;
    private volatile bool _isCheckingForUpdates;

    public AppSettings Settings { get; private set; } = new();
    public SessionStateReducer Reducer { get; } = new();
    public BridgeServer Bridge { get; private set; } = null!;
    public TranscriptDiscovery Discovery { get; } = new();
    public HookInstallManager SetupManager { get; } = new();
    public CodexAppServerClient? CodexServer { get; private set; }
    public IslandWindow? Island { get; private set; }
    public UpdateService Updater { get; private set; } = null!;
    public UpdateCheckResult? LastUpdateCheck
    {
        get => _lastUpdateCheck;
        private set => _lastUpdateCheck = value;
    }
    public bool IsCheckingForUpdates
    {
        get => _isCheckingForUpdates;
        private set => _isCheckingForUpdates = value;
    }
    public event Action<UpdateCheckResult>? UpdateCheckCompleted;
    public event Action? AgentInstallStateChanged;
    private SettingsWindow? _settingsWindow;
    private UpdateWindow? _updateWindow;
    private AgentConnectWindow? _agentConnectWindow;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);


        _singleInstanceMutex = new Mutex(true, BridgeConstants.MutexName, out bool createdNew);
        if (!createdNew)
        {
            AppLog.Warn($"second instance detected, exiting (mutex={BridgeConstants.MutexName})");
            Shutdown();
            return;
        }

        AppLog.Info($"==== Open Island starting (pid={Environment.ProcessId}, instance='{BridgeConstants.InstanceSuffix}', data='{BridgeConstants.AppDataDir}') ====");

        Settings = AppSettings.Load();
        Strings.CurrentLanguage = Settings.Language;
        SettingsThemeManager.Initialize(Settings.SettingsAppearance);
        Reducer.CoalesceRecurringSessions = Settings.CoalesceRecurringSessions;

        Updater = new UpdateService(currentVersion: UpdateService.GetCurrentAppVersion(Assembly.GetExecutingAssembly()));
        AppLog.Info($"updater initialized: version={Updater.CurrentVersion}, mode={Updater.InstallMode}, dir='{Updater.AppBaseDir}'");

        if (Settings.LaunchAtStartup)
        {
            try
            {
                var startupSync = StartupRegistryManager.Apply(true);
                if (!startupSync.Ok)
                {
                    AppLog.Warn($"startup registry sync failed on launch: {startupSync.Error}");
                }
            }
            catch (Exception ex)
            {
                AppLog.Warn($"startup registry sync threw on launch: {ex.Message}");
            }
        }

        _ = Task.Run(() => Updater.CleanupOldUpdateArtifacts());

        AppDomain.CurrentDomain.UnhandledException += (_, args) =>
            AppLog.Error("unhandled exception", args.ExceptionObject as Exception);
        DispatcherUnhandledException += (_, args) =>
        {
            AppLog.Error("dispatcher exception", args.Exception);
            args.Handled = true;
        };

        // Resolve the hooks executable that ships next to the app (the packaged build is
        // self-contained, so it resolves its own runtime/dependencies there). Never copy
        // only the apphost to %LOCALAPPDATA%\OpenIsland\bin — that binary cannot start.
        SetupManager.EnsureHooksBinaryInstalled();

        // Repair Codex config.toml if an older Open Island build appended its
        // (invalid for current Codex) [features]/[[hooks]] block. That block makes
        // ChatGPT/Codex App fail to start, so this migration must run unconditionally.
        try
        {
            var repaired = SetupManager.RepairLegacyCodexHooksConfig();
            if (repaired) AppLog.Warn("repaired legacy Codex config.toml (removed invalid Open Island hook block)");
        }
        catch (Exception ex)
        {
            AppLog.Error("Codex config repair failed", ex);
        }

        // Older builds pointed hooks at a copied apphost that could not start. Rewrite those
        // configurations to the bundled hooks executable so approvals/questions/events work
        // again without the user having to reinstall hooks manually.
        try
        {
            var migrated = SetupManager.MigrateHooksToBundledBinary();
            if (migrated > 0) AppLog.Info($"migrated {migrated} hook configuration(s) to bundled hooks binary");
        }
        catch (Exception ex)
        {
            AppLog.Error("hook path migration failed", ex);
        }

        // Restore cached sessions
        Reducer.RestorePersistedSessions(SessionPersistence.Load());

        // Start Named Pipe BridgeServer
        Bridge = new BridgeServer(Reducer);
        Bridge.Start();
        AppLog.Info($"bridge listening on {BridgeConstants.FullPipePath}");

        // Optional Codex Desktop app-server JSON-RPC bridge
        if (Settings.EnableCodexAppServer)
        {
            CodexServer = new CodexAppServerClient(Reducer);
            CodexServer.StartIfAvailable();
        }

        // Wire notification sounds
        Reducer.ApprovalRequested += _ => PlayNotificationSound(isUrgent: true);
        Reducer.QuestionRequested += _ => PlayNotificationSound(isUrgent: true);
        Reducer.TurnCompleted += _ => PlayNotificationSound(isUrgent: false);

        // Create floating Island window & System Tray icon
        Island = new IslandWindow(this);
        Island.Show();

        SetupTrayIcon();
        RunDiscoveryPass();

        if (e.Args.Any(a => string.Equals(a, "--settings", StringComparison.OrdinalIgnoreCase)))
        {
            OpenSettings();
        }

        _pollTimer = new DispatcherTimer
        {
            Interval = TimeSpan.FromMilliseconds(Math.Clamp(Settings.PollIntervalMs, 1000, 15000))
        };
        _pollTimer.Tick += (_, _) => RunDiscoveryPass();
        _pollTimer.Start();

        if (Settings.AutoDetectAgentsOnStartup)
        {
            _ = RunStartupAgentDetectionAsync();
        }

        if (Settings.AutoCheckForUpdates)
        {
            _ = RunStartupUpdateCheckAsync();
        }
    }

    public ConnectionDiagnostics GetConnectionDiagnostics()
    {
        var statuses = SetupManager.GetAllStatuses();
        var hooksPath = SetupManager.EnsureHooksBinaryInstalled();
        var present = File.Exists(hooksPath);
        var version = "—";
        if (present)
        {
            try
            {
                var info = FileVersionInfo.GetVersionInfo(hooksPath);
                version = !string.IsNullOrWhiteSpace(info.FileVersion)
                    ? info.FileVersion
                    : info.ProductVersion ?? "—";
            }
            catch { }
        }

        return new ConnectionDiagnostics(
            BridgeRunning: Bridge?.IsRunning == true,
            PipeName: BridgeConstants.PipeName,
            PipePath: BridgeConstants.FullPipePath,
            ActiveSessions: Reducer.GetActiveSessions().Count,
            InstalledAgents: statuses.Count(s => s.Status == HookInstallStatus.Installed),
            TotalAgents: statuses.Count,
            LastEventAt: Reducer.LastEventAt,
            LastEventName: Reducer.LastEventName,
            LastEventSource: Reducer.LastEventSource?.ToString(),
            EventCount: Reducer.EventCount,
            HooksBinaryPath: hooksPath,
            HooksBinaryPresent: present,
            HooksBinaryVersion: version,
            LogPath: AppLog.EffectiveLogPath);
    }

    /// <summary>
    /// Injects a complete synthetic session lifecycle through the exact same reducer path that
    /// hooks use, so the user can verify the island end-to-end without waiting for a real agent.
    /// </summary>
    public (bool Ok, string Message) SendTestEvent()
    {
        try
        {
            var id = "openisland-selftest-" + Guid.NewGuid().ToString("N")[..8];
            var now = DateTimeOffset.Now;
            Reducer.ApplyHookEvent(new NormalizedHookEvent
            {
                Source = AgentSource.Claude,
                EventName = "SessionStart",
                SessionId = id,
                Cwd = Environment.CurrentDirectory,
                Model = "open-island-selftest",
                Terminal = new TerminalMetadata { TerminalApp = "Open Island", TerminalTitle = "Self-test session" }
            });
            Reducer.ApplyHookEvent(new NormalizedHookEvent
            {
                Source = AgentSource.Claude,
                EventName = "UserPromptSubmit",
                SessionId = id,
                Prompt = Strings.Get("SelfTestPrompt")
            });
            Reducer.ApplyHookEvent(new NormalizedHookEvent
            {
                Source = AgentSource.Claude,
                EventName = "PreToolUse",
                SessionId = id,
                ToolName = "Bash",
                ToolInputPreview = "npm run build"
            });

            _selfTestTimer?.Stop();
            _selfTestTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(6) };
            _selfTestTimer.Tick += (_, _) =>
            {
                _selfTestTimer?.Stop();
                Reducer.ApplyHookEvent(new NormalizedHookEvent
                {
                    Source = AgentSource.Claude,
                    EventName = "Stop",
                    SessionId = id,
                    LastAssistantMessage = Strings.Get("SelfTestDone")
                });
            };
            _selfTestTimer.Start();

            Island?.ShowAndExpand();
            AppLog.Info("self-test event sequence injected");
            return (true, Strings.Get("SelfTestSent"));
        }
        catch (Exception ex)
        {
            AppLog.Error("self-test failed", ex);
            return (false, ex.Message);
        }
    }

    public void OpenLogFolder()
    {
        try
        {
            Directory.CreateDirectory(BridgeConstants.LogsDir);
            Process.Start(new ProcessStartInfo { FileName = BridgeConstants.LogsDir, UseShellExecute = true });
        }
        catch (Exception ex)
        {
            AppLog.Error("cannot open log folder", ex);
        }
    }

    public void RunDiscoveryPass()
    {
        Task.Run(() =>
        {
            try
            {
                var knownCwds = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
                foreach (var s in Reducer.GetActiveSessions())
                {
                    if (!string.IsNullOrWhiteSpace(s.Cwd))
                    {
                        knownCwds[s.SessionId] = s.Cwd;
                    }
                }

                var discovered = Discovery.ScanRecentSessions(knownSessionCwds: knownCwds);
                foreach (var s in discovered)
                {
                    Reducer.UpsertDiscoveredSession(s);
                }

                var (aliveAgents, alivePids) = ProcessDiscovery.ScanRunningAgents();
                Reducer.ReconcileProcessLiveness(aliveAgents, alivePids);
                SessionPersistence.Save(Reducer.GetActiveSessions());
            }
            catch (Exception ex)
            {
                AppLog.Error("discovery pass failed", ex);
            }
        });
    }

    public void OpenSettings()
    {
        if (_settingsWindow is { IsLoaded: true })
        {
            if (_settingsWindow.WindowState == WindowState.Minimized)
                _settingsWindow.WindowState = WindowState.Normal;
            _settingsWindow.Activate();
            return;
        }
        _settingsWindow = new SettingsWindow(this);
        _settingsWindow.Show();
        _settingsWindow.Activate();
        AppLog.Info("settings window opened");
    }

    public void ApplySettings(AppSettings updated)
    {
        Settings = updated;
        Strings.CurrentLanguage = updated.Language;
        SettingsThemeManager.Apply(updated.SettingsAppearance);
        Reducer.CoalesceRecurringSessions = updated.CoalesceRecurringSessions;
        updated.Save();
        AppLog.Info($"settings saved: lang={updated.Language} edge={updated.EdgePosition} theme={updated.ThemeStyle} appearance={updated.SettingsAppearance} monitor={updated.MonitorIndex} startup={updated.LaunchAtStartup} autoUpdate={updated.AutoCheckForUpdates}");

        if (_pollTimer != null)
        {
            _pollTimer.Interval = TimeSpan.FromMilliseconds(Math.Clamp(updated.PollIntervalMs, 1000, 15000));
        }

        Island?.ApplyDisplaySettings();
        RebuildTrayMenu();

        var startupResult = StartupRegistryManager.Apply(updated.LaunchAtStartup);
        if (!startupResult.Ok)
        {
            var err = startupResult.Error ?? "Failed to update Windows startup registry entry.";
            AppLog.Warn($"startup registry sync failed: {err}");
            throw new InvalidOperationException(err);
        }
    }

    private async Task RunStartupUpdateCheckAsync()
    {
        try
        {
            await Task.Delay(TimeSpan.FromSeconds(3), _shutdownCts.Token).ConfigureAwait(false);
            var result = await CheckForUpdatesCoreAsync(_shutdownCts.Token).ConfigureAwait(false);

            if (result.IsUpdateAvailable && result.Release is not null)
            {
                if (string.Equals(result.Release.Version, Settings.SkippedUpdateVersion, StringComparison.OrdinalIgnoreCase))
                {
                    AppLog.Info($"startup update check skipped v{result.Release.Version} per user preference");
                    return;
                }

                await Dispatcher.InvokeAsync(() => ShowUpdateWindow(result.Release));
            }
            else if (result.Status == UpdateCheckStatus.Failed)
            {
                AppLog.Warn($"startup background update check failed: {result.ErrorMessage}");
            }
        }
        catch (OperationCanceledException)
        {
            // Application is shutting down before startup check completed.
        }
        catch (Exception ex)
        {
            AppLog.Warn($"startup background update check failed unexpectedly: {ex.Message}");
        }
    }

    private async Task TriggerTrayUpdateCheckAsync()
    {
        try
        {
            await CheckForUpdatesManualAsync(showNoUpdateDialog: true);
        }
        catch (OperationCanceledException)
        {
            // App shutting down during manual check.
        }
        catch (Exception ex)
        {
            AppLog.Warn($"tray manual update check failed unexpectedly: {ex.Message}");
        }
    }

    public async Task<UpdateCheckResult> CheckForUpdatesManualAsync(bool showNoUpdateDialog, CancellationToken ct = default)
    {
        using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(ct, _shutdownCts.Token);
        var result = await CheckForUpdatesCoreAsync(linkedCts.Token);

        // Manual checks always ignore SkippedUpdateVersion.
        if (result.IsUpdateAvailable && result.Release is not null)
        {
            await Dispatcher.InvokeAsync(() => ShowUpdateWindow(result.Release));
        }
        else if (showNoUpdateDialog)
        {
            await Dispatcher.InvokeAsync(() => ShowUpdateStatusWindow(result));
        }

        return result;
    }

    private async Task<UpdateCheckResult> CheckForUpdatesCoreAsync(CancellationToken ct)
    {
        await _updateCheckLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            _isCheckingForUpdates = true;
            var result = await Updater.CheckForUpdatesAsync(ct).ConfigureAwait(false);
            _lastUpdateCheck = result;
            _isCheckingForUpdates = false;
            try
            {
                UpdateCheckCompleted?.Invoke(result);
            }
            catch (Exception ex)
            {
                AppLog.Warn($"UpdateCheckCompleted subscriber error: {ex.Message}");
            }
            return result;
        }
        finally
        {
            _isCheckingForUpdates = false;
            _updateCheckLock.Release();
        }
    }

    public void ShowUpdateWindow(UpdateReleaseInfo release)
    {
        ArgumentNullException.ThrowIfNull(release);

        if (_updateWindow is { IsLoaded: true })
        {
            if (!_updateWindow.IsBusy)
            {
                _updateWindow.ShowRelease(release);
            }
            if (_updateWindow.WindowState == WindowState.Minimized)
            {
                _updateWindow.WindowState = WindowState.Normal;
            }
            _updateWindow.Activate();
            return;
        }

        _updateWindow = new UpdateWindow(this, release);
        _updateWindow.Closed += (_, _) => _updateWindow = null;
        _updateWindow.Show();
        _updateWindow.Activate();
        AppLog.Info($"update window opened for v{release.Version} (mode={release.InstallMode})");
    }

    public void ShowUpdateStatusWindow(UpdateCheckResult result)
    {
        ArgumentNullException.ThrowIfNull(result);

        if (result.IsUpdateAvailable && result.Release is not null)
        {
            ShowUpdateWindow(result.Release);
            return;
        }

        if (_updateWindow is { IsLoaded: true })
        {
            if (!_updateWindow.IsBusy)
            {
                _updateWindow.ShowCheckResult(result);
            }
            if (_updateWindow.WindowState == WindowState.Minimized)
            {
                _updateWindow.WindowState = WindowState.Normal;
            }
            _updateWindow.Activate();
            return;
        }

        _updateWindow = new UpdateWindow(this, result);
        _updateWindow.Closed += (_, _) => _updateWindow = null;
        _updateWindow.Show();
        _updateWindow.Activate();
        AppLog.Info($"update status window opened (status={result.Status})");
    }

    public void SkipUpdateVersion(string version)
    {
        Settings.SkippedUpdateVersion = version;
        Settings.Save();
        AppLog.Info($"skipped update version v{version}");
    }

    private async Task RunStartupAgentDetectionAsync()
    {
        try
        {
            await Task.Delay(TimeSpan.FromMilliseconds(1800), _shutdownCts.Token).ConfigureAwait(false);
            var candidates = SetupManager.GetDetectedUnconnectedAgents(Settings.DismissedAutoConnectAgents);
            if (candidates.Count == 0) return;

            AppLog.Info($"startup agent detection found {candidates.Count} unconnected agent(s): {string.Join(", ", candidates.Select(c => c.Agent.CliKey))}");
            await Dispatcher.InvokeAsync(() => ShowAgentConnectWindow(candidates));
        }
        catch (OperationCanceledException)
        {
            // App shutting down before startup detection ran.
        }
        catch (Exception ex)
        {
            AppLog.Warn($"startup agent detection failed: {ex.Message}");
        }
    }

    public bool PromptConnectDetectedAgents(bool includeDismissed = true)
    {
        var candidates = SetupManager.GetDetectedUnconnectedAgents(
            includeDismissed ? null : Settings.DismissedAutoConnectAgents);
        if (candidates.Count == 0) return false;
        ShowAgentConnectWindow(candidates);
        return true;
    }

    public void ShowAgentConnectWindow(IReadOnlyList<AgentInstallState> candidates)
    {
        if (candidates.Count == 0) return;

        if (_agentConnectWindow is { IsLoaded: true })
        {
            _agentConnectWindow.LoadCandidates(candidates);
            if (_agentConnectWindow.WindowState == WindowState.Minimized)
                _agentConnectWindow.WindowState = WindowState.Normal;
            _agentConnectWindow.Activate();
            return;
        }

        _agentConnectWindow = new AgentConnectWindow(this, candidates);
        _agentConnectWindow.Closed += (_, _) => _agentConnectWindow = null;
        _agentConnectWindow.Show();
        _agentConnectWindow.Activate();
        AppLog.Info($"agent connect confirmation dialog opened for {candidates.Count} agent(s)");
    }

    public void DismissAutoConnectAgents(IEnumerable<string> cliKeys)
    {
        var set = new HashSet<string>(
            Settings.DismissedAutoConnectAgents ?? new List<string>(),
            StringComparer.OrdinalIgnoreCase);
        foreach (var k in cliKeys)
        {
            if (!string.IsNullOrWhiteSpace(k)) set.Add(k.Trim().ToLowerInvariant());
        }
        Settings.DismissedAutoConnectAgents = set.OrderBy(x => x, StringComparer.OrdinalIgnoreCase).ToList();
        Settings.Save();
        AppLog.Info($"dismissed auto-connect prompt for: {string.Join(", ", Settings.DismissedAutoConnectAgents)}");
    }

    public void ClearDismissedAutoConnectAgents(IEnumerable<string> cliKeys)
    {
        if (Settings.DismissedAutoConnectAgents is null || Settings.DismissedAutoConnectAgents.Count == 0) return;
        var toRemove = new HashSet<string>(cliKeys, StringComparer.OrdinalIgnoreCase);
        var before = Settings.DismissedAutoConnectAgents.Count;
        Settings.DismissedAutoConnectAgents.RemoveAll(k => toRemove.Contains(k));
        if (Settings.DismissedAutoConnectAgents.Count != before)
        {
            Settings.Save();
        }
    }

    public void NotifyAgentInstallStateChanged()
    {
        try
        {
            AgentInstallStateChanged?.Invoke();
        }
        catch (Exception ex)
        {
            AppLog.Warn($"AgentInstallStateChanged subscriber error: {ex.Message}");
        }
    }

    public void ShutdownForUpdate(PreparedUpdate prepared)
    {
        ArgumentNullException.ThrowIfNull(prepared);
        if (_isShuttingDown) return;

        // Validate, write apply-update.ps1, and spawn the detached PowerShell helper BEFORE
        // tearing down the app. The helper waits for our PID to exit before touching app files,
        // so if script creation or process launch fails the running app remains completely intact.
        Updater.LaunchUpdateInstaller(prepared, Environment.ProcessId);

        _isShuttingDown = true;
        AppLog.Info($"==== Open Island shutting down for update to v{prepared.Release.Version} ====");
        TearDownResources();
        Shutdown();
    }

    private static readonly Dictionary<string, SoundPlayer> WavPlayers = new(StringComparer.OrdinalIgnoreCase);
    private static readonly object WavLock = new();

    /// <summary>
    /// Plays the configured notification sound via the 48 kHz 16-bit Stereo PCM
    /// <see cref="NotificationSoundEngine"/> acoustic chime synthesizer.
    /// <paramref name="soundOverride"/> lets the Settings "preview" button audition
    /// the currently selected (not yet saved) sound scheme.
    /// </summary>
    public void PlayNotificationSound(bool isUrgent, string? soundOverride = null, bool ignoreEnabled = false)
    {
        if (!ignoreEnabled && !Settings.SoundEnabled) return;

        var sound = string.IsNullOrWhiteSpace(soundOverride) ? Settings.NotificationSound : soundOverride;
        var scheme = NotificationSoundEngine.NormalizeScheme(sound);
        var cacheKey = $"{scheme}:{(isUrgent ? "urgent" : "normal")}";

        try
        {
            SoundPlayer player;
            lock (WavLock)
            {
                if (!WavPlayers.TryGetValue(cacheKey, out player!))
                {
                    var wavBytes = NotificationSoundEngine.GetWavBytes(scheme, isUrgent);
                    player = new SoundPlayer(new MemoryStream(wavBytes, writable: false));
                    player.Load();
                    WavPlayers[cacheKey] = player;
                }
            }
            player.Play();
        }
        catch
        {
            try { SystemSounds.Asterisk.Play(); } catch { }
        }
    }

    private void SetupTrayIcon()
    {
        _trayIcon = new Forms.NotifyIcon
        {
            Icon = CreateProgrammaticTrayIcon(),
            Text = "Open Island · AI Coding Agent Control Center",
            Visible = true
        };
        _trayIcon.DoubleClick += (_, _) => Dispatcher.Invoke(() => Island?.ToggleVisibility());
        RebuildTrayMenu();
    }

    private void RebuildTrayMenu()
    {
        if (_trayIcon == null) return;
        var menu = new Forms.ContextMenuStrip();
        menu.Items.Add(Strings.Get("ShowHideIsland"), null, (_, _) => Dispatcher.Invoke(() => Island?.ToggleVisibility()));
        menu.Items.Add(Strings.Get("Refresh"), null, (_, _) => RunDiscoveryPass());
        menu.Items.Add(Settings.SoundEnabled ? Strings.Get("Mute") : Strings.Get("Unmute"), null, (_, _) =>
        {
            Settings.SoundEnabled = !Settings.SoundEnabled;
            Settings.Save();
            RebuildTrayMenu();
        });
        menu.Items.Add(new Forms.ToolStripSeparator());
        menu.Items.Add(Strings.Get("Settings"), null, (_, _) => Dispatcher.Invoke(OpenSettings));
        menu.Items.Add(Strings.Get("CheckForUpdates"), null, (_, _) => Dispatcher.Invoke(() => _ = TriggerTrayUpdateCheckAsync()));
        menu.Items.Add(Strings.Get("OpenLogs"), null, (_, _) => OpenLogFolder());
        menu.Items.Add(new Forms.ToolStripSeparator());
        menu.Items.Add(Strings.Get("Quit"), null, (_, _) => Dispatcher.Invoke(ExitApplication));
        _trayIcon.ContextMenuStrip = menu;
    }

    private static Drawing.Icon CreateProgrammaticTrayIcon()
    {
        using var bmp = new Drawing.Bitmap(32, 32);
        using var g = Drawing.Graphics.FromImage(bmp);
        g.SmoothingMode = Drawing.Drawing2D.SmoothingMode.AntiAlias;
        g.Clear(Drawing.Color.Transparent);

        using var pillBrush = new Drawing.SolidBrush(Drawing.Color.FromArgb(255, 18, 20, 24));
        using var borderPen = new Drawing.Pen(Drawing.Color.FromArgb(255, 148, 163, 184), 2f);
        g.FillEllipse(pillBrush, 2, 7, 28, 18);
        g.DrawEllipse(borderPen, 2, 7, 28, 18);

        using var dotBrush = new Drawing.SolidBrush(Drawing.Color.FromArgb(255, 16, 185, 129));
        g.FillEllipse(dotBrush, 12, 12, 8, 8);

        return Drawing.Icon.FromHandle(bmp.GetHicon());
    }

    private void TearDownResources()
    {
        try { _shutdownCts.Cancel(); } catch { }
        SettingsThemeManager.Shutdown();
        _pollTimer?.Stop();
        _selfTestTimer?.Stop();
        SessionPersistence.Save(Reducer.GetActiveSessions());
        Bridge?.Dispose();
        CodexServer?.Dispose();
        if (_trayIcon != null)
        {
            _trayIcon.Visible = false;
            _trayIcon.Dispose();
            _trayIcon = null;
        }
        if (_singleInstanceMutex != null)
        {
            try { _singleInstanceMutex.ReleaseMutex(); } catch { }
            _singleInstanceMutex.Dispose();
            _singleInstanceMutex = null;
        }
        Updater?.Dispose();
    }

    private void ExitApplication()
    {
        if (_isShuttingDown) return;
        _isShuttingDown = true;
        AppLog.Info("==== Open Island shutting down ====");
        TearDownResources();
        Shutdown();
    }
}

