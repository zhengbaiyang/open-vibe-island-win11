using System.Text.Json;

namespace OpenIsland.Core.Models;

public enum IslandEdgePosition
{
    TopCenter,
    BottomCenter
}

public enum IslandThemeStyle
{
    PureBlack,
    Graphite
}

public enum SettingsAppearanceMode
{
    System,
    Light,
    Dark
}

public sealed class AppSettings
{
    public string Language { get; set; } = "zh-CN"; // "zh-CN" or "en"
    public IslandEdgePosition EdgePosition { get; set; } = IslandEdgePosition.TopCenter;
    public IslandThemeStyle ThemeStyle { get; set; } = IslandThemeStyle.PureBlack;
    public SettingsAppearanceMode SettingsAppearance { get; set; } = SettingsAppearanceMode.System;
    public int MonitorIndex { get; set; } = 0;
    public bool SoundEnabled { get; set; } = true;
    /// <summary>
    /// Notification sound name. Built-ins: Asterisk, Exclamation, Beep, Hand,
    /// Notify, Ding, Chimes, Tada, WindowsNotify.
    /// </summary>
    public string NotificationSound { get; set; } = "Asterisk";
    public bool AutoExpandOnApproval { get; set; } = true;
    public bool AutoExpandOnCompletion { get; set; } = true;
    /// <summary>
    /// Merge recurring "new chat" sessions (e.g. Codex scheduled automations that
    /// open a fresh thread every cycle) into the most recent inactive session with
    /// the same agent + working directory instead of piling up rows in the island.
    /// </summary>
    public bool CoalesceRecurringSessions { get; set; } = true;
    public bool LaunchAtStartup { get; set; } = false;
    public bool AutoCheckForUpdates { get; set; } = true;
    public string? SkippedUpdateVersion { get; set; }
    /// <summary>
    /// Tuck the island into the screen edge after a period without pointer interaction
    /// (it slides back in when the pointer reaches that edge).
    /// </summary>
    public bool AutoHideIsland { get; set; } = true;
    /// <summary>Seconds without pointer interaction before the island auto-hides (5-300).</summary>
    public int AutoHideDelaySeconds { get; set; } = 30;
    public bool EnableCodexAppServer { get; set; } = true;
    public bool AutoInstallOpenCodePlugin { get; set; } = false;
    public bool AutoDetectAgentsOnStartup { get; set; } = true;
    public List<string> DismissedAutoConnectAgents { get; set; } = new();
    public int PollIntervalMs { get; set; } = 2000;
    public string ToggleHotkey { get; set; } = "Ctrl+Alt+I";
    public int CompletionCardSeconds { get; set; } = 8;

    public static AppSettings Load()
    {
        try
        {
            if (File.Exists(BridgeConstants.SettingsPath))
            {
                var json = File.ReadAllText(BridgeConstants.SettingsPath);
                var loaded = JsonSerializer.Deserialize<AppSettings>(json, BridgeConstants.JsonOptions);
                if (loaded is not null) return loaded;
            }
        }
        catch
        {
            // Fall back to defaults on corrupt settings file
        }
        return new AppSettings();
    }

    public void Save()
    {
        try
        {
            Directory.CreateDirectory(BridgeConstants.AppDataDir);
            var json = JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true });
            File.WriteAllText(BridgeConstants.SettingsPath, json);
        }
        catch
        {
            // Ignore write errors
        }
    }
}
