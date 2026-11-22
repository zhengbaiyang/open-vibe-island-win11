using System.Text;
using System.Text.Json;
using OpenIsland.Core.Models;

namespace OpenIsland.Core.Hooks;

/// <summary>
/// Normalized hook event decoded from any of the 15 supported agent payload formats.
/// Supports snake_case (Claude/Codex/Gemini/Hermes/ZCode), camelCase (Grok/Cursor), and extension payloads (OpenCode/Pi/OMP).
/// </summary>
public sealed class NormalizedHookEvent
{
    public AgentSource Source { get; set; }
    public string EventName { get; set; } = "";
    public string SessionId { get; set; } = "";
    public string? Cwd { get; set; }
    public string? Model { get; set; }
    public string? PermissionMode { get; set; }
    public string? TranscriptPath { get; set; }
    public string? Prompt { get; set; }
    public string? LastAssistantMessage { get; set; }
    public string? ToolName { get; set; }
    public string? ToolInputPreview { get; set; }
    public string? ToolUseId { get; set; }
    public string? NotificationType { get; set; }
    public string? Message { get; set; }
    public string? Title { get; set; }
    public string? Reason { get; set; }
    public string? Error { get; set; }
    public bool IsInterrupt { get; set; }
    public string? AgentId { get; set; }
    public string? AgentType { get; set; }
    public string? TurnId { get; set; }
    public string? ThreadId { get; set; }
    public List<PendingQuestionItem> Questions { get; set; } = new();
    public List<QuestionOption> ApprovalOptions { get; set; } = new();
    public TerminalMetadata Terminal { get; set; } = new();
    public bool ExpectsBlockingDirective { get; set; }
}

