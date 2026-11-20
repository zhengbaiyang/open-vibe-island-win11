using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Data.Sqlite;
using OpenIsland.Core.Diagnostics;
using OpenIsland.Core.Hooks;
using OpenIsland.Core.Models;

namespace OpenIsland.Core.Discovery;

/// <summary>
/// Auto-discovers recent sessions from:
///  - Claude Code (~/.claude/projects/**/*.jsonl)
///  - Codex (~/.codex/sessions/**/rollout-*.jsonl plus the authoritative
///    ~/.codex/state_*.sqlite threads table)
///  - DeepSeek Harness (~/.dsh/storages/session_projcache/sessions/*.json)
/// and extracts 5-hour &amp; 7-day account usage windows.
/// </summary>
public sealed class TranscriptDiscovery
{
    private readonly Dictionary<string, long> _fileOffsets = new(StringComparer.OrdinalIgnoreCase);

    public List<AgentSession> ScanRecentSessions(TimeSpan? maxAge = null)
    {
        var cutoff = DateTime.UtcNow - (maxAge ?? TimeSpan.FromHours(6));
        var results = new List<AgentSession>();

        ScanClaudeProjects(cutoff, results);
        // Codex: the thread database (when present) is authoritative for what is recent;
        // rollout files may have been rewritten by a migration and would otherwise resurface
        // old conversations after an app update.
        var recentCodexThreadIds = ScanCodexThreadDatabase(cutoff, results);
        ScanCodexRollouts(cutoff, results, recentCodexThreadIds);
        ScanDeepSeekHarnessSessions(cutoff, results);

        return results;
    }

    private void ScanClaudeProjects(DateTime cutoffUtc, List<AgentSession> results)
    {
        var baseDir = AgentRegistry.ExpandHomePath("~/.claude/projects");
        if (!Directory.Exists(baseDir)) return;

        try
        {
            foreach (var file in Directory.EnumerateFiles(baseDir, "*.jsonl", SearchOption.AllDirectories))
            {
                var info = new FileInfo(file);
                if (info.LastWriteTimeUtc < cutoffUtc) continue;

                var sessionId = Path.GetFileNameWithoutExtension(file);
                string? cwd = null;
                string? model = null;
                string? lastPrompt = null;
                string? lastAssistant = null;

                foreach (var line in ReadTailLines(file, 40))
                {
                    try
                    {
                        using var doc = JsonDocument.Parse(line);
                        var root = doc.RootElement;
                        if (root.TryGetProperty("cwd", out var cwdEl) && cwdEl.ValueKind == JsonValueKind.String)
                            cwd = cwdEl.GetString();

                        var type = root.TryGetProperty("type", out var tEl) ? tEl.GetString() : null;
                        if (root.TryGetProperty("message", out var msgEl) && msgEl.ValueKind == JsonValueKind.Object)
                        {
                            if (msgEl.TryGetProperty("model", out var mEl) && mEl.ValueKind == JsonValueKind.String)
                                model = mEl.GetString();

                            var role = msgEl.TryGetProperty("role", out var rEl) ? rEl.GetString() : type;
                            var content = ExtractTextContent(msgEl);
                            if (!string.IsNullOrWhiteSpace(content))
                            {
                                if (string.Equals(role, "user", StringComparison.OrdinalIgnoreCase))
                                    lastPrompt = HookPayloadDecoder.Clip(content, 160);
                                else if (string.Equals(role, "assistant", StringComparison.OrdinalIgnoreCase))
                                    lastAssistant = HookPayloadDecoder.Clip(content, 240);
                            }
                        }
                    }
                    catch { }
                }

                results.Add(new AgentSession
                {
                    SessionId = sessionId,
                    Source = AgentSource.Claude,
                    Cwd = cwd,
                    Model = model,
                    TranscriptPath = file,
                    LastPrompt = lastPrompt,
                    LastAssistantMessage = lastAssistant,
                    StatusSummary = lastAssistant ?? lastPrompt ?? "Claude Code session",
                    Phase = (DateTime.UtcNow - info.LastWriteTimeUtc).TotalSeconds < 25 ? SessionPhase.Running : SessionPhase.Idle,
                    UpdatedAt = new DateTimeOffset(info.LastWriteTimeUtc, TimeSpan.Zero),
                    CreatedAt = new DateTimeOffset(info.CreationTimeUtc, TimeSpan.Zero)
                });
            }
        }
        catch { }
    }

