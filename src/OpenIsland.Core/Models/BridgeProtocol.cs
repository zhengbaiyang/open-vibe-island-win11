using System.Text.Json;
using System.Text.Json.Serialization;

namespace OpenIsland.Core.Models;

/// <summary>
/// Wire envelope sent from OpenIslandHooks.exe (or OpenCode/Pi plugins) to BridgeServer over Windows Named Pipe.
/// </summary>
public sealed class BridgeEnvelope
{
    [JsonPropertyName("source")]
    public string Source { get; set; } = "unknown";

    [JsonPropertyName("raw_payload")]
    public string RawPayload { get; set; } = "{}";

    [JsonPropertyName("cwd")]
    public string? Cwd { get; set; }

    [JsonPropertyName("pid")]
    public int? ProcessId { get; set; }

    [JsonPropertyName("parent_pid")]
    public int? ParentProcessId { get; set; }

    [JsonPropertyName("terminal_app")]
    public string? TerminalApp { get; set; }

    [JsonPropertyName("terminal_session_id")]
    public string? TerminalSessionId { get; set; }

    [JsonPropertyName("terminal_title")]
    public string? TerminalTitle { get; set; }

    [JsonPropertyName("window_handle")]
    public long? WindowHandle { get; set; }

    [JsonPropertyName("is_desktop_app")]
    public bool IsDesktopApp { get; set; }

    [JsonPropertyName("desktop_app_name")]
    public string? DesktopAppName { get; set; }

    [JsonPropertyName("expects_directive")]
    public bool ExpectsDirective { get; set; }

    [JsonPropertyName("timeout_seconds")]
    public int TimeoutSeconds { get; set; } = 45;

    [JsonPropertyName("sent_at")]
    public DateTimeOffset SentAt { get; set; } = DateTimeOffset.UtcNow;
}

public sealed class BridgeResponse
{
    [JsonPropertyName("ok")]
    public bool Ok { get; set; } = true;

    /// <summary>
    /// Raw JSON string to be written verbatim to stdout by OpenIslandHooks.exe (null/empty = no stdout).
    /// </summary>
    [JsonPropertyName("stdout_directive")]
    public string? StdoutDirective { get; set; }

    /// <summary>
    /// Structured decision for extension-based clients (e.g. OpenCode JS plugin).
    /// </summary>
    [JsonPropertyName("decision")]
    public string? Decision { get; set; }

    [JsonPropertyName("reason")]
    public string? Reason { get; set; }

    [JsonPropertyName("answers")]
    public Dictionary<string, string>? Answers { get; set; }
}

public static class BridgeConstants
{
    /// <summary>
    /// Optional instance suffix (env var <c>OPEN_ISLAND_INSTANCE</c>) so multiple development
    /// instances can run side by side with isolated pipe / mutex / data directory.
    /// </summary>
    public static string InstanceSuffix
    {
        get
        {
            var raw = Environment.GetEnvironmentVariable("OPEN_ISLAND_INSTANCE");
            if (string.IsNullOrWhiteSpace(raw)) return "";
            var clean = new string(raw.Where(char.IsLetterOrDigit).ToArray());
            return clean.Length == 0 ? "" : clean.ToLowerInvariant();
        }
    }

    public const string DefaultPipeName = "open-island-bridge";

    public static string PipeName =>
        InstanceSuffix.Length == 0 ? DefaultPipeName : $"{DefaultPipeName}-{InstanceSuffix}";

    public static string FullPipePath => $@"\\.\pipe\{PipeName}";

    public static string MutexName =>
        InstanceSuffix.Length == 0
            ? @"Local\OpenIslandWin11SingleInstance"
            : $@"Local\OpenIslandWin11SingleInstance-{InstanceSuffix}";

    /// <summary>
    /// Data directory (settings, session cache, logs). Override with
    /// <c>OPEN_ISLAND_DATA_DIR</c> for tests / portable mode.
    /// </summary>
    public static string AppDataDir
    {
        get
        {
            var raw = Environment.GetEnvironmentVariable("OPEN_ISLAND_DATA_DIR");
            if (!string.IsNullOrWhiteSpace(raw)) return raw;
            return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "OpenIsland");
        }
    }

    public static string LogsDir => Path.Combine(AppDataDir, "logs");
    public static string LogPath => Path.Combine(LogsDir, "open-island.log");
    public static string BridgeLogPath => Path.Combine(LogsDir, "bridge.log");

    public static string InstalledHooksExePath =>
        Path.Combine(AppDataDir, "bin", "OpenIslandHooks.exe");

    public static string SessionsCachePath =>
        Path.Combine(AppDataDir, "sessions.json");

    public static string SettingsPath =>
        Path.Combine(AppDataDir, "settings.json");

    public static string ClaudeRateLimitCachePath =>
        Path.Combine(Path.GetTempPath(), "open-island-rl.json");

    public static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        WriteIndented = false,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };
}