public static class HookPayloadDecoder
{
    public static NormalizedHookEvent Decode(BridgeEnvelope envelope)
    {
        var source = AgentRegistry.Parse(envelope.Source);
        var evt = new NormalizedHookEvent
        {
            Source = source,
            Cwd = envelope.Cwd,
            ExpectsBlockingDirective = envelope.ExpectsDirective,
            Terminal = new TerminalMetadata
            {
                TerminalApp = envelope.TerminalApp,
                TerminalSessionId = envelope.TerminalSessionId,
                TerminalTitle = envelope.TerminalTitle,
                HostProcessId = envelope.ParentProcessId ?? envelope.ProcessId,
                WindowHandle = envelope.WindowHandle,
                IsDesktopApp = envelope.IsDesktopApp,
                DesktopAppName = envelope.DesktopAppName
            }
        };

        if (string.IsNullOrWhiteSpace(envelope.RawPayload))
        {
            evt.SessionId = GenerateFallbackSessionId(source, envelope, null, evt.Cwd);
            return evt;
        }

        try
        {
            using var doc = JsonDocument.Parse(envelope.RawPayload);
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                evt.SessionId = GenerateFallbackSessionId(source, envelope, null, evt.Cwd);
                return evt;
            }

            // Common fields across snake_case and camelCase formats.
            // NB: cwd/transcript must be parsed before deriving a fallback session id so
            // that recurring runs of the same conversation map to one stable id.
            evt.EventName = GetString(root, "hook_event_name", "hookEventName", "event", "type") ?? "";
            evt.Cwd = GetString(root, "cwd", "workspace", "working_directory") ?? evt.Cwd;
            evt.TranscriptPath = GetString(root, "transcript_path", "transcriptPath");
            evt.SessionId = GetString(root, "session_id", "sessionId", "conversation_id", "conversationId", "thread_id", "threadId")
                            ?? GenerateFallbackSessionId(source, envelope, evt.TranscriptPath, evt.Cwd);
            evt.Model = GetString(root, "model", "modelName");
            evt.PermissionMode = GetString(root, "permission_mode", "permissionMode");
            evt.Prompt = GetString(root, "prompt", "user_prompt", "userPrompt", "lastUserInput");
            evt.LastAssistantMessage = GetString(root, "last_assistant_message", "lastAssistantMessage", "prompt_response", "assistant_response", "response");
            evt.ToolName = GetString(root, "tool_name", "toolName", "tool");
            evt.ToolUseId = GetString(root, "tool_use_id", "toolUseId", "tool_call_id");
            evt.NotificationType = GetString(root, "notification_type", "notificationType");
            evt.Message = GetString(root, "message");
            evt.Title = GetString(root, "title");
            evt.Reason = GetString(root, "reason", "terminationReason");
            evt.Error = GetString(root, "error", "error_details");
            evt.IsInterrupt = GetBool(root, "is_interrupt", "isInterrupt");
            evt.AgentId = GetString(root, "agent_id", "agentId");
            evt.AgentType = GetString(root, "agent_type", "agentType");
            evt.TurnId = GetString(root, "turn_id", "turnId");
            evt.ThreadId = GetString(root, "thread_id", "threadId");

            // Antigravity hook payload specifics (camelCase protojson: workspacePaths, toolCall, terminationReason)
            if (source == AgentSource.Antigravity)
            {
                if (string.IsNullOrWhiteSpace(evt.Cwd)
                    && root.TryGetProperty("workspacePaths", out var wp)
                    && wp.ValueKind == JsonValueKind.Array)
                {
                    foreach (var item in wp.EnumerateArray())
                    {
                        if (item.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(item.GetString()))
                        {
                            evt.Cwd = item.GetString();
                            break;
                        }
                    }
                }

                if (root.TryGetProperty("toolCall", out var toolCall) && toolCall.ValueKind == JsonValueKind.Object)
                {
                    evt.ToolUseId ??= GetString(toolCall, "id");
                    evt.ToolName ??= GetString(toolCall, "name");
                    if (toolCall.TryGetProperty("args", out var tcArgs))
                    {
                        evt.ToolInputPreview ??= ExtractToolPreview(tcArgs);
                        ExtractQuestions(tcArgs, evt.Questions);
                        ExtractApprovalOptions(tcArgs, evt.ApprovalOptions);
                    }
                }

                if (!string.IsNullOrWhiteSpace(evt.Reason) &&
                    string.Equals(evt.Reason, "USER_CANCELED", StringComparison.OrdinalIgnoreCase))
                {
                    evt.IsInterrupt = true;
                }

                evt.Terminal.IsDesktopApp = true;
                evt.Terminal.DesktopAppName ??= "Antigravity";
                evt.Terminal.TerminalApp ??= "Antigravity";
            }

            // Terminal metadata inside payload takes precedence if present
            evt.Terminal.TerminalApp ??= GetString(root, "terminal_app", "terminalApp");
            evt.Terminal.TerminalSessionId ??= GetString(root, "terminal_session_id", "terminalSessionId");
            evt.Terminal.TerminalTitle ??= GetString(root, "terminal_title", "terminalTitle");

            // Extract tool_input preview (command / file_path / description / raw JSON)
            if (TryGetProperty(root, out var toolInput, "tool_input", "toolInput", "input", "arguments", "args"))
            {
                evt.ToolInputPreview ??= ExtractToolPreview(toolInput);
                ExtractQuestions(toolInput, evt.Questions);
                ExtractApprovalOptions(toolInput, evt.ApprovalOptions);
            }

            // Hermes Agent `extra` object
            if (source == AgentSource.Hermes && TryGetProperty(root, out var extra, "extra") && extra.ValueKind == JsonValueKind.Object)
            {
                evt.Prompt ??= GetString(extra, "user_message");
                evt.LastAssistantMessage ??= GetString(extra, "assistant_response");
                evt.Model ??= GetString(extra, "model");
                evt.Title ??= GetString(extra, "description");
                evt.ToolUseId ??= GetString(extra, "tool_call_id");
                var cmd = GetString(extra, "command");
                if (!string.IsNullOrWhiteSpace(cmd))
                {
                    evt.ToolInputPreview = cmd;
                }
                var childRole = GetString(extra, "child_role");
                if (!string.IsNullOrWhiteSpace(childRole))
                {
                    evt.AgentType = childRole;
                }
                ExtractQuestions(extra, evt.Questions);
                ExtractApprovalOptions(extra, evt.ApprovalOptions);
            }

            // OpenCode / AskUserQuestion / Codex / Antigravity question or approval options at root
            if (evt.Questions.Count == 0)
            {
                ExtractQuestions(root, evt.Questions);
            }
            if (evt.ApprovalOptions.Count == 0)
            {
                ExtractApprovalOptions(root, evt.ApprovalOptions);
            }

            // Codex conversation deep link. Codex App / ChatGPT registers the
            // codex:// protocol, so clicking a session in the island can open the exact
            // conversation instead of only activating a terminal/folder.
            if (source == AgentSource.Codex)
            {
                var tid = evt.ThreadId ?? evt.SessionId;
                if (!string.IsNullOrWhiteSpace(tid) && Guid.TryParse(tid, out _))
                {
                    evt.Terminal.DeepLinkUrl = $"codex://threads/{tid}";
                }
                if (!string.IsNullOrWhiteSpace(evt.Terminal.TerminalApp) &&
                    evt.Terminal.TerminalApp.Contains("codex", StringComparison.OrdinalIgnoreCase))
                {
                    evt.Terminal.IsDesktopApp = true;
                    evt.Terminal.DesktopAppName ??= "Codex Desktop";
                }
            }

            // Gemini duplicated completion body cleanup (best-effort)
            if (source == AgentSource.Gemini && !string.IsNullOrWhiteSpace(evt.LastAssistantMessage))
            {
                evt.LastAssistantMessage = DeduplicateGeminiResponse(evt.LastAssistantMessage);
            }
        }
        catch
        {
            evt.SessionId = GenerateFallbackSessionId(source, envelope, null, evt.Cwd);
        }

