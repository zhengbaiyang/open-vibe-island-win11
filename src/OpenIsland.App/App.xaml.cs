using System.Diagnostics;
using System.IO;
using System.Media;
using System.Reflection;
using System.Windows;
using System.Windows.Threading;
using OpenIsland.Core.Diagnostics;
using OpenIsland.Core.Discovery;
using OpenIsland.Core.Hooks;
using OpenIsland.Core.IPC;
using OpenIsland.Core.Localization;
using OpenIsland.Core.Models;
using OpenIsland.Core.Setup;
using OpenIsland.Core.State;
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

    public AppSettings Settings { get; private set; } = new();
    public SessionStateReducer Reducer { get; } = new();
    public BridgeServer Bridge { get; private set; } = null!;
    public TranscriptDiscovery Discovery { get; } = new();
    public HookInstallManager SetupManager { get; } = new();
    public CodexAppServerClient? CodexServer { get; private set; }
    public IslandWindow? Island { get; private set; }
    private SettingsWindow? _settingsWindow;

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
        Reducer.CoalesceRecurringSessions = Settings.CoalesceRecurringSessions;

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

        // Auto-install OpenCode plugin on first launch if ~/.config/opencode exists
        if (Settings.AutoInstallOpenCodePlugin)
        {
            var openCodeDir = AgentRegistry.ExpandHomePath("~/.config/opencode");
            if (Directory.Exists(openCodeDir))
            {
                var st = SetupManager.GetStatus(AgentRegistry.Get(AgentSource.OpenCode));
                if (st.Status == HookInstallStatus.NotInstalled)
                {
                    AppLog.Info("auto-installing OpenCode plugin");
                    SetupManager.Install(AgentSource.OpenCode);
                }
            }
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
                var discovered = Discovery.ScanRecentSessions();
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
        Reducer.CoalesceRecurringSessions = updated.CoalesceRecurringSessions;
        updated.Save();
        AppLog.Info($"settings saved: lang={updated.Language} edge={updated.EdgePosition} theme={updated.ThemeStyle} monitor={updated.MonitorIndex}");

        if (_pollTimer != null)
        {
            _pollTimer.Interval = TimeSpan.FromMilliseconds(Math.Clamp(updated.PollIntervalMs, 1000, 15000));
        }

        Island?.ApplyDisplaySettings();
        RebuildTrayMenu();
    }

    private static readonly Dictionary<string, SoundPlayer> WavPlayers = new(StringComparer.OrdinalIgnoreCase);
    private static readonly object WavLock = new();

    /// <summary>
    /// Plays the configured notification sound.
    /// The same sound scheme is used for approvals, questions and completions so the
    /// choice made in Settings is honoured everywhere. <paramref name="soundOverride"/>
    /// lets the Settings "preview" button audition the currently selected (not yet saved) sound.
    /// </summary>
    public void PlayNotificationSound(bool isUrgent, string? soundOverride = null, bool ignoreEnabled = false)
    {
        if (!ignoreEnabled && !Settings.SoundEnabled) return;

        var sound = string.IsNullOrWhiteSpace(soundOverride) ? Settings.NotificationSound : soundOverride;
        try
        {
            switch ((sound ?? "").Trim())
            {
                case "Exclamation": SystemSounds.Exclamation.Play(); break;
                case "Beep": SystemSounds.Beep.Play(); break;
                case "Hand": SystemSounds.Hand.Play(); break;
                case "Notify": PlayWav("notify.wav"); break;
                case "Ding": PlayWav("ding.wav"); break;
                case "Chimes": PlayWav("chimes.wav"); break;
                case "Tada": PlayWav("tada.wav"); break;
                case "WindowsNotify": PlayWav("Windows Notify System Generic.wav"); break;
                case "WindowsExclamation": PlayWav("Windows Exclamation.wav"); break;
                case "Asterisk":
                default: SystemSounds.Asterisk.Play(); break;
            }
        }
        catch { }
    }

    private static void PlayWav(string fileName)
    {
        try
        {
            var path = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.Windows), "Media", fileName);
            if (!File.Exists(path))
            {
                SystemSounds.Asterisk.Play();
                return;
            }

            SoundPlayer player;
            lock (WavLock)
            {
                if (!WavPlayers.TryGetValue(path, out player!))
                {
                    player = new SoundPlayer(path);
                    player.Load();
                    WavPlayers[path] = player;
                }
            }
            player.Play();
        }
        catch
        {
            SystemSounds.Asterisk.Play();
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

    private void ExitApplication()
    {
        AppLog.Info("==== Open Island shutting down ====");
        SessionPersistence.Save(Reducer.GetActiveSessions());
        _pollTimer?.Stop();
        Bridge?.Dispose();
        CodexServer?.Dispose();
        if (_trayIcon != null)
        {
            _trayIcon.Visible = false;
            _trayIcon.Dispose();
        }
        _singleInstanceMutex?.Dispose();
        Shutdown();
    }
}

