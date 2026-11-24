using System.Text.Json;
using System.Text.RegularExpressions;
using OpenIsland.Core.Models;

namespace OpenIsland.Core.Discovery;

public enum AntigravityExecutionMode
{
    Unknown,
    RequestReview,
    Default,
    Auto,
    Turbo
}

public enum AntigravityReviewPolicy
{
    Unknown,
    RequestReview,
    AgentDecides,
    AlwaysProceed
}

public sealed class AntigravityEffectivePolicy
{
    public AntigravityExecutionMode ExecutionMode { get; set; } = AntigravityExecutionMode.Unknown;
    public AntigravityReviewPolicy ReviewPolicy { get; set; } = AntigravityReviewPolicy.Unknown;
    public List<string> AllowCommandPatterns { get; } = new();
    public List<string> AskCommandPatterns { get; } = new();
    public List<string> DenyCommandPatterns { get; } = new();

    public AntigravityEffectivePolicy Clone()
    {
        var copy = new AntigravityEffectivePolicy
        {
            ExecutionMode = ExecutionMode,
            ReviewPolicy = ReviewPolicy
        };
        copy.AllowCommandPatterns.AddRange(AllowCommandPatterns);
        copy.AskCommandPatterns.AddRange(AskCommandPatterns);
        copy.DenyCommandPatterns.AddRange(DenyCommandPatterns);
        return copy;
    }
}

/// <summary>
/// Reads Google Antigravity permission settings (Auto / Turbo / Default / RequestReview,
/// review policies, and allow/ask/deny rules) and determines whether a transcript-observed
/// tool call is genuinely awaiting user input or handled automatically by Antigravity.
/// </summary>
public sealed class AntigravityPolicyDetector
{
    private static readonly TimeSpan CacheTtl = TimeSpan.FromSeconds(3);
    private static readonly JsonDocumentOptions LenientJsonOptions = new()
    {
        CommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true
    };