        evt.EventName = NormalizeEventName(source, evt.EventName, evt.Reason, evt.Error);
        return evt;
    }

    private static string NormalizeEventName(AgentSource source, string raw, string? reason = null, string? error = null)
    {
        if (string.IsNullOrWhiteSpace(raw)) return "Heartbeat";
        var key = raw.Trim().ToLowerInvariant().Replace("_", "").Replace("-", "");

        // Google Antigravity lifecycle hook event names (hooks.json)
        if (source == AgentSource.Antigravity)
        {
            return key switch
            {
                "sessionstart" => "SessionStart",
                "preinvocation" => "UserPromptSubmit",
                "postinvocation" => "Heartbeat",
                "pretooluse" => "PreToolUse",
                "posttooluse" => !string.IsNullOrWhiteSpace(error) ? "PostToolUseFailure" : "PostToolUse",
                "stop" => !string.IsNullOrWhiteSpace(error) || string.Equals(reason, "ERROR", StringComparison.OrdinalIgnoreCase)
                    ? "StopFailure"
                    : string.Equals(reason, "USER_CANCELED", StringComparison.OrdinalIgnoreCase)
                        ? "StopCancelled"
                        : "Stop",
                _ => raw
            };
        }

        // Cursor-specific hook event names
        if (source == AgentSource.Cursor)
        {
            return key switch
            {
                "beforesubmitprompt" => "UserPromptSubmit",
                "beforeshellexecution" or "beforemcpexecution" or "beforereadfile" => "PreToolUse",
                "afterfileedit" => "PostToolUse",
                "stop" => "Stop",
                _ => raw
            };
        }

        // Gemini-specific event names
        if (source == AgentSource.Gemini)
        {
            return key switch
            {
                "sessionstart" => "SessionStart",
                "beforeagent" or "userpromptsubmit" => "UserPromptSubmit",
                "afteragent" or "stop" => "Stop",
                "sessionend" => "SessionEnd",
                "notification" => "Notification",
                "beforetool" or "pretooluse" => "PreToolUse",
                "aftertool" or "posttooluse" => "PostToolUse",
                "permissionrequest" => "PermissionRequest",
                "questionrequest" or "question" => "QuestionRequest",
                _ => raw
            };
        }

        // Hermes Agent event names
        if (source == AgentSource.Hermes)
        {
            return key switch
            {
                "onsessionstart" => "SessionStart",
                "onsessionend" => "SessionEnd",
                "postllmcall" => "Stop",
                "subagentstop" => "SubagentStop",
                "pretoolcall" => "PreToolUse",
                "preapprovalrequest" => "PermissionRequest",
                _ => raw
            };
        }

        // Pi & Oh My Pi event names
        if (source is AgentSource.Pi or AgentSource.OhMyPi)
        {
            return key switch
            {
                "sessionstart" => "SessionStart",
                "beforeagentstart" or "userpromptsubmit" => "UserPromptSubmit",
                "toolexecutionstart" or "pretooluse" => "PreToolUse",
                "toolexecutionend" or "posttooluse" => "PostToolUse",
                "agentsettled" or "sessionstop" or "stop" => "Stop",
                "heartbeat" => "Heartbeat",
                "sessionshutdown" or "sessionend" => "SessionEnd",
                _ => raw
            };
        }

        // Canonical snake_case / camelCase / PascalCase mapping (Claude, Codex, Grok, Kimi, ZCode, OpenCode, forks)
        return key switch
        {
            "sessionstart" => "SessionStart",
            "sessionend" => "SessionEnd",
            "userpromptsubmit" => "UserPromptSubmit",
            "pretooluse" or "beforetool" => "PreToolUse",
            "posttooluse" or "aftertool" or "permission.replied" or "question.replied" => "PostToolUse",
            "posttoolusefailure" => "PostToolUseFailure",
            "permissionrequest" or "permission.asked" or "permission.updated" => "PermissionRequest",
            "permissiondenied" => "PermissionDenied",
            "question" or "questionrequest" or "question.asked" or "requestuserinput" => "QuestionRequest",
            "notification" => "Notification",
            "stop" => "Stop",
            "stopfailure" => "StopFailure",
            "stopcancelled" or "stopcanceled" => "StopCancelled",
            "subagentstart" => "SubagentStart",
            "subagentstop" => "SubagentStop",
            "precompact" => "PreCompact",
            "postcompact" => "PostCompact",
            "heartbeat" => "Heartbeat",
            _ => raw
        };
    }

    private static string ExtractToolPreview(JsonElement el)
    {
        if (el.ValueKind == JsonValueKind.String) return Clip(el.GetString(), 240);
        if (el.ValueKind == JsonValueKind.Object)
        {
            var cmd = GetString(el, "command", "cmd", "script", "CommandLine");
            if (!string.IsNullOrWhiteSpace(cmd)) return Clip(cmd, 240);
            var desc = GetString(el, "description", "toolSummary");
            var path = GetString(el, "file_path", "filePath", "path", "Path", "target_file");
            if (!string.IsNullOrWhiteSpace(path))
                return string.IsNullOrWhiteSpace(desc) ? Clip(path, 240) : Clip($"{path} — {desc}", 240);
            if (!string.IsNullOrWhiteSpace(desc)) return Clip(desc, 240);
            return Clip(el.GetRawText(), 240);
        }
        return Clip(el.GetRawText(), 240);
    }

    public static bool IsQuestionToolName(string? toolName)
    {
        if (string.IsNullOrWhiteSpace(toolName)) return false;
        var clean = toolName.Trim();
        var sep = clean.LastIndexOfAny([':', '.', '/']);
        var leaf = sep >= 0 ? clean[(sep + 1)..] : clean;
        var norm = leaf.ToLowerInvariant().Replace("_", "").Replace("-", "");
        return norm is "askquestion" or "askuserquestion" or "askuser" or "requestuserinput"
            or "clarify" or "question" or "userquestion" or "selectoption" or "promptuser";
    }

    public static bool IsPlanApprovalToolName(string? toolName)
    {
        if (string.IsNullOrWhiteSpace(toolName)) return false;
        var clean = toolName.Trim();
        var sep = clean.LastIndexOfAny([':', '.', '/']);
        var leaf = sep >= 0 ? clean[(sep + 1)..] : clean;
        var norm = leaf.ToLowerInvariant().Replace("_", "").Replace("-", "");
        return norm is "exitplanmode" or "presentplan" or "approveplan";
    }

    public static void ExtractQuestions(JsonElement container, List<PendingQuestionItem> target)
    {
        if (container.ValueKind == JsonValueKind.String)
        {
            var raw = container.GetString();
            if (string.IsNullOrWhiteSpace(raw)) return;
            try
            {
                using var inner = JsonDocument.Parse(raw);
                ExtractQuestions(inner.RootElement, target);
            }
            catch { }
            return;
        }

        if (container.ValueKind == JsonValueKind.Array)
        {
            ExtractQuestionArray(container, target);
            return;
        }

        if (container.ValueKind != JsonValueKind.Object) return;

        if (TryGetProperty(container, out var qProp, "questions", "prompts", "items", "fields"))
        {
            if (qProp.ValueKind == JsonValueKind.Array)
            {
                int before = target.Count;
                ExtractQuestionArray(qProp, target);
                if (target.Count > before) return;
            }
            else if (qProp.ValueKind == JsonValueKind.String)
            {
                var raw = qProp.GetString();
                if (!string.IsNullOrWhiteSpace(raw))
                {
                    try
                    {
                        using var parsed = JsonDocument.Parse(raw);
                        if (parsed.RootElement.ValueKind == JsonValueKind.Array)
                        {
                            int before = target.Count;
                            ExtractQuestionArray(parsed.RootElement, target);
                            if (target.Count > before) return;
                        }
                    }
                    catch { }
                }
            }
        }

        // Fallback: single flat question object ({ "question": "...", "options" / "choices": [...] })
        if (TryParseSingleQuestion(container, out var single))
        {
            target.Add(single);
        }
    }

    public static void ExtractApprovalOptions(JsonElement container, List<QuestionOption> target)
    {
        if (container.ValueKind == JsonValueKind.String)
        {
            var raw = container.GetString();
            if (string.IsNullOrWhiteSpace(raw)) return;
            try
            {
                using var inner = JsonDocument.Parse(raw);
                ExtractApprovalOptions(inner.RootElement, target);
            }
            catch { }
            return;
        }

        if (container.ValueKind != JsonValueKind.Object) return;

        if (TryGetProperty(container, out var optProp,
                "availableDecisions", "available_decisions", "permission_suggestions",
                "permissionSuggestions", "options", "choices", "actions", "buttons"))
        {
            if (optProp.ValueKind == JsonValueKind.Array)
            {
                ExtractOptionsArray(optProp, target);
            }
            else if (optProp.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(optProp.GetString()))
            {
                try
                {
                    using var parsed = JsonDocument.Parse(optProp.GetString()!);
                    if (parsed.RootElement.ValueKind == JsonValueKind.Array)
                    {
                        ExtractOptionsArray(parsed.RootElement, target);
                    }
                }
                catch { }
            }
        }
    }

    private static void ExtractQuestionArray(JsonElement qArr, List<PendingQuestionItem> target)
    {
        foreach (var item in qArr.EnumerateArray())
        {
            if (TryParseSingleQuestion(item, out var q))
            {
                target.Add(q);
            }
        }
    }

    private static bool TryParseSingleQuestion(JsonElement item, out PendingQuestionItem result)
    {
        result = null!;
        if (item.ValueKind != JsonValueKind.Object) return false;

        var options = new List<QuestionOption>();
        if (TryGetProperty(item, out var opts, "options", "choices", "selections", "actions", "buttons", "answers"))
        {
            if (opts.ValueKind == JsonValueKind.Array)
            {
                ExtractOptionsArray(opts, options);
            }
            else if (opts.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(opts.GetString()))
            {
                try
                {
                    using var parsedOpts = JsonDocument.Parse(opts.GetString()!);
                    if (parsedOpts.RootElement.ValueKind == JsonValueKind.Array)
                    {
                        ExtractOptionsArray(parsedOpts.RootElement, options);
                    }
                }
                catch { }
            }
        }

        var questionText = GetString(item, "question", "text", "prompt", "message", "title", "description", "label");
        // Avoid treating arbitrary tool inputs (like a random file read with a "title" or "description")
        // as a question unless it either explicitly has "question" OR has selectable options.
        bool hasExplicitQuestionKey = item.TryGetProperty("question", out _);
        if (string.IsNullOrWhiteSpace(questionText))
        {
            if (options.Count == 0) return false;
            questionText = Localization.Strings.Get("QuestionHint");
        }
        else if (!hasExplicitQuestionKey && options.Count == 0)
        {
            return false;
        }

        result = new PendingQuestionItem
        {
            Header = GetString(item, "header", "category", "id") ?? "",
            Question = questionText,
            MultiSelect = GetBool(item, "is_multi_select", "isMultiSelect", "multi_select", "multiSelect", "multiple"),
            Options = options
        };
        return true;
    }

    private static void ExtractOptionsArray(JsonElement opts, List<QuestionOption> target)
    {
        foreach (var opt in opts.EnumerateArray())
        {
            if (opt.ValueKind == JsonValueKind.String)
            {
                var label = opt.GetString();
                if (!string.IsNullOrWhiteSpace(label))
                {
                    target.Add(new QuestionOption { Label = FormatDecisionLabel(label) });
                }
            }
            else if (opt.ValueKind == JsonValueKind.Object)
            {
                var label = GetString(opt, "label", "title", "name", "value", "text", "behavior", "type", "decision") ?? "";
                if (!string.IsNullOrWhiteSpace(label))
                {
                    target.Add(new QuestionOption
                    {
                        Label = FormatDecisionLabel(label),
                        Description = GetString(opt, "description", "detail", "reason", "command")
                    });
                }
            }
        }
    }

    private static string FormatDecisionLabel(string raw)
    {
        var trimmed = raw.Trim();
        return trimmed.ToLowerInvariant() switch
        {
            "accept" or "allow" or "approve" => Localization.Strings.Get("OptionAllowOnce"),
            "acceptforsession" or "accept_for_session" or "allow_for_session" or "allowalways" or "always_allow" => Localization.Strings.Get("OptionAllowAlways"),
            "decline" or "deny" or "reject" => Localization.Strings.Get("OptionDeny"),
            "cancel" or "abort" => Localization.Strings.Get("Cancel"),
            _ => trimmed
        };
    }

    private static string DeduplicateGeminiResponse(string text)
    {
        var trimmed = text.Trim();
        if (trimmed.Length < 40) return trimmed;
        int half = trimmed.Length / 2;
        var first = trimmed[..half].Trim();
        var second = trimmed[half..].Trim();
        if (string.Equals(first, second, StringComparison.Ordinal)) return first;
        return trimmed;
    }

    private static string GenerateFallbackSessionId(AgentSource source, BridgeEnvelope env,
        string? transcriptPath = null, string? cwd = null)
    {
        // Must be deterministic across hook processes: string.GetHashCode() is randomized
        // per process, and parent PIDs change every run, which used to create a brand new
        // island session on every scheduled/recurring invocation.
        var anchor = !string.IsNullOrWhiteSpace(transcriptPath) ? transcriptPath
                   : !string.IsNullOrWhiteSpace(env.TerminalSessionId) ? env.TerminalSessionId
                   : !string.IsNullOrWhiteSpace(cwd) ? cwd
                   : !string.IsNullOrWhiteSpace(env.Cwd) ? env.Cwd
                   : "default";
        var hash = System.Security.Cryptography.SHA256.HashData(Encoding.UTF8.GetBytes(anchor));
        return $"{source.ToString().ToLowerInvariant()}-{Convert.ToHexString(hash)[..12].ToLowerInvariant()}";
    }

    private static bool TryGetProperty(JsonElement obj, out JsonElement value, params string[] names)
    {
        foreach (var name in names)
        {
            if (obj.TryGetProperty(name, out value)) return true;
        }
        value = default;
        return false;
    }

    private static string? GetString(JsonElement obj, params string[] names)
    {
        foreach (var name in names)
        {
            if (obj.TryGetProperty(name, out var prop) && prop.ValueKind == JsonValueKind.String)
            {
                var s = prop.GetString();
                if (!string.IsNullOrEmpty(s)) return s;
            }
        }
        return null;
    }

    private static bool GetBool(JsonElement obj, params string[] names)
    {
        foreach (var name in names)
        {
            if (obj.TryGetProperty(name, out var prop))
            {
                if (prop.ValueKind == JsonValueKind.True) return true;
                if (prop.ValueKind == JsonValueKind.False) return false;
            }
        }
        return false;
    }

    public static string Clip(string? text, int maxLen)
    {
        if (string.IsNullOrWhiteSpace(text)) return "";
        var clean = text.Replace("\r\n", " ").Replace("\n", " ").Trim();
        return clean.Length <= maxLen ? clean : clean[..(maxLen - 1)] + "…";
    }
}