    private void ScanCodexRollouts(DateTime cutoffUtc, List<AgentSession> results, HashSet<string>? allowedThreadIds = null)
    {
        var baseDir = AgentRegistry.ExpandHomePath("~/.codex/sessions");
        if (!Directory.Exists(baseDir)) return;

        try
        {
            foreach (var file in Directory.EnumerateFiles(baseDir, "rollout-*.jsonl", SearchOption.AllDirectories))
            {
                var info = new FileInfo(file);
                if (info.LastWriteTimeUtc < cutoffUtc) continue;

                // Incremental read optimization (mirrors Open Island v1.3.2 CPU fix)
                _fileOffsets.TryGetValue(file, out var prevLen);
                if (prevLen == info.Length && prevLen > 0) continue;
                _fileOffsets[file] = info.Length;

                // The real conversation id lives in the first "session_meta" line. The file name
                // also contains the thread UUID (rollout-<timestamp>-<uuid>.jsonl) — use it as a
                // fallback so click-to-jump still works when the content cannot be parsed.
                var fileNameId = TryExtractUuidFromName(file);
                string sessionId = fileNameId ?? Path.GetFileNameWithoutExtension(file);
                string? cwd = null;
                string? model = null;
                string? title = null;
                string? lastPrompt = null;
                string? lastAssistant = null;
                bool isDesktop = false;
                bool skipFile = false;

                var headLine = ReadFirstLine(file);
                if (!string.IsNullOrWhiteSpace(headLine))
                {
                    try
                    {
                        using var headDoc = JsonDocument.Parse(headLine);
                        if (headDoc.RootElement.TryGetProperty("payload", out var meta) &&
                            meta.ValueKind == JsonValueKind.Object)
                        {
                            if (meta.TryGetProperty("source", out var srcEl) && IsInternalCodexSource(srcEl))
                            {
                                skipFile = true;
                            }

                            var metaType = meta.TryGetProperty("type", out var mt) ? mt.GetString() : null;
                            if (string.Equals(metaType, "session_meta", StringComparison.OrdinalIgnoreCase) ||
                                string.Equals(metaType, "session.start", StringComparison.OrdinalIgnoreCase))
                            {
                                if (meta.TryGetProperty("id", out var idEl) && idEl.ValueKind == JsonValueKind.String)
                                    sessionId = idEl.GetString() ?? sessionId;
                                if (meta.TryGetProperty("thread_id", out var tidEl) && tidEl.ValueKind == JsonValueKind.String)
                                    sessionId = tidEl.GetString() ?? sessionId;
                                if (meta.TryGetProperty("cwd", out var cwdEl) && cwdEl.ValueKind == JsonValueKind.String)
                                    cwd = cwdEl.GetString();
                                if (meta.TryGetProperty("model", out var mdlEl) && mdlEl.ValueKind == JsonValueKind.String)
                                    model = mdlEl.GetString();
                                if (meta.TryGetProperty("title", out var titleEl) && titleEl.ValueKind == JsonValueKind.String)
                                    title = titleEl.GetString();
                                if (meta.TryGetProperty("originator", out var origEl) && origEl.ValueKind == JsonValueKind.String)
                                {
                                    var orig = origEl.GetString() ?? "";
                                    isDesktop = orig.Contains("desktop", StringComparison.OrdinalIgnoreCase)
                                                || orig.Contains("chatgpt", StringComparison.OrdinalIgnoreCase)
                                                || orig.Contains("codex_app", StringComparison.OrdinalIgnoreCase);
                                }
                            }
                        }
                    }
                    catch { }
                }

                if (skipFile) continue;

                foreach (var line in ReadTailLines(file, 60))
                {
                    try
                    {
                        using var doc = JsonDocument.Parse(line);
                        var root = doc.RootElement;

                        if (root.TryGetProperty("payload", out var payload) && payload.ValueKind == JsonValueKind.Object)
                        {
                            if (payload.TryGetProperty("source", out var srcEl) && IsInternalCodexSource(srcEl))
                            {
                                skipFile = true;
                                break;
                            }
                            if (payload.TryGetProperty("cwd", out var cwdEl) && cwdEl.ValueKind == JsonValueKind.String)
                                cwd = cwdEl.GetString();
                            if (payload.TryGetProperty("model", out var mEl) && mEl.ValueKind == JsonValueKind.String)
                                model = mEl.GetString();
                            if (payload.TryGetProperty("title", out var tEl) && tEl.ValueKind == JsonValueKind.String
                                && !string.IsNullOrWhiteSpace(tEl.GetString()))
                                title = tEl.GetString();
                            if (payload.TryGetProperty("originator", out var origEl) && origEl.ValueKind == JsonValueKind.String)
                            {
                                var orig = origEl.GetString() ?? "";
                                if (orig.Contains("desktop", StringComparison.OrdinalIgnoreCase)
                                    || orig.Contains("chatgpt", StringComparison.OrdinalIgnoreCase))
                                    isDesktop = true;
                            }
                        }

                        var (role, text) = ExtractCodexMessage(root);
                        if (!string.IsNullOrWhiteSpace(text))
                        {
                            if (string.Equals(role, "user", StringComparison.OrdinalIgnoreCase))
                                lastPrompt = HookPayloadDecoder.Clip(text, 160);
                            else if (string.Equals(role, "assistant", StringComparison.OrdinalIgnoreCase))
                                lastAssistant = HookPayloadDecoder.Clip(text, 240);
                        }
                    }
                    catch { }
                }

                if (skipFile) continue;

                // Internal child threads (subagents / guardian reviews) are not user
                // conversations: showing them made the island pile up with "Codex session"
                // rows during scheduled automation runs.
                if (string.Equals(title, "Guardian review", StringComparison.OrdinalIgnoreCase)) continue;

                // When the Codex thread database is readable only those thread ids are allowed.
                if (allowedThreadIds != null && !allowedThreadIds.Contains(sessionId.ToLowerInvariant())) continue;

                cwd = NormalizeWindowsPath(cwd);
                var terminal = new TerminalMetadata
                {
                    IsDesktopApp = isDesktop,
                    DesktopAppName = isDesktop ? "Codex Desktop" : null,
                    TerminalApp = isDesktop ? "Codex Desktop" : "Codex",
                    // Registered by Codex App / ChatGPT: opens the exact conversation.
                    // Only real thread ids (UUIDs) are valid in the deep link.
                    DeepLinkUrl = Guid.TryParse(sessionId, out _) ? $"codex://threads/{sessionId}" : null
                };

                results.Add(new AgentSession
                {
                    SessionId = sessionId,
                    Source = AgentSource.Codex,
                    Title = title ?? "",
                    Cwd = cwd,
                    Model = model,
                    TranscriptPath = file,
                    LastPrompt = lastPrompt,
                    LastAssistantMessage = lastAssistant,
                    StatusSummary = lastAssistant ?? lastPrompt ?? "Codex session",
                    Phase = (DateTime.UtcNow - info.LastWriteTimeUtc).TotalSeconds < 25 ? SessionPhase.Running : SessionPhase.Idle,
                    UpdatedAt = new DateTimeOffset(info.LastWriteTimeUtc, TimeSpan.Zero),
                    CreatedAt = new DateTimeOffset(info.CreationTimeUtc, TimeSpan.Zero),
                    Terminal = terminal
                });
            }
        }
        catch { }
    }

