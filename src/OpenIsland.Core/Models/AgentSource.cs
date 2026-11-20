namespace OpenIsland.Core.Models;

/// <summary>
/// All 15+ AI coding agents supported by Open Island (ported from Octane0411/open-vibe-island).
/// </summary>
public enum AgentSource
{
    Claude,
    Codex,
    OpenCode,
    Cursor,
    Gemini,
    Kimi,
    Grok,
    Hermes,
    Pi,
    OhMyPi,
    Qoder,
    Qwen,
    Factory,
    CodeBuddy,
    ZCode,
    DeepSeekHarness,
    Unknown
}

public sealed record AgentMetadata(
    AgentSource Source,
    string CliKey,
    string DisplayName,
    string AccentHex,
    string ConfigPathTemplate,
    bool SupportsBlockingApproval,
    bool IsClaudeFork = false,
    bool IsExtensionBased = false,
    bool IsAutoDetected = false);

public static class AgentRegistry
{
    public static readonly IReadOnlyList<AgentMetadata> All = new List<AgentMetadata>
    {
        new(AgentSource.Claude,    "claude",    "Claude Code",      "#D97757", @"~/.claude/settings.json",                   true),
        new(AgentSource.Codex,     "codex",     "Codex",            "#10A37F", @"~/.codex/config.toml",                      true),
        new(AgentSource.OpenCode,  "opencode",  "OpenCode",         "#6366F1", @"~/.config/opencode/plugins/open-island.js", true,  IsExtensionBased: true),
        new(AgentSource.Cursor,    "cursor",    "Cursor",           "#38BDF8", @"~/.cursor/hooks.json",                      false),
        new(AgentSource.Gemini,    "gemini",    "Gemini CLI",       "#4285F4", @"~/.gemini/settings.json",                   false),
        new(AgentSource.Kimi,      "kimi",      "Kimi CLI",         "#8B5CF6", @"~/.kimi/config.toml",                       true),
        new(AgentSource.Grok,      "grok",      "Grok Build",       "#F59E0B", @"~/.grok/hooks/open-island.json",            false),
        new(AgentSource.Hermes,    "hermes",    "Hermes Agent",     "#EC4899", @"~/.hermes/config.yaml",                     false),
        new(AgentSource.Pi,        "pi",        "Pi",               "#14B8A6", @"~/.pi/agent/extensions/open-island.ts",     false, IsExtensionBased: true),
        new(AgentSource.OhMyPi,    "omp",       "Oh My Pi (OMP)",   "#06B6D4", @"~/.omp/agent/extensions/open-island.ts",    false, IsExtensionBased: true),
        new(AgentSource.Qoder,     "qoder",     "Qoder",            "#22C55E", @"~/.qoder/settings.json",                    true,  IsClaudeFork: true),
        new(AgentSource.Qwen,      "qwen",      "Qwen Code",        "#7C3AED", @"~/.qwen/settings.json",                     true,  IsClaudeFork: true),
        new(AgentSource.Factory,   "factory",   "Factory",          "#F97316", @"~/.factory/settings.json",                  true,  IsClaudeFork: true),
        new(AgentSource.CodeBuddy, "codebuddy", "CodeBuddy",        "#3B82F6", @"~/.codebuddy/settings.json",                true,  IsClaudeFork: true),
        new(AgentSource.ZCode,     "zcode",     "ZCode Desktop",    "#0EA5E9", @"~/.zcode/cli/config.json",                  false),
        // DeepSeek Harness (DSH) has no hook system: sessions are auto-discovered from
        // its local session cache, so it is display-only (no install/uninstall action).
        new(AgentSource.DeepSeekHarness, "dsh", "DeepSeek Harness", "#4D6BFE", @"~/.dsh/storages/session_projcache/sessions", false, IsAutoDetected: true)
    };

    private static readonly Dictionary<AgentSource, AgentMetadata> BySource =
        All.ToDictionary(x => x.Source);

    public static AgentMetadata Get(AgentSource source) =>
        BySource.TryGetValue(source, out var meta)
            ? meta
            : new AgentMetadata(AgentSource.Unknown, "unknown", "Agent", "#94A3B8", "", false);

    public static AgentSource Parse(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return AgentSource.Unknown;
        return value.Trim().ToLowerInvariant() switch
        {
            "claude" or "claude-code" or "claudecode" => AgentSource.Claude,
            "codex" or "codex-cli" or "codex-desktop" => AgentSource.Codex,
            "opencode" => AgentSource.OpenCode,
            "cursor" => AgentSource.Cursor,
            "gemini" or "gemini-cli" => AgentSource.Gemini,
            "kimi" or "kimi-cli" => AgentSource.Kimi,
            "grok" or "grok-build" => AgentSource.Grok,
            "hermes" or "hermes-agent" => AgentSource.Hermes,
            "pi" => AgentSource.Pi,
            "omp" or "oh-my-pi" or "ohmypi" => AgentSource.OhMyPi,
            "qoder" => AgentSource.Qoder,
            "qwen" or "qwen-code" => AgentSource.Qwen,
            "factory" => AgentSource.Factory,
            "codebuddy" => AgentSource.CodeBuddy,
            "zcode" => AgentSource.ZCode,
            "dsh" or "deepseek" or "deepseek-harness" or "deepseekharness" => AgentSource.DeepSeekHarness,
            _ => AgentSource.Unknown
        };
    }

    public static string ExpandHomePath(string pathTemplate)
    {
        // OPEN_ISLAND_FAKE_HOME lets tests / sandboxes redirect agent config paths
        // without touching the real user profile.
        var fakeHome = Environment.GetEnvironmentVariable("OPEN_ISLAND_FAKE_HOME");
        var home = !string.IsNullOrWhiteSpace(fakeHome)
            ? fakeHome
            : Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        if (pathTemplate.StartsWith("~/") || pathTemplate.StartsWith(@"~\"))
        {
            var rel = pathTemplate[2..].Replace('/', Path.DirectorySeparatorChar);
            return Path.Combine(home, rel);
        }
        return pathTemplate;
    }
}