    private static readonly Regex SettingsChangeBlockRegex = new(
        @"<USER_SETTINGS_CHANGE>(?<body>[\s\S]*?)</USER_SETTINGS_CHANGE>",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex SettingsChangeSentenceRegex = new(
        @"The user changed setting\s+`(?<name>[^`]+)`\s+from\s+.+?\s+to\s+(?<newValue>[^.\r\n]+)",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex CommandWrapperRegex = new(
        @"^(?<action>[a-zA-Z0-9_.-]+)\((?<target>.*)\)$",
        RegexOptions.Singleline | RegexOptions.Compiled);

    private static readonly Regex ChainSplitRegex = new(
        @"\s*(?:&&|\|\||;|\|)\s*",
        RegexOptions.Compiled);

    private static readonly Regex HighRiskCommandRegex = new(
        @"(?i)(?:^|[\s;&|`])(?:" +
        @"rm\s+(?:-[a-z]*r[a-z]*f|-[a-z]*f[a-z]*r)\b|" +
        @"remove-item\b[^;\r\n|]*(?:-recurse|-force)\b|" +
        @"(?:del|rmdir|rd)\s+[^;\r\n|]*(?:/s\b|/q\b)|" +
        @"git\s+push\s+[^;\r\n|]*(?:--force\b|-f\b)|" +
        @"git\s+reset\s+--hard\b|" +
        @"git\s+clean\s+-[a-z]*f|" +
        @"diskpart\b|" +
        @"bcdedit\b|" +
        @"reg\s+delete\b|" +
        @"shutdown\b|" +
        @"restart-computer\b|" +
        @"stop-computer\b|" +
        @"drop\s+(?:database|table)\b|" +
        @"truncate\s+table\b|" +
        @"icacls\b|" +
        @"takeown\b|" +
        @"set-executionpolicy\b|" +
        @"chmod\s+777\b|" +
        @"mkfs(?:\.[a-z0-9]+)?\b|" +
        @"dd\s+if=|" +
        @"invoke-expression\b|" +
        @"\|\s*(?:sh|bash|pwsh|powershell|iex)\b" +
        @")|" +
        @"(?i)(?:^|[;&|]\s*)format\s+[a-z0-9/:\\-]",
        RegexOptions.Compiled);

    private readonly object _cacheLock = new();
    private readonly Dictionary<string, (DateTimeOffset CachedAt, AntigravityEffectivePolicy Policy)> _cache =
        new(StringComparer.OrdinalIgnoreCase);
    private string _cachedFakeHome = "";

    public AntigravityEffectivePolicy ResolvePolicy(string surfaceName, string? sessionCwd = null)
    {
        var fakeHome = Environment.GetEnvironmentVariable("OPEN_ISLAND_FAKE_HOME") ?? "";
        var normCwd = NormalizePath(sessionCwd) ?? "";
        var cacheKey = $"{surfaceName}|{normCwd}";
        var now = DateTimeOffset.UtcNow;

        lock (_cacheLock)
        {
            if (!string.Equals(_cachedFakeHome, fakeHome, StringComparison.Ordinal))
            {
                _cache.Clear();
                _cachedFakeHome = fakeHome;
            }
            else if (_cache.TryGetValue(cacheKey, out var entry) && (now - entry.CachedAt) < CacheTtl)
            {
                return entry.Policy.Clone();
            }
        }

        var policy = LoadPolicyFromDisk(surfaceName, normCwd, !string.IsNullOrWhiteSpace(fakeHome));

        lock (_cacheLock)
        {
            _cache[cacheKey] = (now, policy.Clone());
        }

        return policy;
    }

    public void ApplyTranscriptSettingsChange(AntigravityEffectivePolicy policy, string? rawContent)
    {
        if (string.IsNullOrWhiteSpace(rawContent)) return;
        if (!rawContent.Contains("<USER_SETTINGS_CHANGE>", StringComparison.OrdinalIgnoreCase)) return;

        foreach (Match block in SettingsChangeBlockRegex.Matches(rawContent))
        {
            var body = block.Groups["body"].Value;
            foreach (Match sentence in SettingsChangeSentenceRegex.Matches(body))
            {
                var settingName = sentence.Groups["name"].Value.Trim();
                var newValue = sentence.Groups["newValue"].Value.Trim().Trim('`', '"', '\'');
                if (string.IsNullOrWhiteSpace(settingName) || string.IsNullOrWhiteSpace(newValue)) continue;
                if (!IsRelevantPolicySettingName(settingName)) continue;

                var lowerName = settingName.ToLowerInvariant();
                if (lowerName.Contains("review", StringComparison.Ordinal) ||
                    lowerName.Contains("artifact", StringComparison.Ordinal) ||
                    lowerName.Contains("plan", StringComparison.Ordinal))
                {
                    var rev = ParseReviewPolicy(newValue);
                    if (rev != AntigravityReviewPolicy.Unknown)
                    {
                        policy.ReviewPolicy = rev;
                        continue;
                    }
                }

                var exec = ParseExecutionMode(newValue);
                if (exec != AntigravityExecutionMode.Unknown)
                {
                    policy.ExecutionMode = exec;
                }
                else
                {
                    var rev = ParseReviewPolicy(newValue);
                    if (rev != AntigravityReviewPolicy.Unknown)
                    {
                        policy.ReviewPolicy = rev;
                    }
                }
            }
        }
    }

    public bool ShouldSurfaceCommandApproval(
        AntigravityEffectivePolicy policy,
        string? commandLine,
        int? waitMsBeforeAsync,
        bool hasExplicitWaitingStatus,
        double idleSeconds)
    {
        if (hasExplicitWaitingStatus) return true;

        if (MatchesCommandRule(commandLine, policy.DenyCommandPatterns, matchChainedSegments: true))
        {
            return false;
        }

        bool matchesAsk = MatchesCommandRule(commandLine, policy.AskCommandPatterns, matchChainedSegments: true);
        if (!matchesAsk && MatchesCommandRule(commandLine, policy.AllowCommandPatterns, matchChainedSegments: false))
        {
            return false;
        }

        if (policy.ExecutionMode == AntigravityExecutionMode.Turbo && !matchesAsk)
        {
            return false;
        }

        double waitSec = waitMsBeforeAsync.HasValue
            ? Math.Clamp(waitMsBeforeAsync.Value / 1000.0, 0.5, 10.0)
            : 6.0;

        double requiredSettleSeconds;
        if (matchesAsk || IsClearlyHighRiskCommand(commandLine))
        {
            requiredSettleSeconds = 5.0;
        }
        else if (policy.ExecutionMode is AntigravityExecutionMode.RequestReview or AntigravityExecutionMode.Default)
        {
            requiredSettleSeconds = 5.0;
        }
        else
        {
            // Auto or Unknown: allow time for Antigravity's safety classifier, shell startup,
            // and synchronous WaitMsBeforeAsync window before concluding a command is awaiting approval.
            requiredSettleSeconds = Math.Max(12.0, waitSec + 9.0);
        }

        return idleSeconds >= requiredSettleSeconds;
    }

    public bool ShouldSurfacePlanFeedback(
        AntigravityEffectivePolicy policy,
        string requestId,
        string? lastType,
        string? lastStatus,
        bool lastHasToolCalls,
        bool hasExplicitWaitingStatus,
        double idleSeconds)
    {
        if (hasExplicitWaitingStatus) return true;

        if (policy.ReviewPolicy == AntigravityReviewPolicy.AlwaysProceed ||
            policy.ExecutionMode == AntigravityExecutionMode.Turbo)
        {
            return false;
        }

        if (requestId.StartsWith("agp-", StringComparison.OrdinalIgnoreCase))
        {
            return string.Equals(lastType, "PLANNER_RESPONSE", StringComparison.OrdinalIgnoreCase) &&
                   !lastHasToolCalls &&
                   string.Equals(lastStatus, "DONE", StringComparison.OrdinalIgnoreCase);
        }

        if (requestId.StartsWith("agn-", StringComparison.OrdinalIgnoreCase))
        {
            if (string.Equals(lastType, "GENERIC", StringComparison.OrdinalIgnoreCase) && idleSeconds < 5.0)
            {
                return false;
            }
            return true;
        }

        return true;
    }

    public static bool MatchesCommandRule(
        string? commandLine,
        IReadOnlyList<string> patterns,
        bool matchChainedSegments)
    {
        if (string.IsNullOrWhiteSpace(commandLine) || patterns.Count == 0) return false;

        var normalizedCommand = CollapseWhitespace(commandLine);
        bool hasChaining = ContainsShellChaining(normalizedCommand);

        if (matchChainedSegments)
        {
            if (MatchesAnyPattern(normalizedCommand, patterns, allowWildcards: true, allowTokenPrefix: true))
            {
                return true;
            }

            if (hasChaining)
            {
                foreach (var segment in SplitChainedSegments(normalizedCommand))
                {
                    if (MatchesAnyPattern(segment, patterns, allowWildcards: true, allowTokenPrefix: true))
                    {
                        return true;
                    }
                }
            }

            return false;
        }

        // Allow rules: never let a chained/subshell command ride on a prefix/wildcard rule
        // unless it is "*", an exact literal match, or every simple chained segment matches.
        if (!hasChaining)
        {
            return MatchesAnyPattern(normalizedCommand, patterns, allowWildcards: true, allowTokenPrefix: true);
        }

        foreach (var rawPattern in patterns)
        {
            var pat = CollapseWhitespace(rawPattern);
            if (string.IsNullOrEmpty(pat)) continue;
            if (pat == "*") return true;
            if (!pat.Contains('*', StringComparison.Ordinal) &&
                string.Equals(normalizedCommand, pat, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        if (ContainsSubshellSyntax(normalizedCommand)) return false;

        var segments = SplitChainedSegments(normalizedCommand).ToList();
        return segments.Count > 0 &&
               segments.All(seg => MatchesAnyPattern(seg, patterns, allowWildcards: true, allowTokenPrefix: true));
    }

    public static bool IsClearlyHighRiskCommand(string? commandLine)
    {
        if (string.IsNullOrWhiteSpace(commandLine)) return false;
        return HighRiskCommandRegex.IsMatch(commandLine);
    }

    private static AntigravityEffectivePolicy LoadPolicyFromDisk(string surfaceName, string normCwd, bool fakeHomeActive)
    {
        var policy = new AntigravityEffectivePolicy();

        var candidateFiles = new List<string>
        {
            AgentRegistry.ExpandHomePath("~/.gemini/config/config.json"),
            AgentRegistry.ExpandHomePath("~/.gemini/antigravity/settings.json"),
            AgentRegistry.ExpandHomePath("~/.gemini/antigravity/config.json")
        };

        if (string.Equals(surfaceName, "Antigravity CLI", StringComparison.OrdinalIgnoreCase))
        {
            candidateFiles.Add(AgentRegistry.ExpandHomePath("~/.gemini/antigravity-cli/settings.json"));
        }
        else if (string.Equals(surfaceName, "Antigravity IDE", StringComparison.OrdinalIgnoreCase))
        {
            candidateFiles.Add(AgentRegistry.ExpandHomePath("~/.gemini/antigravity-ide/settings.json"));
            if (!fakeHomeActive)
            {
                var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
                if (!string.IsNullOrWhiteSpace(appData))
                {
                    candidateFiles.Add(Path.Combine(appData, "Antigravity", "User", "settings.json"));
                    candidateFiles.Add(Path.Combine(appData, "Antigravity IDE", "User", "settings.json"));
                }
            }
        }

        foreach (var file in candidateFiles)
        {
            TryApplyJsonFile(file, policy);
        }

        if (!string.IsNullOrWhiteSpace(normCwd))
        {
            ApplyMatchingProjectOverrides(normCwd, policy);
        }

        return policy;
    }

    private static void ApplyMatchingProjectOverrides(string normCwd, AntigravityEffectivePolicy policy)
    {
        try
        {
            var projectsDir = AgentRegistry.ExpandHomePath("~/.gemini/config/projects");
            if (!Directory.Exists(projectsDir)) return;

            foreach (var file in Directory.EnumerateFiles(projectsDir, "*.json"))
            {
                try
                {
                    using var doc = JsonDocument.Parse(File.ReadAllText(file), LenientJsonOptions);
                    var root = doc.RootElement;
                    if (root.ValueKind != JsonValueKind.Object) continue;

                    if (ProjectConfigMatchesCwd(root, normCwd))
                    {
                        ApplyJsonObject(root, policy);
                    }
                }
                catch { }
            }
        }
        catch { }
    }

    private static bool ProjectConfigMatchesCwd(JsonElement root, string normCwd)
    {
        foreach (var obj in EnumerateConfigScopes(root))
        {
            foreach (var key in new[] { "workspacePath", "workspace", "rootPath", "cwd", "path", "projectPath" })
            {
                if (obj.TryGetProperty(key, out var val) && val.ValueKind == JsonValueKind.String)
                {
                    var normVal = NormalizePath(val.GetString());
                    if (!string.IsNullOrEmpty(normVal) &&
                        string.Equals(normVal, normCwd, StringComparison.OrdinalIgnoreCase))
                    {
                        return true;
                    }
                }
            }

            if (obj.TryGetProperty("workspacePaths", out var arr) && arr.ValueKind == JsonValueKind.Array)
            {
                foreach (var item in arr.EnumerateArray())
                {
                    if (item.ValueKind != JsonValueKind.String) continue;
                    var normItem = NormalizePath(item.GetString());
                    if (!string.IsNullOrEmpty(normItem) &&
                        string.Equals(normItem, normCwd, StringComparison.OrdinalIgnoreCase))
                    {
                        return true;
                    }
                }
            }
        }

        return false;
    }

    private static void TryApplyJsonFile(string path, AntigravityEffectivePolicy policy)
    {
        try
        {
            if (!File.Exists(path)) return;
            using var doc = JsonDocument.Parse(File.ReadAllText(path), LenientJsonOptions);
            if (doc.RootElement.ValueKind != JsonValueKind.Object) return;
            ApplyJsonObject(doc.RootElement, policy);
        }
        catch { }
    }

    private static IEnumerable<JsonElement> EnumerateConfigScopes(JsonElement root)
    {
        yield return root;

        foreach (var nestedKey in new[] { "userSettings", "agent", "permissions", "cascade" })
        {
            if (root.TryGetProperty(nestedKey, out var child) && child.ValueKind == JsonValueKind.Object)
            {
                yield return child;
                foreach (var subKey in new[] { "agent", "permissions", "cascade" })
                {
                    if (child.TryGetProperty(subKey, out var grandChild) && grandChild.ValueKind == JsonValueKind.Object)
                    {
                        yield return grandChild;
                    }
                }
            }
        }
    }

    private static void ApplyJsonObject(JsonElement root, AntigravityEffectivePolicy policy)
    {
        foreach (var scope in EnumerateConfigScopes(root))
        {
            foreach (var prop in scope.EnumerateObject())
            {
                var normKey = NormalizeSettingKey(prop.Name);

                if (IsExecutionModeKey(normKey))
                {
                    var mode = ParseExecutionModeElement(prop.Value);
                    if (mode != AntigravityExecutionMode.Unknown)
                    {
                        policy.ExecutionMode = mode;
                    }
                }
                else if (IsReviewPolicyKey(normKey))
                {
                    var rev = ParseReviewPolicyElement(prop.Value);
                    if (rev != AntigravityReviewPolicy.Unknown)
                    {
                        policy.ReviewPolicy = rev;
                    }
                }
                else if (normKey is "globalpermissiongrants" or "permissiongrants" or "permissions")
                {
                    if (prop.Value.ValueKind == JsonValueKind.Object)
                    {
                        ExtractGrantsFromObject(prop.Value, policy);
                    }
                }
                else if (normKey is "commandallowlist" or "allowedcommands" or "autoapprovecommands")
                {
                    ExtractCommandList(prop.Value, policy.AllowCommandPatterns, acceptBarePattern: true);
                }
                else if (normKey is "commanddenylist" or "deniedcommands")
                {
                    ExtractCommandList(prop.Value, policy.DenyCommandPatterns, acceptBarePattern: true);
                }
                else if (normKey is "commandasklist" or "askcommands")
                {
                    ExtractCommandList(prop.Value, policy.AskCommandPatterns, acceptBarePattern: true);
                }
            }
        }
    }

    private static void ExtractGrantsFromObject(JsonElement obj, AntigravityEffectivePolicy policy)
    {
        if (obj.TryGetProperty("allow", out var allowEl))
            ExtractCommandList(allowEl, policy.AllowCommandPatterns, acceptBarePattern: false);
        if (obj.TryGetProperty("ask", out var askEl))
            ExtractCommandList(askEl, policy.AskCommandPatterns, acceptBarePattern: false);
        if (obj.TryGetProperty("deny", out var denyEl))
            ExtractCommandList(denyEl, policy.DenyCommandPatterns, acceptBarePattern: false);
    }

    private static void ExtractCommandList(JsonElement el, List<string> target, bool acceptBarePattern)
    {
        if (el.ValueKind != JsonValueKind.Array) return;
        foreach (var item in el.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.String) continue;
            var raw = item.GetString()?.Trim();
            if (string.IsNullOrWhiteSpace(raw)) continue;

            if (raw == "*")
            {
                AddUniquePattern(target, "*");
                continue;
            }

            var match = CommandWrapperRegex.Match(raw);
            if (match.Success)
            {
                var action = match.Groups["action"].Value.Trim().ToLowerInvariant();
                if (action is "command" or "shell" or "bash" or "run_command" or "execute_command" or "terminal")
                {
                    var inner = match.Groups["target"].Value.Trim();
                    if (!string.IsNullOrWhiteSpace(inner))
                    {
                        AddUniquePattern(target, inner);
                    }
                }
                continue;
            }

            if (acceptBarePattern)
            {
                AddUniquePattern(target, raw);
            }
        }
    }

    private static void AddUniquePattern(List<string> list, string pattern)
    {
        if (!list.Contains(pattern, StringComparer.OrdinalIgnoreCase))
        {
            list.Add(pattern);
        }
    }

    private static bool IsExecutionModeKey(string normKey) =>
        normKey is "permissionpreset" or "preset" or "toolexecutionpolicy" or "toolpermission"
            or "autoexecutionpolicy" or "terminalcommandautoexecution" or "terminalautoexecutionpolicy"
            or "cascadecommandsautoexecution" or "commandautoexecution" or "agentmode" or "executionmode";

    private static bool IsReviewPolicyKey(string normKey) =>
        normKey is "artifactreviewpolicy" or "reviewpolicy" or "cascadereviewpolicy" or "planreviewpolicy";

    private static string NormalizeSettingKey(string key)
    {
        var lastDot = key.LastIndexOf('.');
        var leaf = lastDot >= 0 ? key[(lastDot + 1)..] : key;
        return leaf.Trim().ToLowerInvariant().Replace("_", "").Replace("-", "").Replace(" ", "");
    }

    private static AntigravityExecutionMode ParseExecutionModeElement(JsonElement el)
    {
        if (el.ValueKind == JsonValueKind.String)
        {
            return ParseExecutionMode(el.GetString());
        }

        if (el.ValueKind == JsonValueKind.Object)
        {
            foreach (var subKey in new[] { "mode", "preset", "policy", "value" })
            {
                if (el.TryGetProperty(subKey, out var sub) && sub.ValueKind == JsonValueKind.String)
                {
                    var parsed = ParseExecutionMode(sub.GetString());
                    if (parsed != AntigravityExecutionMode.Unknown) return parsed;
                }
            }

            if (el.TryGetProperty("enabled", out var enabledEl))
            {
                if (enabledEl.ValueKind == JsonValueKind.True)
                {
                    if (el.TryGetProperty("allowSafeCommands", out var safeEl) && safeEl.ValueKind == JsonValueKind.False)
                    {
                        return AntigravityExecutionMode.Default;
                    }
                    return AntigravityExecutionMode.Auto;
                }
                if (enabledEl.ValueKind == JsonValueKind.False)
                {
                    return AntigravityExecutionMode.RequestReview;
                }
            }
        }

        return AntigravityExecutionMode.Unknown;
    }

    private static AntigravityReviewPolicy ParseReviewPolicyElement(JsonElement el)
    {
        if (el.ValueKind == JsonValueKind.String)
        {
            return ParseReviewPolicy(el.GetString());
        }

        if (el.ValueKind == JsonValueKind.Object)
        {
            foreach (var subKey in new[] { "mode", "policy", "value" })
            {
                if (el.TryGetProperty(subKey, out var sub) && sub.ValueKind == JsonValueKind.String)
                {
                    var parsed = ParseReviewPolicy(sub.GetString());
                    if (parsed != AntigravityReviewPolicy.Unknown) return parsed;
                }
            }
        }

        return AntigravityReviewPolicy.Unknown;
    }

    private static AntigravityExecutionMode ParseExecutionMode(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return AntigravityExecutionMode.Unknown;
        var norm = NormalizeValue(raw);
        if (string.IsNullOrEmpty(norm)) return AntigravityExecutionMode.Unknown;

        if (norm is "turbo" or "alwaysproceed" or "eager" or "alwaysexecute"
                or "unrestricted" or "dontask" or "bypasspermissions" or "allowall"
            || norm.Contains("autoexecutioneager", StringComparison.Ordinal)
            || norm.Contains("autoexecutionturbo", StringComparison.Ordinal)
            || norm.Contains("autoexecutionalways", StringComparison.Ordinal)
            || norm.Contains("alwaysproceed", StringComparison.Ordinal))
        {
            return AntigravityExecutionMode.Turbo;
        }

        if (norm is "auto" or "autopreview" or "autodecide" or "proceedinsandbox" or "agentdecides"
            || norm.Contains("autoexecutionauto", StringComparison.Ordinal)
            || norm.Contains("proceedinsandbox", StringComparison.Ordinal))
        {
            return AntigravityExecutionMode.Auto;
        }

        if (norm is "requestreview" or "strict" or "alwaysask" or "off" or "manual"
            || norm.Contains("autoexecutionoff", StringComparison.Ordinal)
            || norm.Contains("requestreview", StringComparison.Ordinal))
        {
            return AntigravityExecutionMode.RequestReview;
        }

        if (norm is "default" or "standard" or "sandbox")
        {
            return AntigravityExecutionMode.Default;
        }

        return AntigravityExecutionMode.Unknown;
    }

    private static AntigravityReviewPolicy ParseReviewPolicy(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return AntigravityReviewPolicy.Unknown;
        var norm = NormalizeValue(raw);
        if (string.IsNullOrEmpty(norm)) return AntigravityReviewPolicy.Unknown;

        if (norm is "alwaysproceed" or "turbo" or "off" or "never" or "auto"
            || norm.Contains("alwaysproceed", StringComparison.Ordinal))
        {
            return AntigravityReviewPolicy.AlwaysProceed;
        }

        if (norm is "agentdecides" or "smart")
        {
            return AntigravityReviewPolicy.AgentDecides;
        }

        if (norm is "requestreview" or "alwaysask" or "strict")
        {
            return AntigravityReviewPolicy.RequestReview;
        }

        return AntigravityReviewPolicy.Unknown;
    }

    private static string NormalizeValue(string raw)
        => raw.Trim().ToLowerInvariant().Replace("-", "").Replace("_", "").Replace(" ", "");

    private static bool IsRelevantPolicySettingName(string settingName)
    {
        var lower = settingName.ToLowerInvariant();
        return lower.Contains("policy", StringComparison.Ordinal)
            || lower.Contains("permission", StringComparison.Ordinal)
            || lower.Contains("execution", StringComparison.Ordinal)
            || lower.Contains("preset", StringComparison.Ordinal)
            || lower.Contains("review", StringComparison.Ordinal)
            || lower.Contains("sandbox", StringComparison.Ordinal)
            || lower.Contains("auto", StringComparison.Ordinal)
            || lower.Contains("turbo", StringComparison.Ordinal)
            || lower.Contains("terminal", StringComparison.Ordinal);
    }

    private static bool ContainsShellChaining(string command)
        => command.Contains("&&", StringComparison.Ordinal)
           || command.Contains("||", StringComparison.Ordinal)
           || command.Contains(';', StringComparison.Ordinal)
           || command.Contains('|', StringComparison.Ordinal)
           || ContainsSubshellSyntax(command);

    private static bool ContainsSubshellSyntax(string command)
        => command.Contains("$(", StringComparison.Ordinal)
           || command.Contains('`', StringComparison.Ordinal);

    private static IEnumerable<string> SplitChainedSegments(string command)
        => ChainSplitRegex.Split(command)
            .Select(s => s.Trim())
            .Where(s => !string.IsNullOrWhiteSpace(s));

    private static bool MatchesAnyPattern(
        string command,
        IReadOnlyList<string> patterns,
        bool allowWildcards,
        bool allowTokenPrefix)
    {
        foreach (var rawPattern in patterns)
        {
            var pat = CollapseWhitespace(rawPattern);
            if (string.IsNullOrEmpty(pat)) continue;

            if (pat == "*") return true;

            if (allowWildcards && pat.Contains('*', StringComparison.Ordinal))
            {
                var regexPattern = "^" + Regex.Escape(pat).Replace(@"\*", ".*") + "$";
                if (Regex.IsMatch(command, regexPattern, RegexOptions.IgnoreCase))
                {
                    return true;
                }
                continue;
            }

            if (string.Equals(command, pat, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }

            if (allowTokenPrefix &&
                command.Length > pat.Length &&
                command.StartsWith(pat, StringComparison.OrdinalIgnoreCase) &&
                char.IsWhiteSpace(command[pat.Length]))
            {
                return true;
            }
        }

        return false;
    }

    private static string CollapseWhitespace(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return "";
        return Regex.Replace(text.Trim(), @"\s+", " ");
    }

    private static string? NormalizePath(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return null;
        var trimmed = path.Trim();
        if (trimmed.StartsWith(@"\\?\", StringComparison.Ordinal)) trimmed = trimmed[4..];
        try
        {
            return Path.GetFullPath(trimmed).TrimEnd('\\', '/');
        }
        catch
        {
            return trimmed.Replace('/', '\\').TrimEnd('\\', '/');
        }
    }
}