    // ------------------------------------------------------------------
    // Codex thread database (~/.codex/state_*.sqlite): authoritative session
    // metadata (title, cwd, model) used by Codex CLI/App. Robust against
    // rollout format changes (.jsonl.zst) and lets us filter internal threads.
    // ------------------------------------------------------------------
    private static HashSet<string>? ScanCodexThreadDatabase(DateTime cutoffUtc, List<AgentSession> results)
    {
        try
        {
            var codexHome = AgentRegistry.ExpandHomePath("~/.codex");
            if (!Directory.Exists(codexHome)) return null;

            var dbFile = Directory.EnumerateFiles(codexHome, "state_*.sqlite")
                .OrderByDescending(f =>
                {
                    var name = Path.GetFileNameWithoutExtension(f);
                    var num = name.StartsWith("state_", StringComparison.OrdinalIgnoreCase) ? name[6..] : "";
                    return int.TryParse(num, out var version) ? version : -1;
                })
                .FirstOrDefault();
            if (dbFile is null) return null;

            var connectionString = new SqliteConnectionStringBuilder
            {
                DataSource = dbFile,
                Mode = SqliteOpenMode.ReadOnly,
                Cache = SqliteCacheMode.Private,
                DefaultTimeout = 2
            }.ToString();

            using var connection = new SqliteConnection(connectionString);
            connection.Open();

            var columns = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            using (var pragma = connection.CreateCommand())
            {
                pragma.CommandText = "PRAGMA table_info(threads)";
                using var reader = pragma.ExecuteReader();
                while (reader.Read())
                {
                    if (!reader.IsDBNull(1)) columns.Add(reader.GetString(1));
                }
            }
            if (columns.Count == 0 || !columns.Contains("updated_at")) return null;

            var recentIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            var cutoffSeconds = new DateTimeOffset(cutoffUtc, TimeSpan.Zero).ToUnixTimeSeconds();
            using var command = connection.CreateCommand();
            var modelColumn = columns.Contains("model") ? "model" : "NULL";
            command.CommandText =
                "SELECT id, title, cwd, updated_at, source, first_user_message, " +
                $"rollout_path, {modelColumn}, created_at FROM threads " +
                "WHERE archived = 0 AND updated_at >= $cutoff ORDER BY updated_at DESC LIMIT 60";
            command.Parameters.AddWithValue("$cutoff", cutoffSeconds);

            using var rows = command.ExecuteReader();
            while (rows.Read())
            {
                if (rows.IsDBNull(0)) continue;
                var id = rows.GetString(0);
                if (!Guid.TryParse(id, out _)) continue;
                recentIds.Add(id.ToLowerInvariant());

                var source = rows.IsDBNull(4) ? "" : rows.GetString(4);
                if (IsInternalCodexSource(source)) continue;

                var title = rows.IsDBNull(1) ? "" : rows.GetString(1).Trim();
                if (string.Equals(title, "Guardian review", StringComparison.OrdinalIgnoreCase)) continue;

                var updatedRaw = rows.IsDBNull(3) ? 0L : rows.GetInt64(3);
                var updatedAt = FromUnixTimestamp(updatedRaw);
                if (updatedAt < new DateTimeOffset(cutoffUtc, TimeSpan.Zero)) continue;

                var cwd = rows.IsDBNull(2) ? null : NormalizeWindowsPath(rows.GetString(2));
                var firstMessage = rows.IsDBNull(5) ? "" : rows.GetString(5).Trim();
                var rolloutPath = rows.IsDBNull(6) ? null : rows.GetString(6);
                var model = rows.IsDBNull(7) ? null : rows.GetString(7);
                var createdRaw = rows.IsDBNull(8) ? updatedRaw : rows.GetInt64(8);
                var createdAt = FromUnixTimestamp(createdRaw);

                var displayTitle = title.Length > 90 ? title[..90] : title;
                var statusSummary = !string.IsNullOrWhiteSpace(firstMessage) && !string.Equals(firstMessage, title, StringComparison.Ordinal)
                    ? HookPayloadDecoder.Clip(firstMessage, 160)
                    : (string.IsNullOrWhiteSpace(displayTitle) ? "Codex session" : displayTitle);

                results.Add(new AgentSession
                {
                    SessionId = id,
                    Source = AgentSource.Codex,
                    Title = displayTitle,
                    Cwd = cwd,
                    Model = model,
                    TranscriptPath = rolloutPath,
                    LastPrompt = string.IsNullOrWhiteSpace(firstMessage) ? null : HookPayloadDecoder.Clip(firstMessage, 160),
                    StatusSummary = statusSummary,
                    Phase = (DateTimeOffset.UtcNow - updatedAt).TotalSeconds < 60 ? SessionPhase.Running : SessionPhase.Idle,
                    UpdatedAt = updatedAt,
                    CreatedAt = createdAt,
                    Terminal = new TerminalMetadata
                    {
                        IsDesktopApp = true,
                        DesktopAppName = "Codex",
                        TerminalApp = "Codex",
                        DeepLinkUrl = $"codex://threads/{id}"
                    }
                });
            }

            return recentIds;
        }
        catch (Exception ex)
        {
            AppLog.Warn($"Codex thread database scan failed: {ex.Message}");
            return null;
        }
    }

