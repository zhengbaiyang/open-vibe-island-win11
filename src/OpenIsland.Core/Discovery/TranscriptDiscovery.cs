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
    private readonly AntigravityPolicyDetector _antigravityPolicy = new();

    public List<AgentSession> ScanRecentSessions(
        TimeSpan? maxAge = null,
        IReadOnlyDictionary<string, string?>? knownSessionCwds = null)
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
        ScanAntigravitySessions(cutoff, results, knownSessionCwds);

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
                PendingQuestionRequest? pendingQuestion = null;
                PendingApprovalRequest? pendingApproval = null;
                var pendingToolUses = new Dictionary<string, (string Name, PendingQuestionRequest? Question, PendingApprovalRequest? Approval)>(StringComparer.Ordinal);

                foreach (var line in ReadTailLines(file, 50))
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

                            if (msgEl.TryGetProperty("content", out var contentArr) && contentArr.ValueKind == JsonValueKind.Array)
                            {
                                foreach (var block in contentArr.EnumerateArray())
                                {
                                    if (block.ValueKind != JsonValueKind.Object) continue;
                                    var bType = block.TryGetProperty("type", out var btEl) && btEl.ValueKind == JsonValueKind.String
                                        ? btEl.GetString()
                                        : null;
                                    if (string.Equals(bType, "tool_use", StringComparison.OrdinalIgnoreCase))
                                    {
                                        var toolId = block.TryGetProperty("id", out var idEl) && idEl.ValueKind == JsonValueKind.String
                                            ? idEl.GetString() ?? Guid.NewGuid().ToString("N")
                                            : Guid.NewGuid().ToString("N");
                                        var toolName = block.TryGetProperty("name", out var nEl) && nEl.ValueKind == JsonValueKind.String
                                            ? nEl.GetString() ?? ""
                                            : "";

                                        if (HookPayloadDecoder.IsQuestionToolName(toolName))
                                        {
                                            var qs = new List<PendingQuestionItem>();
                                            if (block.TryGetProperty("input", out var inEl))
                                            {
                                                HookPayloadDecoder.ExtractQuestions(inEl, qs);
                                            }
                                            if (qs.Count == 0)
                                            {
                                                qs.Add(new PendingQuestionItem
                                                {
                                                    Header = toolName,
                                                    Question = Localization.Strings.Get("QuestionHint")
                                                });
                                            }
                                            pendingToolUses[toolId] = (toolName, new PendingQuestionRequest
                                            {
                                                RequestId = $"clq-{sessionId}-{toolId}",
                                                SessionId = sessionId,
                                                Source = AgentSource.Claude,
                                                Questions = qs,
                                                IsBlocking = false,
                                                CreatedAt = new DateTimeOffset(info.LastWriteTimeUtc, TimeSpan.Zero)
                                            }, null);
                                        }
                                        else if (HookPayloadDecoder.IsPlanApprovalToolName(toolName))
                                        {
                                            var opts = new List<QuestionOption>();
                                            if (block.TryGetProperty("input", out var inEl))
                                            {
                                                HookPayloadDecoder.ExtractApprovalOptions(inEl, opts);
                                            }
                                            if (opts.Count == 0)
                                            {
                                                opts.Add(new QuestionOption { Label = Localization.Strings.Get("OptionProceedPlan") });
                                                opts.Add(new QuestionOption { Label = Localization.Strings.Get("OptionRequestChanges") });
                                            }
                                            pendingToolUses[toolId] = (toolName, null, new PendingApprovalRequest
                                            {
                                                RequestId = $"clp-{sessionId}-{toolId}",
                                                SessionId = sessionId,
                                                Source = AgentSource.Claude,
                                                HookEventName = "PermissionRequest",
                                                ToolName = toolName,
                                                Summary = Localization.Strings.Get("PlanApprovalSummary"),
                                                Options = opts,
                                                IsBlocking = false,
                                                CreatedAt = new DateTimeOffset(info.LastWriteTimeUtc, TimeSpan.Zero)
                                            });
                                        }
                                    }
                                    else if (string.Equals(bType, "tool_result", StringComparison.OrdinalIgnoreCase))
                                    {
                                        var refId = block.TryGetProperty("tool_use_id", out var refEl) && refEl.ValueKind == JsonValueKind.String
                                            ? refEl.GetString()
                                            : null;
                                        if (!string.IsNullOrWhiteSpace(refId))
                                        {
                                            pendingToolUses.Remove(refId);
                                        }
                                    }
                                }
                            }
                        }
                    }
                    catch { }
                }

                var idleSec = (DateTime.UtcNow - info.LastWriteTimeUtc).TotalSeconds;
                if (idleSec < 1800 && pendingToolUses.Count > 0)
                {
                    var latest = pendingToolUses.Values.Last();
                    pendingQuestion = latest.Question;
                    pendingApproval = latest.Approval;
                }

                var phase = pendingQuestion != null
                    ? SessionPhase.WaitingForAnswer
                    : pendingApproval != null
                        ? SessionPhase.WaitingForApproval
                        : idleSec < 25 ? SessionPhase.Running : SessionPhase.Idle;

                results.Add(new AgentSession
                {
                    SessionId = sessionId,
                    Source = AgentSource.Claude,
                    Cwd = cwd,
                    Model = model,
                    TranscriptPath = file,
                    LastPrompt = lastPrompt,
                    LastAssistantMessage = lastAssistant,
                    StatusSummary = pendingQuestion?.Questions.FirstOrDefault()?.Question
                                    ?? pendingApproval?.Summary
                                    ?? lastAssistant ?? lastPrompt ?? "Claude Code session",
                    Phase = phase,
                    PendingQuestion = pendingQuestion,
                    PendingApproval = pendingApproval,
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

                var codexPendingQuestions = new Dictionary<string, PendingQuestionRequest>(StringComparer.Ordinal);

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

                            var pType = payload.TryGetProperty("type", out var ptEl) && ptEl.ValueKind == JsonValueKind.String
                                ? ptEl.GetString()
                                : null;
                            if (string.Equals(pType, "function_call", StringComparison.OrdinalIgnoreCase) ||
                                string.Equals(pType, "tool_call", StringComparison.OrdinalIgnoreCase))
                            {
                                var fnName = payload.TryGetProperty("name", out var fnEl) && fnEl.ValueKind == JsonValueKind.String
                                    ? fnEl.GetString()
                                    : null;
                                var callId = payload.TryGetProperty("call_id", out var cidEl) && cidEl.ValueKind == JsonValueKind.String
                                    ? cidEl.GetString() ?? Guid.NewGuid().ToString("N")
                                    : Guid.NewGuid().ToString("N");
                                if (HookPayloadDecoder.IsQuestionToolName(fnName))
                                {
                                    var qs = new List<PendingQuestionItem>();
                                    if (payload.TryGetProperty("arguments", out var argsEl))
                                    {
                                        HookPayloadDecoder.ExtractQuestions(argsEl, qs);
                                    }
                                    if (qs.Count == 0)
                                    {
                                        qs.Add(new PendingQuestionItem
                                        {
                                            Header = fnName ?? "Codex",
                                            Question = Localization.Strings.Get("QuestionHint")
                                        });
                                    }
                                    codexPendingQuestions[callId] = new PendingQuestionRequest
                                    {
                                        RequestId = $"cxq-{sessionId}-{callId}",
                                        SessionId = sessionId,
                                        Source = AgentSource.Codex,
                                        Questions = qs,
                                        IsBlocking = false,
                                        CreatedAt = new DateTimeOffset(info.LastWriteTimeUtc, TimeSpan.Zero)
                                    };
                                }
                            }
                            else if (string.Equals(pType, "function_call_output", StringComparison.OrdinalIgnoreCase) ||
                                     string.Equals(pType, "tool_call_output", StringComparison.OrdinalIgnoreCase))
                            {
                                var refCallId = payload.TryGetProperty("call_id", out var refCidEl) && refCidEl.ValueKind == JsonValueKind.String
                                    ? refCidEl.GetString()
                                    : null;
                                if (!string.IsNullOrWhiteSpace(refCallId))
                                {
                                    codexPendingQuestions.Remove(refCallId);
                                }
                            }
                        }

                        var (role, text) = ExtractCodexMessage(root);
                        if (!string.IsNullOrWhiteSpace(text))
                        {
                            if (string.Equals(role, "user", StringComparison.OrdinalIgnoreCase))
                            {
                                lastPrompt = HookPayloadDecoder.Clip(text, 160);
                                codexPendingQuestions.Clear();
                            }
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

                var codexIdleSec = (DateTime.UtcNow - info.LastWriteTimeUtc).TotalSeconds;
                var codexPendingQ = codexIdleSec < 1800 && codexPendingQuestions.Count > 0
                    ? codexPendingQuestions.Values.Last()
                    : null;

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
                    StatusSummary = codexPendingQ?.Questions.FirstOrDefault()?.Question ?? lastAssistant ?? lastPrompt ?? "Codex session",
                    Phase = codexPendingQ != null
                        ? SessionPhase.WaitingForAnswer
                        : codexIdleSec < 25 ? SessionPhase.Running : SessionPhase.Idle,
                    PendingQuestion = codexPendingQ,
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
                    var sessionId = Path.GetFileNameWithoutExtension(file.Name);
                    if (sessionId.StartsWith("session-", StringComparison.OrdinalIgnoreCase))
                        sessionId = sessionId[8..];

                    PendingQuestionRequest? dshPendingQuestion = null;
                    PendingApprovalRequest? dshPendingApproval = null;

                    if (GetRowValue(rows, "approvalState") is { ValueKind: JsonValueKind.Object } approval)
                    {
                        if (approval.TryGetProperty("turnOpen", out var openTurnEl) &&
                            openTurnEl.ValueKind == JsonValueKind.True)
                            turnOpen = true;

                        TryExtractDshPendingInteraction(sessionId, approval, out dshPendingQuestion, out dshPendingApproval);
                    }

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
                    if (idleSeconds >= 1800)
                    {
                        dshPendingQuestion = null;
                        dshPendingApproval = null;
                    }

                    var running = turnOpen && idleSeconds < 1800;
                    var phase = dshPendingQuestion != null
                        ? SessionPhase.WaitingForAnswer
                        : dshPendingApproval != null
                            ? SessionPhase.WaitingForApproval
                            : running
                                ? SessionPhase.Running
                                : idleSeconds < 30 ? SessionPhase.Completed : SessionPhase.Idle;

                    var title = !string.IsNullOrWhiteSpace(metaTitle) ? metaTitle : lastPrompt;
                    var statusSummary = dshPendingQuestion?.Questions.FirstOrDefault()?.Question
                        ?? dshPendingApproval?.Summary
                        ?? (!string.IsNullOrWhiteSpace(lastAssistant)
                            ? HookPayloadDecoder.Clip(lastAssistant, 160)
                            : !string.IsNullOrWhiteSpace(lastPrompt)
                                ? HookPayloadDecoder.Clip(lastPrompt, 160)
                                : "DeepSeek Harness session");

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
                        PendingQuestion = dshPendingQuestion,
                        PendingApproval = dshPendingApproval,
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

    // ------------------------------------------------------------------
    // Google Antigravity (Desktop 2.0 / IDE / CLI) local transcripts.
    // Documented path: ~/.gemini/antigravity*/brain/<uuid>/.system_generated/logs/transcript.jsonl
    // ------------------------------------------------------------------
    private static readonly (string RelativeBrainDir, string SurfaceName, bool IsDesktop)[] AntigravityRoots =
    [
        ("~/.gemini/antigravity/brain",     "Antigravity",     true),
        ("~/.gemini/antigravity-ide/brain", "Antigravity IDE", true),
        ("~/.gemini/antigravity-cli/brain", "Antigravity CLI", false)
    ];

    private static readonly HashSet<string> KnownNonQuestionAntigravityTools = new(StringComparer.OrdinalIgnoreCase)
    {
        "run_command", "execute_command", "write_file", "edit_file", "read_file", "view_file",
        "replace_file_content", "multi_replace_file_content", "search_web", "update_focus",
        "update_steps", "manage_task", "schedule", "swarm", "invoke_subagent", "define_subagent",
        "send_message", "manage_subagents", "run_workflow", "search_marketplace",
        "exit_plan_mode", "notify_user"
    };

    private void ScanAntigravitySessions(
        DateTime cutoffUtc,
        List<AgentSession> results,
        IReadOnlyDictionary<string, string?>? knownSessionCwds = null)
    {
        foreach (var (relBrainDir, surfaceName, isDesktop) in AntigravityRoots)
        {
            var brainRoot = AgentRegistry.ExpandHomePath(relBrainDir);
            if (!Directory.Exists(brainRoot)) continue;

            try
            {
                foreach (var convDir in Directory.EnumerateDirectories(brainRoot))
                {
                    var transcriptPath = Path.Combine(convDir, ".system_generated", "logs", "transcript.jsonl");
                    if (!File.Exists(transcriptPath)) continue;

                    try
                    {
                        var info = new FileInfo(transcriptPath);
                        if (info.LastWriteTimeUtc < cutoffUtc) continue;

                        var sessionId = Path.GetFileName(convDir);
                        if (string.IsNullOrWhiteSpace(sessionId)) continue;

                        string? sessionCwd = null;
                        knownSessionCwds?.TryGetValue(sessionId, out sessionCwd);
                        var policy = _antigravityPolicy.ResolvePolicy(surfaceName, sessionCwd);

                        var headLines = ReadHeadLines(transcriptPath, 6);
                        var tailLines = ReadTailLines(transcriptPath, 50).ToList();

                        bool hasExplicitUserInput = false;
                        string? firstUserPrompt = null;
                        string? lastPrompt = null;
                        string? lastAssistant = null;
                        string? model = null;
                        string? activeToolName = null;
                        string? lastType = null;
                        string? lastStatus = null;
                        bool lastHasToolCalls = false;
                        DateTimeOffset? createdAt = null;
                        DateTimeOffset? lastActivity = null;
                        PendingQuestionRequest? pendingQuestion = null;
                        PendingApprovalRequest? pendingApproval = null;
                        PendingApprovalRequest? pendingPlanFeedback = null;
                        int pendingQuestionStepIndex = -1;
                        int pendingApprovalStepIndex = -1;
                        string? pendingApprovalCommandLine = null;
                        int? pendingApprovalWaitMs = null;
                        bool hasExplicitWaitingStatus = false;
                        int pendingToolCallCount = 0;
                        int completedGenericSincePlanner = 0;
                        HashSet<int>? seenGenericSteps = null;

                        void ProcessLine(string line, bool isHead)
                        {
                            try
                            {
                                using var doc = JsonDocument.Parse(line);
                                var root = doc.RootElement;
                                if (root.ValueKind != JsonValueKind.Object) return;

                                var source = root.TryGetProperty("source", out var srcEl) && srcEl.ValueKind == JsonValueKind.String
                                    ? srcEl.GetString()
                                    : null;
                                var type = root.TryGetProperty("type", out var typeEl) && typeEl.ValueKind == JsonValueKind.String
                                    ? typeEl.GetString()
                                    : null;
                                var status = root.TryGetProperty("status", out var stEl) && stEl.ValueKind == JsonValueKind.String
                                    ? stEl.GetString()
                                    : null;
                                int stepIndex = root.TryGetProperty("step_index", out var siEl) && siEl.TryGetInt32(out var siVal)
                                    ? siVal
                                    : -1;

                                DateTimeOffset? lineTs = null;
                                if (root.TryGetProperty("created_at", out var tsEl) && tsEl.ValueKind == JsonValueKind.String &&
                                    DateTimeOffset.TryParse(tsEl.GetString(), System.Globalization.CultureInfo.InvariantCulture,
                                        System.Globalization.DateTimeStyles.AssumeUniversal | System.Globalization.DateTimeStyles.AdjustToUniversal,
                                        out var parsedTs))
                                {
                                    lineTs = parsedTs;
                                    if (isHead && createdAt is null) createdAt = parsedTs;
                                    if (!isHead && (lastActivity is null || parsedTs > lastActivity)) lastActivity = parsedTs;
                                }

                                if (string.Equals(type, "USER_INPUT", StringComparison.OrdinalIgnoreCase))
                                {
                                    var rawUserContent = root.TryGetProperty("content", out var cEl) && cEl.ValueKind == JsonValueKind.String
                                        ? cEl.GetString()
                                        : null;
                                    _antigravityPolicy.ApplyTranscriptSettingsChange(policy, rawUserContent);

                                    if (!isHead)
                                    {
                                        pendingQuestion = null;
                                        pendingApproval = null;
                                        pendingPlanFeedback = null;
                                        pendingQuestionStepIndex = -1;
                                        pendingApprovalStepIndex = -1;
                                        pendingApprovalCommandLine = null;
                                        pendingApprovalWaitMs = null;
                                        hasExplicitWaitingStatus = false;
                                        pendingToolCallCount = 0;
                                        completedGenericSincePlanner = 0;
                                        seenGenericSteps?.Clear();
                                    }
                                    if (string.Equals(source, "USER_EXPLICIT", StringComparison.OrdinalIgnoreCase))
                                    {
                                        hasExplicitUserInput = true;
                                        if (!string.IsNullOrWhiteSpace(rawUserContent))
                                        {
                                            var extracted = ExtractAntigravityUserPrompt(rawUserContent);
                                            if (!string.IsNullOrWhiteSpace(extracted))
                                            {
                                                firstUserPrompt ??= extracted;
                                                if (!isHead) lastPrompt = extracted;
                                            }
                                        }
                                    }
                                }
                                else if (!isHead && string.Equals(type, "PLANNER_RESPONSE", StringComparison.OrdinalIgnoreCase))
                                {
                                    pendingQuestion = null;
                                    pendingApproval = null;
                                    pendingQuestionStepIndex = -1;
                                    pendingApprovalStepIndex = -1;
                                    pendingApprovalCommandLine = null;
                                    pendingApprovalWaitMs = null;
                                    hasExplicitWaitingStatus = false;
                                    pendingToolCallCount = 0;
                                    completedGenericSincePlanner = 0;
                                    seenGenericSteps?.Clear();

                                    if (root.TryGetProperty("model", out var mEl) && mEl.ValueKind == JsonValueKind.String &&
                                        !string.IsNullOrWhiteSpace(mEl.GetString()))
                                    {
                                        model = mEl.GetString();
                                    }

                                    if (root.TryGetProperty("content", out var respEl) && respEl.ValueKind == JsonValueKind.String &&
                                        !string.IsNullOrWhiteSpace(respEl.GetString()))
                                    {
                                        lastAssistant = HookPayloadDecoder.Clip(respEl.GetString(), 240);
                                    }

                                    bool hasTools = false;
                                    string? toolName = null;
                                    int toolCallCount = 0;
                                    List<PendingQuestionItem>? askQuestions = null;
                                    int newQuestionStepIndex = -1;
                                    PendingApprovalRequest? newStepApproval = null;
                                    int newStepApprovalStepIndex = -1;
                                    string? newStepApprovalCommandLine = null;
                                    int? newStepApprovalWaitMs = null;
                                    PendingApprovalRequest? newPlanFeedback = null;

                                    var stepKey = stepIndex >= 0
                                        ? stepIndex.ToString(System.Globalization.CultureInfo.InvariantCulture)
                                        : (lineTs ?? DateTimeOffset.UtcNow).ToUnixTimeSeconds().ToString(System.Globalization.CultureInfo.InvariantCulture);

                                    if (root.TryGetProperty("tool_calls", out var tcEl) && tcEl.ValueKind == JsonValueKind.Array)
                                    {
                                        foreach (var tc in tcEl.EnumerateArray())
                                        {
                                            int toolCallOffset = toolCallCount;
                                            hasTools = true;
                                            toolCallCount++;
                                            if (tc.ValueKind != JsonValueKind.Object ||
                                                !tc.TryGetProperty("name", out var nEl) || nEl.ValueKind != JsonValueKind.String)
                                            {
                                                continue;
                                            }

                                            var currentTool = nEl.GetString() ?? "";
                                            toolName = currentTool;
                                            var hasArgs = tc.TryGetProperty("args", out var argsEl) && argsEl.ValueKind == JsonValueKind.Object;
                                            int predictedStepIndex = stepIndex >= 0 ? stepIndex + 1 + toolCallOffset : -1;

                                            // 1. Structured question / option tool (ask_question, AskUserQuestion, or any tool with questions/options/choices)
                                            if (HookPayloadDecoder.IsQuestionToolName(currentTool))
                                            {
                                                askQuestions ??= new List<PendingQuestionItem>();
                                                if (hasArgs) HookPayloadDecoder.ExtractQuestions(argsEl, askQuestions);
                                                newQuestionStepIndex = predictedStepIndex;
                                            }
                                            else if (hasArgs && !KnownNonQuestionAntigravityTools.Contains(currentTool))
                                            {
                                                // Any other tool that explicitly carries questions or selectable options/choices
                                                var genericQs = new List<PendingQuestionItem>();
                                                HookPayloadDecoder.ExtractQuestions(argsEl, genericQs);
                                                if (genericQs.Count > 0)
                                                {
                                                    askQuestions ??= new List<PendingQuestionItem>();
                                                    askQuestions.AddRange(genericQs);
                                                    newQuestionStepIndex = predictedStepIndex;
                                                }
                                            }

                                            // 2. Command execution requiring manual user approval (BypassSandbox: true or SafeToAutoRun: false)
                                            if (hasArgs &&
                                                (string.Equals(currentTool, "run_command", StringComparison.OrdinalIgnoreCase) ||
                                                 string.Equals(currentTool, "execute_command", StringComparison.OrdinalIgnoreCase)))
                                            {
                                                bool bypassSandbox = TryParseAntigravityArgBool(argsEl, "BypassSandbox") == true;
                                                bool unsafeAutoRun = TryParseAntigravityArgBool(argsEl, "SafeToAutoRun") == false;
                                                if (bypassSandbox || unsafeAutoRun)
                                                {
                                                    var cmdLine = TryParseAntigravityArgString(argsEl, "CommandLine", "command", "Command");
                                                    var summary = TryParseAntigravityArgString(argsEl, "toolSummary", "Description", "description")
                                                                  ?? Localization.Strings.Get("CommandApprovalSummary");
                                                    newStepApprovalStepIndex = predictedStepIndex;
                                                    newStepApprovalCommandLine = cmdLine;
                                                    newStepApprovalWaitMs = TryParseAntigravityArgInt(argsEl, "WaitMsBeforeAsync");
                                                    newStepApproval = new PendingApprovalRequest
                                                    {
                                                        RequestId = $"aga-{sessionId}-{stepKey}",
                                                        SessionId = sessionId,
                                                        Source = AgentSource.Antigravity,
                                                        HookEventName = "PermissionRequest",
                                                        ToolName = currentTool,
                                                        Summary = HookPayloadDecoder.Clip(summary, 160),
                                                        CommandOrDetails = HookPayloadDecoder.Clip(cmdLine, 260),
                                                        Options =
                                                        [
                                                            new QuestionOption { Label = Localization.Strings.Get("OptionAllowOnce") },
                                                            new QuestionOption { Label = Localization.Strings.Get("OptionAllowAlways") },
                                                            new QuestionOption { Label = Localization.Strings.Get("OptionDeny") }
                                                        ],
                                                        IsBlocking = false,
                                                        CreatedAt = lineTs ?? DateTimeOffset.UtcNow
                                                    };
                                                }
                                            }

                                            // 3. Plan / artifact feedback request (write_file / edit_file with ArtifactMetadata.RequestFeedback: true -> Proceed button)
                                            if (hasArgs &&
                                                (string.Equals(currentTool, "write_file", StringComparison.OrdinalIgnoreCase) ||
                                                 string.Equals(currentTool, "edit_file", StringComparison.OrdinalIgnoreCase)) &&
                                                TryExtractArtifactFeedbackRequest(argsEl, out var artifactName, out var artifactSummary))
                                            {
                                                newPlanFeedback = new PendingApprovalRequest
                                                {
                                                    RequestId = $"agp-{sessionId}-{stepKey}",
                                                    SessionId = sessionId,
                                                    Source = AgentSource.Antigravity,
                                                    HookEventName = "PermissionRequest",
                                                    ToolName = string.IsNullOrWhiteSpace(artifactName) ? "Plan" : artifactName,
                                                    Summary = !string.IsNullOrWhiteSpace(artifactSummary)
                                                        ? HookPayloadDecoder.Clip(artifactSummary, 160)
                                                        : Localization.Strings.Get("PlanApprovalSummary"),
                                                    Options =
                                                    [
                                                        new QuestionOption { Label = Localization.Strings.Get("OptionProceedPlan") },
                                                        new QuestionOption { Label = Localization.Strings.Get("OptionRequestChanges") }
                                                    ],
                                                    IsBlocking = false,
                                                    CreatedAt = lineTs ?? DateTimeOffset.UtcNow
                                                };
                                            }

                                            // 4. notify_user with BlockedOnUser: true (Antigravity IDE / Agent Manager review confirmation)
                                            if (hasArgs &&
                                                string.Equals(currentTool, "notify_user", StringComparison.OrdinalIgnoreCase) &&
                                                TryParseAntigravityArgBool(argsEl, "BlockedOnUser") == true)
                                            {
                                                var notifyMsg = TryParseAntigravityArgString(argsEl, "Message", "message")
                                                                ?? Localization.Strings.Get("PlanApprovalSummary");
                                                newPlanFeedback = new PendingApprovalRequest
                                                {
                                                    RequestId = $"agn-{sessionId}-{stepKey}",
                                                    SessionId = sessionId,
                                                    Source = AgentSource.Antigravity,
                                                    HookEventName = "PermissionRequest",
                                                    ToolName = "notify_user",
                                                    Summary = HookPayloadDecoder.Clip(notifyMsg, 180),
                                                    Options =
                                                    [
                                                        new QuestionOption { Label = Localization.Strings.Get("OptionProceedPlan") },
                                                        new QuestionOption { Label = Localization.Strings.Get("OptionRequestChanges") }
                                                    ],
                                                    IsBlocking = false,
                                                    CreatedAt = lineTs ?? DateTimeOffset.UtcNow
                                                };
                                            }
                                        }
                                    }

                                    // If tool_calls was truncated in compact transcript.jsonl, recover full ask_question args from transcript_full.jsonl.
                                    if (askQuestions != null && askQuestions.Count == 0 && stepIndex >= 0)
                                    {
                                        TryRecoverAntigravityQuestionsFromFullTranscript(transcriptPath, stepIndex, askQuestions);
                                    }

                                    if (askQuestions != null)
                                    {
                                        if (askQuestions.Count == 0)
                                        {
                                            askQuestions.Add(new PendingQuestionItem
                                            {
                                                Header = surfaceName,
                                                Question = Localization.Strings.Get("QuestionHint")
                                            });
                                        }
                                        pendingQuestion = new PendingQuestionRequest
                                        {
                                            RequestId = $"agq-{sessionId}-{stepKey}",
                                            SessionId = sessionId,
                                            Source = AgentSource.Antigravity,
                                            Questions = askQuestions,
                                            IsBlocking = false,
                                            CreatedAt = lineTs ?? DateTimeOffset.UtcNow
                                        };
                                        pendingQuestionStepIndex = newQuestionStepIndex;
                                        pendingToolCallCount = Math.Max(1, toolCallCount);
                                        toolName = "ask_question";
                                    }
                                    else if (newStepApproval != null)
                                    {
                                        pendingApproval = newStepApproval;
                                        pendingApprovalStepIndex = newStepApprovalStepIndex;
                                        pendingApprovalCommandLine = newStepApprovalCommandLine;
                                        pendingApprovalWaitMs = newStepApprovalWaitMs;
                                        pendingToolCallCount = Math.Max(1, toolCallCount);
                                    }

                                    if (newPlanFeedback != null)
                                    {
                                        pendingPlanFeedback = newPlanFeedback;
                                    }
                                    else if (hasTools)
                                    {
                                        // Agent kept executing new tools after the plan artifact; clear old plan feedback banner.
                                        pendingPlanFeedback = null;
                                    }

                                    lastHasToolCalls = hasTools;
                                    activeToolName = hasTools ? toolName : null;
                                }
                                else if (!isHead && string.Equals(type, "GENERIC", StringComparison.OrdinalIgnoreCase))
                                {
                                    var genericContent = root.TryGetProperty("content", out var gcEl) && gcEl.ValueKind == JsonValueKind.String
                                        ? gcEl.GetString() ?? ""
                                        : "";
                                    bool stepFinished =
                                        string.Equals(status, "DONE", StringComparison.OrdinalIgnoreCase) ||
                                        string.Equals(status, "ERROR", StringComparison.OrdinalIgnoreCase) ||
                                        string.Equals(status, "CANCELLED", StringComparison.OrdinalIgnoreCase) ||
                                        string.Equals(status, "CANCELED", StringComparison.OrdinalIgnoreCase);

                                    // For run_command approvals, once Antigravity writes a RUNNING step or DONE/ERROR/CANCELLED
                                    // command output, the command has already been cleared to execute or was rejected.
                                    bool commandAlreadyStarted =
                                        stepFinished ||
                                        string.Equals(status, "RUNNING", StringComparison.OrdinalIgnoreCase);

                                    bool countedStep = false;
                                    void CountGenericStepOnce()
                                    {
                                        if (countedStep) return;
                                        countedStep = true;
                                        if (stepIndex < 0)
                                        {
                                            completedGenericSincePlanner++;
                                            return;
                                        }
                                        seenGenericSteps ??= new HashSet<int>();
                                        if (seenGenericSteps.Add(stepIndex))
                                        {
                                            completedGenericSincePlanner++;
                                        }
                                    }

                                    if (pendingQuestion != null && stepFinished)
                                    {
                                        CountGenericStepOnce();
                                        bool exactStepMatch = pendingQuestionStepIndex >= 0 && stepIndex == pendingQuestionStepIndex;
                                        bool looksLikeQuestionAnswer =
                                            genericContent.Contains("\nA1:", StringComparison.Ordinal) ||
                                            genericContent.StartsWith("A1:", StringComparison.Ordinal) ||
                                            genericContent.Contains("User cancelled", StringComparison.OrdinalIgnoreCase);

                                        if (exactStepMatch ||
                                            (pendingQuestionStepIndex < 0 && pendingToolCallCount <= 1) ||
                                            completedGenericSincePlanner >= pendingToolCallCount ||
                                            looksLikeQuestionAnswer)
                                        {
                                            pendingQuestion = null;
                                            pendingQuestionStepIndex = -1;
                                            pendingToolCallCount = 0;
                                            completedGenericSincePlanner = 0;
                                            seenGenericSteps?.Clear();
                                        }
                                    }

                                    if (pendingApproval != null && commandAlreadyStarted)
                                    {
                                        CountGenericStepOnce();
                                        bool exactStepMatch = pendingApprovalStepIndex >= 0 && stepIndex == pendingApprovalStepIndex;
                                        if (exactStepMatch ||
                                            (pendingApprovalStepIndex < 0 && pendingToolCallCount <= 1) ||
                                            completedGenericSincePlanner >= pendingToolCallCount)
                                        {
                                            pendingApproval = null;
                                            pendingApprovalStepIndex = -1;
                                            pendingApprovalCommandLine = null;
                                            pendingApprovalWaitMs = null;
                                            hasExplicitWaitingStatus = false;
                                            pendingToolCallCount = 0;
                                            completedGenericSincePlanner = 0;
                                            seenGenericSteps?.Clear();
                                        }
                                    }

                                    if (IsWaitingStatus(status))
                                    {
                                        hasExplicitWaitingStatus = true;
                                        if (pendingQuestion == null && pendingApproval == null)
                                        {
                                            var stepKey = stepIndex >= 0
                                                ? stepIndex.ToString(System.Globalization.CultureInfo.InvariantCulture)
                                                : (lineTs ?? DateTimeOffset.UtcNow).ToUnixTimeSeconds().ToString(System.Globalization.CultureInfo.InvariantCulture);
                                            pendingApproval = new PendingApprovalRequest
                                            {
                                                RequestId = $"agw-{sessionId}-{stepKey}",
                                                SessionId = sessionId,
                                                Source = AgentSource.Antigravity,
                                                HookEventName = "PermissionRequest",
                                                ToolName = activeToolName ?? surfaceName,
                                                Summary = !string.IsNullOrWhiteSpace(genericContent)
                                                    ? HookPayloadDecoder.Clip(genericContent, 160)
                                                    : Localization.Strings.Get("ApproveHint"),
                                                Options =
                                                [
                                                    new QuestionOption { Label = Localization.Strings.Get("OptionAllowOnce") },
                                                    new QuestionOption { Label = Localization.Strings.Get("OptionDeny") }
                                                ],
                                                IsBlocking = false,
                                                CreatedAt = lineTs ?? DateTimeOffset.UtcNow
                                            };
                                        }
                                    }
                                }
                                else if (!isHead && string.Equals(type, "ERROR_MESSAGE", StringComparison.OrdinalIgnoreCase))
                                {
                                    pendingQuestion = null;
                                    pendingApproval = null;
                                    pendingPlanFeedback = null;
                                    pendingQuestionStepIndex = -1;
                                    pendingApprovalStepIndex = -1;
                                    pendingApprovalCommandLine = null;
                                    pendingApprovalWaitMs = null;
                                    hasExplicitWaitingStatus = false;
                                    pendingToolCallCount = 0;
                                    completedGenericSincePlanner = 0;
                                    seenGenericSteps?.Clear();
                                }

                                if (!isHead)
                                {
                                    lastType = type;
                                    lastStatus = status;
                                    if (!string.Equals(type, "PLANNER_RESPONSE", StringComparison.OrdinalIgnoreCase))
                                    {
                                        lastHasToolCalls = false;
                                        if (string.Equals(type, "USER_INPUT", StringComparison.OrdinalIgnoreCase))
                                        {
                                            activeToolName = null;
                                        }
                                    }
                                }
                            }
                            catch
                            {
                                // Live transcript line may be partially flushed; ignore malformed lines.
                            }
                        }

                        foreach (var line in headLines) ProcessLine(line, isHead: true);
                        foreach (var line in tailLines) ProcessLine(line, isHead: false);

                        // Skip background subagent/swarm transcripts that never had explicit user input.
                        if (!hasExplicitUserInput) continue;

                        var updatedAt = lastActivity ?? new DateTimeOffset(info.LastWriteTimeUtc, TimeSpan.Zero);
                        if (info.LastWriteTimeUtc > updatedAt.UtcDateTime)
                        {
                            updatedAt = new DateTimeOffset(info.LastWriteTimeUtc, TimeSpan.Zero);
                        }
                        if (updatedAt.UtcDateTime < cutoffUtc) continue;

                        var idleSeconds = (DateTimeOffset.UtcNow - updatedAt).TotalSeconds;
                        if (pendingApproval != null &&
                            pendingApproval.RequestId.StartsWith("aga-", StringComparison.OrdinalIgnoreCase) &&
                            !_antigravityPolicy.ShouldSurfaceCommandApproval(
                                policy,
                                pendingApprovalCommandLine,
                                pendingApprovalWaitMs,
                                hasExplicitWaitingStatus,
                                idleSeconds))
                        {
                            pendingApproval = null;
                        }

                        if (pendingPlanFeedback != null &&
                            !_antigravityPolicy.ShouldSurfacePlanFeedback(
                                policy,
                                pendingPlanFeedback.RequestId,
                                lastType,
                                lastStatus,
                                lastHasToolCalls,
                                hasExplicitWaitingStatus,
                                idleSeconds))
                        {
                            pendingPlanFeedback = null;
                        }

                        // If no live tool approval is active, surface a pending plan/artifact feedback card (Proceed).
                        var effectiveApproval = pendingApproval ?? pendingPlanFeedback;
                        if (idleSeconds >= 1800)
                        {
                            // Avoid surfacing abandoned questions/approvals older than 30 minutes.
                            pendingQuestion = null;
                            effectiveApproval = null;
                        }

                        SessionPhase phase;
                        if (pendingQuestion != null)
                        {
                            phase = SessionPhase.WaitingForAnswer;
                        }
                        else if (effectiveApproval != null)
                        {
                            phase = SessionPhase.WaitingForApproval;
                        }
                        else if (string.Equals(lastType, "ERROR_MESSAGE", StringComparison.OrdinalIgnoreCase) ||
                                 string.Equals(lastStatus, "ERROR", StringComparison.OrdinalIgnoreCase))
                        {
                            phase = SessionPhase.Error;
                        }
                        else if (string.Equals(lastType, "USER_INPUT", StringComparison.OrdinalIgnoreCase) ||
                                 string.Equals(lastType, "GENERIC", StringComparison.OrdinalIgnoreCase) ||
                                 (string.Equals(lastType, "PLANNER_RESPONSE", StringComparison.OrdinalIgnoreCase) &&
                                  (lastHasToolCalls || !string.Equals(lastStatus, "DONE", StringComparison.OrdinalIgnoreCase))))
                        {
                            phase = idleSeconds < 120 ? SessionPhase.Running : SessionPhase.Idle;
                        }
                        else if (string.Equals(lastType, "PLANNER_RESPONSE", StringComparison.OrdinalIgnoreCase))
                        {
                            phase = idleSeconds < 30 ? SessionPhase.Completed : SessionPhase.Idle;
                        }
                        else
                        {
                            phase = idleSeconds < 25 ? SessionPhase.Running : SessionPhase.Idle;
                        }

                        var effectivePrompt = lastPrompt ?? firstUserPrompt;
                        var title = HookPayloadDecoder.Clip(firstUserPrompt ?? effectivePrompt, 90);
                        var statusSummary = pendingQuestion != null
                            ? HookPayloadDecoder.Clip(pendingQuestion.Questions.FirstOrDefault()?.Question ?? Localization.Strings.Get("QuestionHint"), 160)
                            : effectiveApproval != null
                                ? HookPayloadDecoder.Clip(effectiveApproval.Summary, 160)
                                : phase == SessionPhase.Running && !string.IsNullOrWhiteSpace(activeToolName)
                                    ? $"Running {activeToolName}"
                                    : !string.IsNullOrWhiteSpace(lastAssistant)
                                        ? HookPayloadDecoder.Clip(lastAssistant, 160)
                                        : !string.IsNullOrWhiteSpace(effectivePrompt)
                                            ? HookPayloadDecoder.Clip(effectivePrompt, 160)
                                            : $"{surfaceName} session";

                        results.Add(new AgentSession
                        {
                            SessionId = sessionId,
                            Source = AgentSource.Antigravity,
                            Title = title,
                            Model = model,
                            TranscriptPath = transcriptPath,
                            ActiveToolName = phase is SessionPhase.Running or SessionPhase.WaitingForAnswer or SessionPhase.WaitingForApproval ? activeToolName : null,
                            LastPrompt = HookPayloadDecoder.Clip(effectivePrompt, 160),
                            LastAssistantMessage = lastAssistant,
                            StatusSummary = statusSummary,
                            Phase = phase,
                            PendingQuestion = pendingQuestion,
                            PendingApproval = effectiveApproval,
                            UpdatedAt = updatedAt,
                            CreatedAt = createdAt ?? new DateTimeOffset(info.CreationTimeUtc, TimeSpan.Zero),
                            Terminal = new TerminalMetadata
                            {
                                IsDesktopApp = isDesktop,
                                DesktopAppName = isDesktop ? surfaceName : null,
                                TerminalApp = surfaceName
                            }
                        });
                    }
                    catch
                    {
                        // Ignore unreadable conversation directories.
                    }
                }
            }
            catch (Exception ex)
            {
                AppLog.Warn($"Antigravity session scan failed ({brainRoot}): {ex.Message}");
            }
        }
    }

    private static void TryExtractDshPendingInteraction(
        string sessionId,
        JsonElement approvalState,
        out PendingQuestionRequest? question,
        out PendingApprovalRequest? approval)
    {
        question = null;
        approval = null;

        // Check nested pending request containers or root approvalState object
        JsonElement container = approvalState;
        bool hasExplicitPendingObj = false;
        bool matchedQuestionKey = false;
        foreach (var key in new[] { "pendingQuestion", "question", "pendingApproval", "pendingRequest", "pending", "currentRequest", "request", "approval" })
        {
            if (approvalState.TryGetProperty(key, out var sub) && sub.ValueKind == JsonValueKind.Object)
            {
                container = sub;
                hasExplicitPendingObj = true;
                matchedQuestionKey = key is "pendingQuestion" or "question";
                break;
            }
        }

        bool isWaiting = hasExplicitPendingObj;
        if (!isWaiting)
        {
            if (approvalState.TryGetProperty("waitingForApproval", out var wfa) && wfa.ValueKind == JsonValueKind.True) isWaiting = true;
            if (approvalState.TryGetProperty("waitingForUser", out var wfu) && wfu.ValueKind == JsonValueKind.True) isWaiting = true;
            if (approvalState.TryGetProperty("pendingCount", out var pc) && pc.TryGetInt32(out var pcVal) && pcVal > 0) isWaiting = true;
            if (approvalState.TryGetProperty("status", out var st) && st.ValueKind == JsonValueKind.String && IsWaitingStatus(st.GetString())) isWaiting = true;
        }
        if (!isWaiting) return;

        var rawSig = container.GetRawText();
        var hash = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(rawSig)))[..10].ToLowerInvariant();

        bool looksLikeQuestion = matchedQuestionKey
            || container.TryGetProperty("questions", out _)
            || container.TryGetProperty("question", out _);

        if (looksLikeQuestion)
        {
            var qs = new List<PendingQuestionItem>();
            HookPayloadDecoder.ExtractQuestions(container, qs);
            if (qs.Count == 0 && hasExplicitPendingObj)
            {
                HookPayloadDecoder.ExtractQuestions(approvalState, qs);
            }

            if (qs.Count > 0)
            {
                question = new PendingQuestionRequest
                {
                    RequestId = $"dsq-{sessionId}-{hash}",
                    SessionId = sessionId,
                    Source = AgentSource.DeepSeekHarness,
                    Questions = qs,
                    IsBlocking = false,
                    CreatedAt = DateTimeOffset.UtcNow
                };
                return;
            }
        }

        var opts = new List<QuestionOption>();
        HookPayloadDecoder.ExtractApprovalOptions(container, opts);
        if (opts.Count == 0)
        {
            opts.Add(new QuestionOption { Label = Localization.Strings.Get("OptionAllowOnce") });
            opts.Add(new QuestionOption { Label = Localization.Strings.Get("OptionDeny") });
        }

        var toolName = TryParseAntigravityArgString(container, "toolName", "tool_name", "tool", "action") ?? "DeepSeek Harness";
        var summary = TryParseAntigravityArgString(container, "summary", "title", "message", "description", "reason")
                      ?? Localization.Strings.Get("ApproveHint");
        var details = TryParseAntigravityArgString(container, "command", "CommandLine", "details", "input", "path");

        approval = new PendingApprovalRequest
        {
            RequestId = $"dsa-{sessionId}-{hash}",
            SessionId = sessionId,
            Source = AgentSource.DeepSeekHarness,
            HookEventName = "PermissionRequest",
            ToolName = toolName,
            Summary = HookPayloadDecoder.Clip(summary, 160),
            CommandOrDetails = HookPayloadDecoder.Clip(details, 260),
            Options = opts,
            IsBlocking = false,
            CreatedAt = DateTimeOffset.UtcNow
        };
    }

    private static bool IsWaitingStatus(string? status)
    {
        if (string.IsNullOrWhiteSpace(status)) return false;
        var s = status.Trim().ToUpperInvariant();
        return s.Contains("WAIT") || s.Contains("PENDING") || s.Contains("APPROV") || s.Contains("BLOCKED");
    }

    private static bool? TryParseAntigravityArgBool(JsonElement argsObj, string propertyName)
    {
        if (!argsObj.TryGetProperty(propertyName, out var el)) return null;
        if (el.ValueKind == JsonValueKind.True) return true;
        if (el.ValueKind == JsonValueKind.False) return false;
        if (el.ValueKind == JsonValueKind.String)
        {
            var raw = UnquoteJsonArgString(el.GetString());
            if (bool.TryParse(raw, out var b)) return b;
        }
        return null;
    }

    private static int? TryParseAntigravityArgInt(JsonElement argsObj, string propertyName)
    {
        if (!argsObj.TryGetProperty(propertyName, out var el)) return null;
        if (el.ValueKind == JsonValueKind.Number && el.TryGetInt32(out var n)) return n;
        if (el.ValueKind == JsonValueKind.String)
        {
            var raw = UnquoteJsonArgString(el.GetString());
            if (int.TryParse(raw, System.Globalization.NumberStyles.Integer,
                    System.Globalization.CultureInfo.InvariantCulture, out var parsed))
            {
                return parsed;
            }
        }
        return null;
    }

    private static string? TryParseAntigravityArgString(JsonElement argsObj, params string[] propertyNames)
    {
        foreach (var name in propertyNames)
        {
            if (argsObj.TryGetProperty(name, out var el) && el.ValueKind == JsonValueKind.String)
            {
                var unquoted = UnquoteJsonArgString(el.GetString());
                if (!string.IsNullOrWhiteSpace(unquoted)) return unquoted;
            }
        }
        return null;
    }

    private static string UnquoteJsonArgString(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return "";
        var trimmed = raw.Trim();
        if (trimmed.Length >= 2 && trimmed[0] == '"' && trimmed[^1] == '"')
        {
            try
            {
                var decoded = JsonSerializer.Deserialize<string>(trimmed);
                if (decoded != null) return decoded.Trim();
            }
            catch
            {
                return trimmed[1..^1].Trim();
            }
        }
        return trimmed;
    }

    private static bool TryExtractArtifactFeedbackRequest(
        JsonElement argsObj,
        out string? artifactName,
        out string? summary)
    {
        artifactName = null;
        summary = null;
        if (!argsObj.TryGetProperty("ArtifactMetadata", out var metaEl)) return false;

        try
        {
            JsonElement metaObj;
            JsonDocument? parsedDoc = null;
            try
            {
                if (metaEl.ValueKind == JsonValueKind.Object)
                {
                    metaObj = metaEl;
                }
                else if (metaEl.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(metaEl.GetString()))
                {
                    parsedDoc = JsonDocument.Parse(metaEl.GetString()!);
                    metaObj = parsedDoc.RootElement;
                }
                else
                {
                    return false;
                }

                if (metaObj.ValueKind != JsonValueKind.Object) return false;
                bool reqFeedback = TryParseAntigravityArgBool(metaObj, "RequestFeedback") == true;
                if (!reqFeedback) return false;

                summary = TryParseAntigravityArgString(metaObj, "Summary", "summary");
                var path = TryParseAntigravityArgString(argsObj, "Path", "path");
                if (!string.IsNullOrWhiteSpace(path))
                {
                    artifactName = Path.GetFileName(path);
                }
                return true;
            }
            finally
            {
                parsedDoc?.Dispose();
            }
        }
        catch
        {
            return false;
        }
    }

    private static void TryRecoverAntigravityQuestionsFromFullTranscript(
        string compactTranscriptPath,
        int targetStepIndex,
        List<PendingQuestionItem> target)
    {
        try
        {
            var dir = Path.GetDirectoryName(compactTranscriptPath);
            if (string.IsNullOrWhiteSpace(dir)) return;
            var fullPath = Path.Combine(dir, "transcript_full.jsonl");
            if (!File.Exists(fullPath)) return;

            var marker = $"\"step_index\":{targetStepIndex},";
            foreach (var line in ReadTailLines(fullPath, 60))
            {
                if (!line.Contains(marker, StringComparison.Ordinal)) continue;
                try
                {
                    using var doc = JsonDocument.Parse(line);
                    var root = doc.RootElement;
                    if (root.TryGetProperty("tool_calls", out var tcEl) && tcEl.ValueKind == JsonValueKind.Array)
                    {
                        foreach (var tc in tcEl.EnumerateArray())
                        {
                            if (tc.ValueKind == JsonValueKind.Object &&
                                tc.TryGetProperty("name", out var nEl) &&
                                nEl.ValueKind == JsonValueKind.String &&
                                HookPayloadDecoder.IsQuestionToolName(nEl.GetString()) &&
                                tc.TryGetProperty("args", out var argsEl))
                            {
                                HookPayloadDecoder.ExtractQuestions(argsEl, target);
                            }
                        }
                    }
                }
                catch { }
                break;
            }
        }
        catch { }
    }

    internal static string ExtractAntigravityUserPrompt(string? rawContent)
    {
        if (string.IsNullOrWhiteSpace(rawContent)) return "";

        var startTag = "<USER_REQUEST>";
        var endTag = "</USER_REQUEST>";
        var startIdx = rawContent.IndexOf(startTag, StringComparison.OrdinalIgnoreCase);
        if (startIdx >= 0)
        {
            var bodyStart = startIdx + startTag.Length;
            var endIdx = rawContent.IndexOf(endTag, bodyStart, StringComparison.OrdinalIgnoreCase);
            var extracted = endIdx >= 0 ? rawContent[bodyStart..endIdx] : rawContent[bodyStart..];
            return HookPayloadDecoder.Clip(extracted, 160);
        }

        // Strip trailing <ADDITIONAL_METADATA> / <USER_SETTINGS_CHANGE> blocks if present without wrapper.
        var metaIdx = rawContent.IndexOf("<ADDITIONAL_METADATA>", StringComparison.OrdinalIgnoreCase);
        var cleaned = metaIdx > 0 ? rawContent[..metaIdx] : rawContent;
        return HookPayloadDecoder.Clip(cleaned, 160);
    }

    private static List<string> ReadHeadLines(string path, int maxLines)
    {
        var list = new List<string>(maxLines);
        try
        {
            using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            using var reader = new StreamReader(fs);
            while (list.Count < maxLines)
            {
                var line = reader.ReadLine();
                if (line is null) break;
                if (!string.IsNullOrWhiteSpace(line)) list.Add(line);
            }
        }
        catch { }
        return list;
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

    public static List<UsageWindowInfo> ReadUsageWindows(
        Setup.HookInstallManager? setupManager = null,
        IReadOnlyDictionary<AgentSource, bool>? connectionOverrides = null)
    {
        var manager = setupManager ?? new Setup.HookInstallManager();

        bool IsConnected(AgentSource source)
        {
            if (connectionOverrides != null && connectionOverrides.TryGetValue(source, out var overridden))
            {
                return overridden;
            }
            try
            {
                var st = manager.GetStatus(AgentRegistry.Get(source));
                return st.Status == Setup.HookInstallStatus.Installed ||
                       st.Status == Setup.HookInstallStatus.Partial;
            }
            catch
            {
                return false;
            }
        }

        return
        [
            ReadClaudeUsage(IsConnected(AgentSource.Claude)),
            ReadCodexUsage(IsConnected(AgentSource.Codex)),
            ReadAntigravityUsage(IsConnected(AgentSource.Antigravity)),
            ReadDeepSeekHarnessUsage(IsConnected(AgentSource.DeepSeekHarness))
        ];
    }

    private static UsageWindowInfo ReadClaudeUsage(bool isConnected)
    {
        var nowUtc = DateTimeOffset.UtcNow;
        var fiveHourCutoff = nowUtc.AddHours(-5);
        var sevenDayCutoff = nowUtc.AddDays(-7);

        var usage = new UsageWindowInfo
        {
            Source = AgentSource.Claude,
            AgentName = "Claude Code",
            IsConnected = isConnected,
            SupportsRemainingQuota = true,
            Note = Localization.Strings.Get("UsageClaudeQuotaHint")
        };

        // 1. Rate-limit quota from managed statusLine cache (%TEMP%\open-island-rl.json)
        if (File.Exists(BridgeConstants.ClaudeRateLimitCachePath))
        {
            try
            {
                var json = File.ReadAllText(BridgeConstants.ClaudeRateLimitCachePath);
                using var doc = JsonDocument.Parse(json);
                var root = doc.RootElement;
                bool foundAny = false;
                if (root.TryGetProperty("five_hour", out var fh) && fh.ValueKind == JsonValueKind.Object &&
                    fh.TryGetProperty("utilization", out var fhVal) && fhVal.TryGetDouble(out var fhPct))
                {
                    usage.FiveHourPercent = fhPct;
                    usage.FiveHourResetText = TryGetResetString(fh);
                    foundAny = true;
                }
                if (root.TryGetProperty("seven_day", out var sd) && sd.ValueKind == JsonValueKind.Object &&
                    sd.TryGetProperty("utilization", out var sdVal) && sdVal.TryGetDouble(out var sdPct))
                {
                    usage.SevenDayPercent = sdPct;
                    usage.SevenDayResetText = TryGetResetString(sd);
                    foundAny = true;
                }
                if (foundAny)
                {
                    usage.HasQuotaData = true;
                    usage.LastUpdated = new FileInfo(BridgeConstants.ClaudeRateLimitCachePath).LastWriteTime;
                    usage.Note = null;
                }
            }
            catch { }
        }

        // 2. Local project transcript token & model usage (~/.claude/projects/**/*.jsonl)
        var baseDir = AgentRegistry.ExpandHomePath("~/.claude/projects");
        var perModel = new Dictionary<string, ModelUsageBreakdown>(StringComparer.OrdinalIgnoreCase);
        DateTimeOffset? latestTokenActivity = null;

        if (Directory.Exists(baseDir))
        {
            try
            {
                foreach (var file in Directory.EnumerateFiles(baseDir, "*.jsonl", SearchOption.AllDirectories))
                {
                    try
                    {
                        var info = new FileInfo(file);
                        if (info.LastWriteTimeUtc < sevenDayCutoff.UtcDateTime) continue;

                        var byMessageId = new Dictionary<string, (DateTimeOffset Ts, string Model, long Input, long Cache, long Output)>(StringComparer.Ordinal);
                        var anonEntries = new List<(DateTimeOffset Ts, string Model, long Input, long Cache, long Output)>();

                        using var fs = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                        using var reader = new StreamReader(fs);
                        string? line;
                        while ((line = reader.ReadLine()) != null)
                        {
                            if (!line.Contains("\"usage\"", StringComparison.Ordinal)) continue;
                            try
                            {
                                using var doc = JsonDocument.Parse(line);
                                var root = doc.RootElement;
                                if (root.ValueKind != JsonValueKind.Object) continue;
                                if (!root.TryGetProperty("message", out var msgEl) || msgEl.ValueKind != JsonValueKind.Object)
                                    continue;
                                if (!msgEl.TryGetProperty("usage", out var uEl) || uEl.ValueKind != JsonValueKind.Object)
                                    continue;

                                var ts = TryParseJsonTimestamp(root, "timestamp")
                                         ?? new DateTimeOffset(info.LastWriteTimeUtc, TimeSpan.Zero);
                                if (ts < sevenDayCutoff) continue;

                                long input = TryGetInt64(uEl, "input_tokens");
                                long cache = TryGetInt64(uEl, "cache_read_input_tokens") +
                                             TryGetInt64(uEl, "cache_creation_input_tokens");
                                long output = TryGetInt64(uEl, "output_tokens");
                                if (input + cache + output <= 0) continue;

                                var model = msgEl.TryGetProperty("model", out var mEl) && mEl.ValueKind == JsonValueKind.String
                                    ? mEl.GetString()
                                    : null;
                                if (string.IsNullOrWhiteSpace(model) ||
                                    string.Equals(model, "<synthetic>", StringComparison.OrdinalIgnoreCase))
                                {
                                    model = "claude";
                                }

                                var msgId = msgEl.TryGetProperty("id", out var idEl) && idEl.ValueKind == JsonValueKind.String
                                    ? idEl.GetString()
                                    : null;

                                if (!string.IsNullOrWhiteSpace(msgId))
                                {
                                    if (byMessageId.TryGetValue(msgId, out var prev))
                                    {
                                        byMessageId[msgId] = (
                                            ts > prev.Ts ? ts : prev.Ts,
                                            model,
                                            Math.Max(prev.Input, input),
                                            Math.Max(prev.Cache, cache),
                                            Math.Max(prev.Output, output));
                                    }
                                    else
                                    {
                                        byMessageId[msgId] = (ts, model, input, cache, output);
                                    }
                                }
                                else
                                {
                                    anonEntries.Add((ts, model, input, cache, output));
                                }
                            }
                            catch { }
                        }

                        foreach (var entry in byMessageId.Values.Concat(anonEntries))
                        {
                            AccumulateTokenEntry(usage, perModel, entry.Ts, fiveHourCutoff, sevenDayCutoff,
                                entry.Model, entry.Input, entry.Cache, entry.Output, ref latestTokenActivity);
                        }
                    }
                    catch { }
                }
            }
            catch (Exception ex)
            {
                AppLog.Warn($"Claude token usage scan failed: {ex.Message}");
            }
        }

        FinalizeTokenUsage(usage, perModel, latestTokenActivity);
        return usage;
    }

    private static UsageWindowInfo ReadCodexUsage(bool isConnected)
    {
        var nowUtc = DateTimeOffset.UtcNow;
        var fiveHourCutoff = nowUtc.AddHours(-5);
        var sevenDayCutoff = nowUtc.AddDays(-7);

        var usage = new UsageWindowInfo
        {
            Source = AgentSource.Codex,
            AgentName = "Codex",
            IsConnected = isConnected,
            SupportsRemainingQuota = true,
            Note = Localization.Strings.Get("UsageCodexQuotaHint")
        };

        var codexDir = AgentRegistry.ExpandHomePath("~/.codex/sessions");
        var perModel = new Dictionary<string, ModelUsageBreakdown>(StringComparer.OrdinalIgnoreCase);
        DateTimeOffset? latestTokenActivity = null;
        DateTimeOffset? latestQuotaTimestamp = null;

        if (Directory.Exists(codexDir))
        {
            try
            {
                var files = Directory.EnumerateFiles(codexDir, "rollout-*.jsonl", SearchOption.AllDirectories)
                    .Select(f => new FileInfo(f))
                    .OrderByDescending(f => f.LastWriteTimeUtc)
                    .ToList();

                foreach (var file in files)
                {
                    bool isRecentForTokens = file.LastWriteTimeUtc >= sevenDayCutoff.UtcDateTime;
                    if (!isRecentForTokens && usage.HasQuotaData) continue;

                    try
                    {
                        string? currentModel = null;
                        long prevNonCachedInput = 0;
                        long prevCache = 0;
                        long prevOutput = 0;
                        var fileTokenEvents = new List<(DateTimeOffset Ts, string? Model, long Input, long Cache, long Output)>();

                        using var fs = new FileStream(file.FullName, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                        using var reader = new StreamReader(fs);
                        string? line;
                        while ((line = reader.ReadLine()) != null)
                        {
                            bool hasModel = line.Contains("\"model\"", StringComparison.Ordinal);
                            bool hasRateLimits = line.Contains("\"rate_limits\"", StringComparison.Ordinal);
                            bool hasTokenCount = isRecentForTokens && line.Contains("\"token_count\"", StringComparison.Ordinal);

                            if (!hasModel && !hasRateLimits && !hasTokenCount) continue;

                            try
                            {
                                using var doc = JsonDocument.Parse(line);
                                var root = doc.RootElement;
                                if (root.ValueKind != JsonValueKind.Object) continue;

                                var lineTs = TryParseJsonTimestamp(root, "timestamp")
                                             ?? new DateTimeOffset(file.LastWriteTimeUtc, TimeSpan.Zero);

                                if (root.TryGetProperty("payload", out var payload) && payload.ValueKind == JsonValueKind.Object)
                                {
                                    if (payload.TryGetProperty("model", out var mEl) && mEl.ValueKind == JsonValueKind.String &&
                                        !string.IsNullOrWhiteSpace(mEl.GetString()))
                                    {
                                        currentModel = mEl.GetString()!.Trim();
                                    }

                                    if (hasTokenCount &&
                                        payload.TryGetProperty("type", out var ptEl) &&
                                        string.Equals(ptEl.GetString(), "token_count", StringComparison.OrdinalIgnoreCase) &&
                                        payload.TryGetProperty("info", out var infoEl) &&
                                        infoEl.ValueKind == JsonValueKind.Object)
                                    {
                                        if (infoEl.TryGetProperty("total_token_usage", out var totEl) &&
                                            totEl.ValueKind == JsonValueKind.Object)
                                        {
                                            long cumRawInput = TryGetInt64(totEl, "input_tokens");
                                            long cumCache = TryGetInt64(totEl, "cached_input_tokens");
                                            long cumNonCachedInput = cumRawInput >= cumCache ? cumRawInput - cumCache : cumRawInput;
                                            long cumOutput = TryGetInt64(totEl, "output_tokens");

                                            long dInput = Math.Max(0, cumNonCachedInput - prevNonCachedInput);
                                            long dCache = Math.Max(0, cumCache - prevCache);
                                            long dOutput = Math.Max(0, cumOutput - prevOutput);

                                            prevNonCachedInput = Math.Max(prevNonCachedInput, cumNonCachedInput);
                                            prevCache = Math.Max(prevCache, cumCache);
                                            prevOutput = Math.Max(prevOutput, cumOutput);

                                            if (dInput + dCache + dOutput > 0)
                                            {
                                                fileTokenEvents.Add((lineTs, currentModel, dInput, dCache, dOutput));
                                            }
                                        }
                                        else if (infoEl.TryGetProperty("last_token_usage", out var lastEl) &&
                                                 lastEl.ValueKind == JsonValueKind.Object)
                                        {
                                            long rawInput = TryGetInt64(lastEl, "input_tokens");
                                            long cache = TryGetInt64(lastEl, "cached_input_tokens");
                                            long nonCachedInput = rawInput >= cache ? rawInput - cache : rawInput;
                                            long output = TryGetInt64(lastEl, "output_tokens");
                                            if (nonCachedInput + cache + output > 0)
                                            {
                                                fileTokenEvents.Add((lineTs, currentModel, nonCachedInput, cache, output));
                                            }
                                        }
                                    }
                                }

                                if (hasRateLimits &&
                                    (latestQuotaTimestamp is null || lineTs >= latestQuotaTimestamp) &&
                                    TryFindRateLimits(root, out var rl))
                                {
                                    bool gotQuota = false;
                                    double fhPct = usage.FiveHourPercent;
                                    string? fhReset = usage.FiveHourResetText;
                                    double sdPct = usage.SevenDayPercent;
                                    string? sdReset = usage.SevenDayResetText;

                                    if (rl.TryGetProperty("primary", out var p) && p.ValueKind == JsonValueKind.Object &&
                                        p.TryGetProperty("used_percent", out var pv) && pv.TryGetDouble(out var pVal))
                                    {
                                        fhPct = pVal;
                                        fhReset = TryGetResetString(p) ?? fhReset;
                                        gotQuota = true;
                                    }
                                    if (rl.TryGetProperty("secondary", out var s) && s.ValueKind == JsonValueKind.Object &&
                                        s.TryGetProperty("used_percent", out var sv) && sv.TryGetDouble(out var sVal))
                                    {
                                        sdPct = sVal;
                                        sdReset = TryGetResetString(s) ?? sdReset;
                                        gotQuota = true;
                                    }

                                    if (gotQuota)
                                    {
                                        usage.FiveHourPercent = fhPct;
                                        usage.FiveHourResetText = fhReset;
                                        usage.SevenDayPercent = sdPct;
                                        usage.SevenDayResetText = sdReset;
                                        usage.HasQuotaData = true;
                                        usage.Note = null;
                                        latestQuotaTimestamp = lineTs;
                                        usage.LastUpdated = lineTs.ToLocalTime();
                                    }
                                }
                            }
                            catch { }
                        }

                        var fallbackModel = string.IsNullOrWhiteSpace(currentModel) ? "gpt-5-codex" : currentModel!;
                        foreach (var ev in fileTokenEvents)
                        {
                            var mdl = string.IsNullOrWhiteSpace(ev.Model) ? fallbackModel : ev.Model!;
                            AccumulateTokenEntry(usage, perModel, ev.Ts, fiveHourCutoff, sevenDayCutoff,
                                mdl, ev.Input, ev.Cache, ev.Output, ref latestTokenActivity);
                        }
                    }
                    catch { }
                }
            }
            catch (Exception ex)
            {
                AppLog.Warn($"Codex usage scan failed: {ex.Message}");
            }
        }

        FinalizeTokenUsage(usage, perModel, latestTokenActivity);
        return usage;
    }

    private static UsageWindowInfo ReadAntigravityUsage(bool isConnected)
    {
        var nowUtc = DateTimeOffset.UtcNow;
        var fiveHourCutoff = nowUtc.AddHours(-5);
        var sevenDayCutoff = nowUtc.AddDays(-7);

        var usage = new UsageWindowInfo
        {
            Source = AgentSource.Antigravity,
            AgentName = "Google Antigravity",
            IsConnected = isConnected,
            SupportsRemainingQuota = true,
            HasQuotaData = false,
            IsTokenMetricMode = true,
            OfficialQuotaHint = Localization.Strings.Get("UsageAntigravityHint"),
            Note = Localization.Strings.Get("UsageAntigravityQuotaCloudHint")
        };

        var perModel = new Dictionary<string, ModelUsageBreakdown>(StringComparer.OrdinalIgnoreCase);
        DateTimeOffset? latestActivity = null;

        foreach (var (relBrainDir, _, _) in AntigravityRoots)
        {
            var brainRoot = AgentRegistry.ExpandHomePath(relBrainDir);
            if (!Directory.Exists(brainRoot)) continue;

            try
            {
                foreach (var convDir in Directory.EnumerateDirectories(brainRoot))
                {
                    var transcriptPath = Path.Combine(convDir, ".system_generated", "logs", "transcript.jsonl");
                    if (!File.Exists(transcriptPath)) continue;

                    try
                    {
                        var info = new FileInfo(transcriptPath);
                        if (info.LastWriteTimeUtc < sevenDayCutoff.UtcDateTime) continue;

                        using var fs = new FileStream(transcriptPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                        using var reader = new StreamReader(fs);
                        string? line;
                        while ((line = reader.ReadLine()) != null)
                        {
                            // Fast pre-filter before JSON parsing: only PLANNER_RESPONSE carries model token stats.
                            if (!line.Contains("\"PLANNER_RESPONSE\"", StringComparison.Ordinal)) continue;
                            if (!line.Contains("_tokens\"", StringComparison.Ordinal)) continue;

                            try
                            {
                                using var doc = JsonDocument.Parse(line);
                                var root = doc.RootElement;
                                if (root.ValueKind != JsonValueKind.Object) continue;

                                var ts = TryParseJsonTimestamp(root, "created_at");
                                if (ts is null || ts.Value < sevenDayCutoff) continue;

                                long input = TryGetInt64(root, "input_tokens");
                                long cache = TryGetInt64(root, "cache_read_tokens");
                                long output = TryGetInt64(root, "output_tokens");
                                if (input + cache + output <= 0) continue;

                                var modelName = root.TryGetProperty("model", out var mEl) && mEl.ValueKind == JsonValueKind.String
                                    ? mEl.GetString()
                                    : null;
                                if (string.IsNullOrWhiteSpace(modelName))
                                {
                                    modelName = "gemini-antigravity";
                                }

                                AccumulateTokenEntry(usage, perModel, ts.Value, fiveHourCutoff, sevenDayCutoff,
                                    modelName!, input, cache, output, ref latestActivity);
                            }
                            catch { }
                        }
                    }
                    catch { }
                }
            }
            catch (Exception ex)
            {
                AppLog.Warn($"Antigravity usage scan failed ({brainRoot}): {ex.Message}");
            }
        }

        FinalizeTokenUsage(usage, perModel, latestActivity);
        return usage;
    }

    private static UsageWindowInfo ReadDeepSeekHarnessUsage(bool isConnected)
    {
        var nowUtc = DateTimeOffset.UtcNow;
        var fiveHourCutoff = nowUtc.AddHours(-5);
        var sevenDayCutoff = nowUtc.AddDays(-7);
        var sevenDayCutoffMs = sevenDayCutoff.ToUnixTimeMilliseconds();

        var usage = new UsageWindowInfo
        {
            Source = AgentSource.DeepSeekHarness,
            AgentName = "DeepSeek Harness",
            IsConnected = isConnected,
            SupportsRemainingQuota = false,
            HasQuotaData = false,
            IsTokenMetricMode = true,
            OfficialQuotaHint = Localization.Strings.Get("UsageDeepSeekHint")
        };

        var dir = AgentRegistry.ExpandHomePath("~/.dsh/storages/session_projcache/sessions");
        var perModel = new Dictionary<string, ModelUsageBreakdown>(StringComparer.OrdinalIgnoreCase);
        DateTimeOffset? latestActivity = null;

        if (Directory.Exists(dir))
        {
            try
            {
                foreach (var file in Directory.EnumerateFiles(dir, "*.json", SearchOption.TopDirectoryOnly))
                {
                    try
                    {
                        using var doc = JsonDocument.Parse(File.ReadAllText(file));
                        if (!doc.RootElement.TryGetProperty("record", out var record) ||
                            record.ValueKind != JsonValueKind.Object)
                            continue;

                        if (!record.TryGetProperty("identity", out var identity) ||
                            identity.ValueKind != JsonValueKind.Object)
                            continue;

                        if (identity.TryGetProperty("isSeeded", out var seeded) &&
                            seeded.ValueKind == JsonValueKind.True)
                            continue;

                        if (!record.TryGetProperty("rows", out var rows) ||
                            rows.ValueKind != JsonValueKind.Object)
                            continue;

                        long? activityMs = null;
                        if (GetRowValue(rows, "timeContext") is { ValueKind: JsonValueKind.Object } timeContext &&
                            timeContext.TryGetProperty("lastMessageTime", out var lastMessageEl) &&
                            lastMessageEl.TryGetInt64(out var lastMessageMs))
                        {
                            activityMs = lastMessageMs;
                        }
                        if (GetRowValue(rows, "sessionListMetadata") is { ValueKind: JsonValueKind.Object } listMeta &&
                            listMeta.TryGetProperty("lastPromptAt", out var promptAtEl) &&
                            promptAtEl.TryGetInt64(out var promptAtMs) &&
                            (activityMs == null || promptAtMs > activityMs))
                        {
                            activityMs = promptAtMs;
                        }

                        if (activityMs == null || activityMs.Value < sevenDayCutoffMs) continue;
                        var sessionUpdatedAt = DateTimeOffset.FromUnixTimeMilliseconds(activityMs.Value);

                        string sessionModel = "deepseek-chat";
                        if (GetRowValue(rows, "modelSelection") is { ValueKind: JsonValueKind.Object } selection &&
                            selection.TryGetProperty("lastUsed", out var lastUsed) &&
                            lastUsed.ValueKind == JsonValueKind.Object &&
                            lastUsed.TryGetProperty("model", out var modelEl) &&
                            modelEl.ValueKind == JsonValueKind.String &&
                            !string.IsNullOrWhiteSpace(modelEl.GetString()))
                        {
                            sessionModel = modelEl.GetString()!.Trim();
                        }

                        bool recordedAnyTurn = false;
                        if (GetRowValue(rows, "turnOutline") is { ValueKind: JsonValueKind.Object } outline &&
                            outline.TryGetProperty("turns", out var outlineTurns) &&
                            outlineTurns.ValueKind == JsonValueKind.Array)
                        {
                            foreach (var turn in outlineTurns.EnumerateArray())
                            {
                                if (turn.ValueKind != JsonValueKind.Object) continue;

                                var turnTs = TryParseFlexibleTimestamp(turn, "timestamp", "createdAt", "updatedAt")
                                             ?? sessionUpdatedAt;
                                if (turnTs < sevenDayCutoff) continue;

                                var turnModel = turn.TryGetProperty("model", out var tmEl) &&
                                                tmEl.ValueKind == JsonValueKind.String &&
                                                !string.IsNullOrWhiteSpace(tmEl.GetString())
                                    ? tmEl.GetString()!.Trim()
                                    : sessionModel;

                                var (input, cache, output) = ExtractOrEstimateDshTokens(turn);
                                if (input + cache + output <= 0) continue;

                                AccumulateTokenEntry(usage, perModel, turnTs, fiveHourCutoff, sevenDayCutoff,
                                    turnModel, input, cache, output, ref latestActivity);
                                recordedAnyTurn = true;
                            }
                        }

                        if (!recordedAnyTurn &&
                            GetRowValue(rows, "sessionStats") is { ValueKind: JsonValueKind.Object } stats)
                        {
                            var (input, cache, output) = ExtractExplicitTokens(stats);
                            if (input + cache + output > 0)
                            {
                                AccumulateTokenEntry(usage, perModel, sessionUpdatedAt, fiveHourCutoff, sevenDayCutoff,
                                    sessionModel, input, cache, output, ref latestActivity);
                            }
                        }
                    }
                    catch { }
                }
            }
            catch (Exception ex)
            {
                AppLog.Warn($"DeepSeek Harness usage scan failed: {ex.Message}");
            }
        }

        FinalizeTokenUsage(usage, perModel, latestActivity);
        return usage;
    }

    private static (long Input, long Cache, long Output) ExtractOrEstimateDshTokens(JsonElement turn)
    {
        var (input, cache, output) = ExtractExplicitTokens(turn);
        if (input + cache + output == 0 &&
            turn.TryGetProperty("usage", out var usageEl) &&
            usageEl.ValueKind == JsonValueKind.Object)
        {
            (input, cache, output) = ExtractExplicitTokens(usageEl);
        }

        if (input + cache + output > 0)
        {
            return (input, cache, output);
        }

        var prompt = turn.TryGetProperty("prompt", out var pEl) && pEl.ValueKind == JsonValueKind.String
            ? pEl.GetString()
            : null;
        var response = turn.TryGetProperty("response", out var rEl) && rEl.ValueKind == JsonValueKind.String
            ? rEl.GetString()
            : null;

        long estInput = EstimateTokensFromText(prompt);
        long estOutput = EstimateTokensFromText(response);
        return (estInput, 0, estOutput);
    }

    private static (long Input, long Cache, long Output) ExtractExplicitTokens(JsonElement obj)
    {
        long input = TryGetInt64(obj, "inputTokens")
                     + TryGetInt64(obj, "promptTokens")
                     + TryGetInt64(obj, "input_tokens")
                     + TryGetInt64(obj, "prompt_tokens");
        long cache = TryGetInt64(obj, "cacheTokens")
                     + TryGetInt64(obj, "cachedTokens")
                     + TryGetInt64(obj, "cache_read_tokens")
                     + TryGetInt64(obj, "cache_read_input_tokens")
                     + TryGetInt64(obj, "cached_input_tokens");
        long output = TryGetInt64(obj, "outputTokens")
                      + TryGetInt64(obj, "completionTokens")
                      + TryGetInt64(obj, "output_tokens")
                      + TryGetInt64(obj, "completion_tokens");
        return (input, cache, output);
    }

    internal static long EstimateTokensFromText(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return 0;
        long cjkCount = 0;
        long otherChars = 0;
        foreach (var ch in text)
        {
            if (char.IsWhiteSpace(ch)) continue;
            if ((ch >= '\u4E00' && ch <= '\u9FFF') ||
                (ch >= '\u3400' && ch <= '\u4DBF') ||
                (ch >= '\u3000' && ch <= '\u303F') ||
                (ch >= '\uFF00' && ch <= '\uFFEF'))
            {
                cjkCount++;
            }
            else
            {
                otherChars++;
            }
        }
        long otherTokens = otherChars > 0 ? Math.Max(1, (otherChars + 3) / 4) : 0;
        return cjkCount + otherTokens;
    }

    private static void AccumulateTokenEntry(
        UsageWindowInfo usage,
        Dictionary<string, ModelUsageBreakdown> perModel,
        DateTimeOffset ts,
        DateTimeOffset fiveHourCutoff,
        DateTimeOffset sevenDayCutoff,
        string modelName,
        long input,
        long cache,
        long output,
        ref DateTimeOffset? latestActivity)
    {
        if (ts < sevenDayCutoff) return;
        long total = input + cache + output;
        if (total <= 0) return;

        usage.SevenDayTokens.InputTokens += input;
        usage.SevenDayTokens.CacheReadTokens += cache;
        usage.SevenDayTokens.OutputTokens += output;
        usage.SevenDayTokens.RequestCount++;

        bool inFiveHour = ts >= fiveHourCutoff;
        if (inFiveHour)
        {
            usage.FiveHourTokens.InputTokens += input;
            usage.FiveHourTokens.CacheReadTokens += cache;
            usage.FiveHourTokens.OutputTokens += output;
            usage.FiveHourTokens.RequestCount++;
        }

        if (!string.IsNullOrWhiteSpace(modelName))
        {
            if (!perModel.TryGetValue(modelName, out var breakdown))
            {
                breakdown = new ModelUsageBreakdown { Model = modelName };
                perModel[modelName] = breakdown;
            }
            breakdown.SevenDayTokens += total;
            if (inFiveHour) breakdown.FiveHourTokens += total;
            breakdown.RequestCount++;
        }

        if (latestActivity is null || ts > latestActivity)
        {
            latestActivity = ts;
        }
    }

    private static void FinalizeTokenUsage(
        UsageWindowInfo usage,
        Dictionary<string, ModelUsageBreakdown> perModel,
        DateTimeOffset? latestTokenActivity)
    {
        if (perModel.Count > 0)
        {
            usage.TopModels = perModel.Values
                .OrderByDescending(m => m.SevenDayTokens)
                .ThenByDescending(m => m.RequestCount)
                .Take(6)
                .ToList();
        }

        if (latestTokenActivity.HasValue)
        {
            var localTs = latestTokenActivity.Value.ToLocalTime();
            if (!usage.LastUpdated.HasValue || localTs > usage.LastUpdated.Value)
            {
                usage.LastUpdated = localTs;
            }
        }
    }

    private static DateTimeOffset? TryParseJsonTimestamp(JsonElement obj, string propertyName)
    {
        if (!obj.TryGetProperty(propertyName, out var el)) return null;
        if (el.ValueKind == JsonValueKind.String &&
            DateTimeOffset.TryParse(el.GetString(), System.Globalization.CultureInfo.InvariantCulture,
                System.Globalization.DateTimeStyles.AssumeUniversal | System.Globalization.DateTimeStyles.AdjustToUniversal,
                out var parsed))
        {
            return parsed;
        }
        if (el.ValueKind == JsonValueKind.Number && el.TryGetInt64(out var rawNum) && rawNum > 0)
        {
            return FromUnixTimestamp(rawNum);
        }
        return null;
    }

    private static DateTimeOffset? TryParseFlexibleTimestamp(JsonElement obj, params string[] propertyNames)
    {
        foreach (var prop in propertyNames)
        {
            var ts = TryParseJsonTimestamp(obj, prop);
            if (ts.HasValue) return ts;
        }
        return null;
    }

    private static string? TryGetResetString(JsonElement windowObj)
    {
        if (!windowObj.TryGetProperty("resets_at", out var r)) return null;
        if (r.ValueKind == JsonValueKind.String) return r.GetString();
        if (r.ValueKind == JsonValueKind.Number && r.TryGetInt64(out var unix) && unix > 0)
        {
            return FromUnixTimestamp(unix).ToString("O");
        }
        return null;
    }

    private static long TryGetInt64(JsonElement obj, string propertyName)
    {
        if (obj.TryGetProperty(propertyName, out var prop) && prop.ValueKind == JsonValueKind.Number && prop.TryGetInt64(out var val))
        {
            return Math.Max(0, val);
        }
        return 0;
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