    // ------------------------------------------------------------------
    // DeepSeek Harness (DSH) local sessions. DSH has no hook system; the
    // session cache contains identity (cwd/createdAt), turn outline
    // (prompt/response) and live turn state, which is enough for the island.
    // ------------------------------------------------------------------
    private void ScanDeepSeekHarnessSessions(DateTime cutoffUtc, List<AgentSession> results)
    {
        try
        {
            var dir = AgentRegistry.ExpandHomePath("~/.dsh/storages/session_projcache/sessions");
            if (!Directory.Exists(dir)) return;

            var cutoffMs = new DateTimeOffset(cutoffUtc, TimeSpan.Zero).ToUnixTimeMilliseconds();
            // Cache files are rewritten whenever DSH starts, so file mtime is NOT the session
            // activity time. Parse the cache and use timeContext.lastMessageTime instead.
            var files = new DirectoryInfo(dir)
                .EnumerateFiles("*.json")
                .OrderByDescending(f => f.LastWriteTimeUtc)
                .Take(80)
                .ToList();

            foreach (var file in files)
            {
                try
                {
                    using var doc = JsonDocument.Parse(File.ReadAllText(file.FullName));
                    if (!doc.RootElement.TryGetProperty("record", out var record) ||
                        record.ValueKind != JsonValueKind.Object)
                        continue;

                    if (!record.TryGetProperty("identity", out var identity) ||
                        identity.ValueKind != JsonValueKind.Object)
                        continue;

                    if (identity.TryGetProperty("isSeeded", out var seeded) &&
                        seeded.ValueKind == JsonValueKind.True)
                        continue;

                    if (!record.TryGetProperty("rows", out var rows) || rows.ValueKind != JsonValueKind.Object)
                        continue;

                    // --- activity time (authoritative; cache files are rewritten on launch) ---
                    long? activityMs = null;
                    if (GetRowValue(rows, "timeContext") is { } timeContext &&
                        timeContext.TryGetProperty("lastMessageTime", out var lastMessageEl) &&
                        lastMessageEl.TryGetInt64(out var lastMessageMs))
                        activityMs = lastMessageMs;
                    if (GetRowValue(rows, "sessionListMetadata") is { } listMeta)
                    {
                        if (listMeta.TryGetProperty("lastPromptAt", out var promptAtEl) &&
                            promptAtEl.TryGetInt64(out var promptAtMs) &&
                            (activityMs == null || promptAtMs > activityMs))
                            activityMs = promptAtMs;
                    }
                    if (activityMs == null || activityMs < cutoffMs) continue;

                    // --- blank / untouched sessions: nothing to show ---
                    var isBlank = false;
                    if (GetRowValue(rows, "sessionListMetadata") is { } metaForBlank &&
                        metaForBlank.TryGetProperty("blank", out var blankEl) &&
                        blankEl.ValueKind == JsonValueKind.True)
                        isBlank = true;

                    string? metaTitle = null;
                    if (GetRowValue(rows, "title") is { ValueKind: JsonValueKind.String } titleElement)
                        metaTitle = titleElement.GetString();

                    var cwd = identity.TryGetProperty("cwd", out var cwdEl) && cwdEl.ValueKind == JsonValueKind.String
                        ? NormalizeWindowsPath(cwdEl.GetString())
                        : null;
                    if (GetRowValue(rows, "workingDirectory") is { ValueKind: JsonValueKind.String } workDirEl &&
                        !string.IsNullOrWhiteSpace(workDirEl.GetString()))
                        cwd = NormalizeWindowsPath(workDirEl.GetString());

                    string? model = null;
                    if (GetRowValue(rows, "modelSelection") is { } selection &&
                        selection.TryGetProperty("lastUsed", out var lastUsed) &&
                        lastUsed.ValueKind == JsonValueKind.Object &&
                        lastUsed.TryGetProperty("model", out var modelEl) &&
                        modelEl.ValueKind == JsonValueKind.String)
                        model = modelEl.GetString();

                    var createdAtMs = identity.TryGetProperty("createdAt", out var createdEl) && createdEl.TryGetInt64(out var created)
                        ? created
                        : new DateTimeOffset(file.CreationTimeUtc, TimeSpan.Zero).ToUnixTimeMilliseconds();
                    var createdAt = DateTimeOffset.FromUnixTimeMilliseconds(createdAtMs);

                    // --- turn data ---
                    string? lastPrompt = null;
                    string? lastAssistant = null;
                    var turnCount = 0;
                    var turnOpen = false;

                    if (GetRowValue(rows, "sessionStats") is { } stats)
                    {
                        if (stats.TryGetProperty("openStep", out var openStep) &&
                            openStep.ValueKind == JsonValueKind.Object)
                            turnOpen = true;
                        if (stats.TryGetProperty("turns", out var turnsEl) && turnsEl.TryGetInt32(out var turns))
                            turnCount = turns;
                    }
                    if (GetRowValue(rows, "approvalState") is { } approval &&
                        approval.TryGetProperty("turnOpen", out var openTurnEl) &&
                        openTurnEl.ValueKind == JsonValueKind.True)
                        turnOpen = true;

                    if (GetRowValue(rows, "turnOutline") is { } outline &&
                        outline.TryGetProperty("turns", out var outlineTurns) &&
                        outlineTurns.ValueKind == JsonValueKind.Array)
                    {
                        JsonElement? lastTurn = null;
                        foreach (var turn in outlineTurns.EnumerateArray())
                        {
                            lastTurn = turn;
                        }
                        if (lastTurn is JsonElement last)
                        {
                            if (last.TryGetProperty("prompt", out var promptEl) && promptEl.ValueKind == JsonValueKind.String)
                                lastPrompt = promptEl.GetString();
                            if (last.TryGetProperty("response", out var respEl) && respEl.ValueKind == JsonValueKind.String)
                                lastAssistant = respEl.GetString();
                        }
                    }

                    if (isBlank && turnCount == 0 && string.IsNullOrWhiteSpace(metaTitle)) continue;

                    var updatedAt = DateTimeOffset.FromUnixTimeMilliseconds(activityMs.Value);
                    var idleSeconds = (DateTimeOffset.UtcNow - updatedAt).TotalSeconds;
                    var running = turnOpen && idleSeconds < 1800;
                    var phase = running
                        ? SessionPhase.Running
                        : idleSeconds < 30 ? SessionPhase.Completed : SessionPhase.Idle;

                    var sessionId = Path.GetFileNameWithoutExtension(file.Name);
                    if (sessionId.StartsWith("session-", StringComparison.OrdinalIgnoreCase))
                        sessionId = sessionId[8..];

                    var title = !string.IsNullOrWhiteSpace(metaTitle) ? metaTitle : lastPrompt;
                    var statusSummary = !string.IsNullOrWhiteSpace(lastAssistant)
                        ? HookPayloadDecoder.Clip(lastAssistant, 160)
                        : !string.IsNullOrWhiteSpace(lastPrompt)
                            ? HookPayloadDecoder.Clip(lastPrompt, 160)
                            : "DeepSeek Harness session";

                    results.Add(new AgentSession
                    {
                        SessionId = sessionId,
                        Source = AgentSource.DeepSeekHarness,
                        Title = HookPayloadDecoder.Clip(title, 90) ?? "",
                        Cwd = cwd,
                        Model = model,
                        LastPrompt = HookPayloadDecoder.Clip(lastPrompt, 160),
                        LastAssistantMessage = HookPayloadDecoder.Clip(lastAssistant, 240),
                        StatusSummary = statusSummary,
                        Phase = phase,
                        UpdatedAt = updatedAt,
                        CreatedAt = createdAt,
                        Terminal = new TerminalMetadata
                        {
                            TerminalApp = "DeepSeek Harness",
                            DesktopAppName = "DeepSeek Harness"
                        }
                    });
                }
                catch
                {
                    // Cache files are rewritten live; a partially written JSON is expected.
                }
            }
        }
        catch (Exception ex)
        {
            AppLog.Warn($"DeepSeek Harness session scan failed: {ex.Message}");
        }
    }

    private static JsonElement? GetRowValue(JsonElement rows, string rowName)
    {
        if (rows.TryGetProperty(rowName, out var row) &&
            row.ValueKind == JsonValueKind.Object &&
            row.TryGetProperty("val", out var value))
        {
            return value;
        }
        return null;
    }

    private static string? TryExtractUuidFromName(string path)
    {
        var match = Regex.Match(Path.GetFileNameWithoutExtension(path),
            @"[0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12}");
        return match.Success ? match.Value.ToLowerInvariant() : null;
    }

    private static bool IsInternalCodexSource(JsonElement source)
        => IsInternalCodexSource(source.ValueKind == JsonValueKind.String ? source.GetString() : source.GetRawText());

    private static bool IsInternalCodexSource(string? source)
    {
        if (string.IsNullOrWhiteSpace(source)) return false;
        return source.Contains("subagent", StringComparison.OrdinalIgnoreCase)
               || source.Contains("guardian", StringComparison.OrdinalIgnoreCase);
    }

    private static string? NormalizeWindowsPath(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return path;
        var trimmed = path.Trim();
        if (trimmed.StartsWith(@"\\?\", StringComparison.Ordinal)) trimmed = trimmed[4..];
        return trimmed;
    }

    private static DateTimeOffset FromUnixTimestamp(long value)
    {
        // Accept both seconds (state_5) and milliseconds (future schema variants).
        return value > 10_000_000_000
            ? DateTimeOffset.FromUnixTimeMilliseconds(value)
            : DateTimeOffset.FromUnixTimeSeconds(value);
    }

    private static string? ReadFirstLine(string path)
    {
        try
        {
            using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            using var reader = new StreamReader(fs);
            return reader.ReadLine();
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// Extracts a user/assistant message from a Codex rollout line
    /// (response_item message, event_msg user_message/agent_message, or legacy shapes).
    /// </summary>
    private static (string? Role, string? Text) ExtractCodexMessage(JsonElement root)
    {
        if (root.TryGetProperty("payload", out var payload) && payload.ValueKind == JsonValueKind.Object)
        {
            var type = payload.TryGetProperty("type", out var tEl) ? tEl.GetString() : null;
            if (payload.TryGetProperty("role", out var roleEl) && roleEl.ValueKind == JsonValueKind.String)
            {
                var role = roleEl.GetString();
                if (payload.TryGetProperty("content", out var content))
                {
                    var text = ExtractTextContentFrom(content);
                    if (!string.IsNullOrWhiteSpace(text)) return (role, text);
                }
            }

            if (string.Equals(type, "user_message", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(type, "agent_message", StringComparison.OrdinalIgnoreCase))
            {
                var message = payload.TryGetProperty("message", out var mEl) && mEl.ValueKind == JsonValueKind.String
                    ? mEl.GetString()
                    : null;
                var role = type!.StartsWith("user", StringComparison.OrdinalIgnoreCase) ? "user" : "assistant";
                if (!string.IsNullOrWhiteSpace(message)) return (role, message);
            }

            if (payload.TryGetProperty("message", out var msgEl) && msgEl.ValueKind == JsonValueKind.String)
            {
                var text = msgEl.GetString();
                if (!string.IsNullOrWhiteSpace(text))
                {
                    var role = payload.TryGetProperty("role", out var rEl) && rEl.ValueKind == JsonValueKind.String
                        ? rEl.GetString()
                        : null;
                    return (role, text);
                }
            }
        }

        // Legacy/flat shapes
        if (root.TryGetProperty("role", out var flatRole) && flatRole.ValueKind == JsonValueKind.String)
        {
            var text = ExtractTextContent(root);
            if (!string.IsNullOrWhiteSpace(text)) return (flatRole.GetString(), text);
        }
        return (null, null);
    }

    private static string? ExtractTextContentFrom(JsonElement content)
    {
        if (content.ValueKind == JsonValueKind.String) return content.GetString();
        if (content.ValueKind == JsonValueKind.Array)
        {
            foreach (var part in content.EnumerateArray())
            {
                if (part.ValueKind == JsonValueKind.Object &&
                    part.TryGetProperty("text", out var t) && t.ValueKind == JsonValueKind.String)
                {
                    var s = t.GetString();
                    if (!string.IsNullOrWhiteSpace(s)) return s;
                }
                if (part.ValueKind == JsonValueKind.String) return part.GetString();
            }
        }
        return null;
    }

    public static List<UsageWindowInfo> ReadUsageWindows()
    {
        var list = new List<UsageWindowInfo>();

        // 1. Claude Code cached rate_limits from managed statusLine (%TEMP%\open-island-rl.json)
        var claudeUsage = new UsageWindowInfo
        {
            AgentName = "Claude Code",
            Note = "Requires Claude statusLine bridge (installable in Settings -> Setup)"
        };
        if (File.Exists(BridgeConstants.ClaudeRateLimitCachePath))
        {
            try
            {
                var json = File.ReadAllText(BridgeConstants.ClaudeRateLimitCachePath);
                using var doc = JsonDocument.Parse(json);
                var root = doc.RootElement;
                if (root.TryGetProperty("five_hour", out var fh) && fh.TryGetProperty("utilization", out var fhVal))
                {
                    claudeUsage.FiveHourPercent = fhVal.GetDouble();
                    if (fh.TryGetProperty("resets_at", out var r)) claudeUsage.FiveHourResetText = r.GetString();
                }
                if (root.TryGetProperty("seven_day", out var sd) && sd.TryGetProperty("utilization", out var sdVal))
                {
                    claudeUsage.SevenDayPercent = sdVal.GetDouble();
                    if (sd.TryGetProperty("resets_at", out var r)) claudeUsage.SevenDayResetText = r.GetString();
                }
                claudeUsage.LastUpdated = new FileInfo(BridgeConstants.ClaudeRateLimitCachePath).LastWriteTime;
                claudeUsage.Note = null;
            }
            catch { }
        }
        list.Add(claudeUsage);

        // 2. Codex rollout rate_limits scan
        var codexUsage = new UsageWindowInfo
        {
            AgentName = "Codex",
            Note = "Reads 5h/7d windows from latest ~/.codex/sessions/**/rollout-*.jsonl"
        };
        var codexDir = AgentRegistry.ExpandHomePath("~/.codex/sessions");
        if (Directory.Exists(codexDir))
        {
            try
            {
                var latest = Directory.EnumerateFiles(codexDir, "rollout-*.jsonl", SearchOption.AllDirectories)
                    .Select(f => new FileInfo(f))
                    .OrderByDescending(f => f.LastWriteTimeUtc)
                    .FirstOrDefault();

                if (latest != null)
                {
                    foreach (var line in ReadTailLines(latest.FullName, 80).Reverse())
                    {
                        if (!line.Contains("rate_limits", StringComparison.OrdinalIgnoreCase)) continue;
                        try
                        {
                            using var doc = JsonDocument.Parse(line);
                            if (TryFindRateLimits(doc.RootElement, out var rl))
                            {
                                if (rl.TryGetProperty("primary", out var p) && p.TryGetProperty("used_percent", out var pv))
                                    codexUsage.FiveHourPercent = pv.GetDouble();
                                if (rl.TryGetProperty("secondary", out var s) && s.TryGetProperty("used_percent", out var sv))
                                    codexUsage.SevenDayPercent = sv.GetDouble();
                                codexUsage.LastUpdated = latest.LastWriteTime;
                                codexUsage.Note = null;
                                break;
                            }
                        }
                        catch { }
                    }
                }
            }
            catch { }
        }
        list.Add(codexUsage);

        return list;
    }

    private static bool TryFindRateLimits(JsonElement el, out JsonElement found)
    {
        if (el.ValueKind == JsonValueKind.Object)
        {
            if (el.TryGetProperty("rate_limits", out found) && found.ValueKind == JsonValueKind.Object)
                return true;
            foreach (var prop in el.EnumerateObject())
            {
                if (TryFindRateLimits(prop.Value, out found)) return true;
            }
        }
        found = default;
        return false;
    }

    private static string? ExtractTextContent(JsonElement obj)
    {
        if (!obj.TryGetProperty("content", out var content)) return null;
        if (content.ValueKind == JsonValueKind.String) return content.GetString();
        if (content.ValueKind == JsonValueKind.Array)
        {
            foreach (var part in content.EnumerateArray())
            {
                if (part.ValueKind == JsonValueKind.Object
                    && part.TryGetProperty("text", out var t)
                    && t.ValueKind == JsonValueKind.String)
                {
                    var s = t.GetString();
                    if (!string.IsNullOrWhiteSpace(s)) return s;
                }
            }
        }
        return null;
    }

    private static IEnumerable<string> ReadTailLines(string path, int maxLines)
    {
        var lines = new Queue<string>(maxLines + 1);
        using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        using var sr = new StreamReader(fs);
        string? line;
        while ((line = sr.ReadLine()) != null)
        {
            if (lines.Count >= maxLines) lines.Dequeue();
            lines.Enqueue(line);
        }
        return lines;
    }
}
