using OpenIsland.Core.Diagnostics;
using OpenIsland.Core.Hooks;
using OpenIsland.Core.Models;

namespace OpenIsland.Core.State;

/// <summary>
/// Single source of truth for all active AI coding agent sessions, pending approvals,
/// HITL questions, and turn completion cards (mirrors Swift SessionState.apply).
/// </summary>
public sealed class SessionStateReducer
{
    private readonly object _lock = new();
    private readonly Dictionary<string, AgentSession> _sessions = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<CompletionCard> _completionCards = new();

    /// <summary>
    /// Maps an incoming session id to an already-visible session id. Used when recurring
    /// automation runs (Codex scheduled tasks etc.) open a brand-new thread every cycle:
    /// they are coalesced into one island row so the list does not pile up.
    /// </summary>
    private readonly Dictionary<string, string> _sessionAliases = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Tracks observational/transcript-discovered question &amp; approval IDs that have already been
    /// dismissed or resolved so subsequent polling ticks do not re-surface the same prompt.
    /// </summary>
    private readonly HashSet<string> _dismissedQuestionIds = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _dismissedApprovalIds = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Merge recurring generic-titled sessions ("new-chat", empty title, …) of the same
    /// agent + working directory into one row. Configured from AppSettings.
    /// </summary>
    public bool CoalesceRecurringSessions { get; set; } = true;

    public event Action? StateChanged;
    public event Action<PendingApprovalRequest>? ApprovalRequested;
    public event Action<PendingQuestionRequest>? QuestionRequested;
    public event Action<CompletionCard>? TurnCompleted;

    // Diagnostics surfaced in Settings -> Connection.
    public DateTimeOffset? LastEventAt { get; private set; }
    public string? LastEventName { get; private set; }
    public AgentSource? LastEventSource { get; private set; }
    public int EventCount { get; private set; }

    public IReadOnlyList<AgentSession> GetActiveSessions()
    {
        lock (_lock)
        {
            return _sessions.Values
                .Where(s => s.Phase != SessionPhase.Ended)
                .OrderByDescending(s => s.PendingApproval != null || s.PendingQuestion != null)
                .ThenByDescending(s => s.Phase == SessionPhase.Running)
                .ThenByDescending(s => s.UpdatedAt)
                .ToList();
        }
    }

    public IReadOnlyList<CompletionCard> GetRecentCompletions()
    {
        lock (_lock)
        {
            return _completionCards.ToList();
        }
    }

    public void DismissCompletionCard(string cardId)
    {
        lock (_lock)
        {
            _completionCards.RemoveAll(c => c.Id == cardId);
        }
        StateChanged?.Invoke();
    }

    public void RestorePersistedSessions(IEnumerable<AgentSession> sessions)
    {
        lock (_lock)
        {
            foreach (var s in sessions)
            {
                if (string.IsNullOrWhiteSpace(s.SessionId) || s.Phase == SessionPhase.Ended) continue;
                // Keep sessions updated in the last 24h on restore
                if ((DateTimeOffset.UtcNow - s.UpdatedAt).TotalHours > 24) continue;
                _sessions[s.SessionId] = s;
            }
        }
        StateChanged?.Invoke();
    }

    public (PendingApprovalRequest? Approval, PendingQuestionRequest? Question) ApplyHookEvent(NormalizedHookEvent evt)
    {
        PendingApprovalRequest? newApproval = null;
        PendingQuestionRequest? newQuestion = null;
        CompletionCard? newCompletion = null;

        LastEventAt = DateTimeOffset.Now;
        LastEventName = evt.EventName;
        LastEventSource = evt.Source;
        EventCount++;
        AppLog.Hook($"event={evt.EventName} source={evt.Source} session={evt.SessionId} cwd={evt.Cwd} prompt={(evt.Prompt is null ? "" : HookPayloadDecoder.Clip(evt.Prompt, 60))}");

        lock (_lock)
        {
            if (_sessionAliases.TryGetValue(evt.SessionId, out var canonicalId))
            {
                evt.SessionId = canonicalId;
            }

            var exists = _sessions.TryGetValue(evt.SessionId, out var session);

            // A recurring/late event for an already-expired conversation revives the same
            // row instead of creating a duplicate session.
            if (exists && session!.Phase == SessionPhase.Ended)
            {
                session.Phase = SessionPhase.Idle;
                session.PendingApproval = null;
                session.PendingQuestion = null;
            }

            if (!exists || session is null)
            {
                if (CoalesceRecurringSessions)
                {
                    var coalesced = FindCoalesceCandidate(evt.Source, evt.Cwd, evt.Title, evt.SessionId);
                    if (coalesced != null)
                    {
                        _sessionAliases[evt.SessionId] = coalesced.SessionId;
                        if (!string.IsNullOrWhiteSpace(evt.Title)) coalesced.Title = evt.Title;
                        if (!string.IsNullOrWhiteSpace(evt.Cwd) && string.IsNullOrWhiteSpace(coalesced.Cwd))
                            coalesced.Cwd = evt.Cwd;
                        if (!string.IsNullOrWhiteSpace(evt.Model) && string.IsNullOrWhiteSpace(coalesced.Model))
                            coalesced.Model = evt.Model;
                        // Keep the deep link pointing at the newest conversation of this task.
                        if (!string.IsNullOrWhiteSpace(evt.Terminal.DeepLinkUrl))
                            coalesced.Terminal.DeepLinkUrl = evt.Terminal.DeepLinkUrl;
                        coalesced.UpdatedAt = DateTimeOffset.UtcNow;
                        evt.SessionId = coalesced.SessionId;
                        session = coalesced;
                        exists = true;
                    }
                }
            }

            if (!exists || session is null)
            {
                session = new AgentSession
                {
                    SessionId = evt.SessionId,
                    Source = evt.Source,
                    Cwd = evt.Cwd,
                    Model = evt.Model,
                    PermissionMode = evt.PermissionMode,
                    TranscriptPath = evt.TranscriptPath,
                    IsHookManaged = true,
                    IsProcessAlive = true,
                    CreatedAt = DateTimeOffset.UtcNow,
                    UpdatedAt = DateTimeOffset.UtcNow,
                    LastHeartbeatAt = DateTimeOffset.UtcNow,
                    Terminal = evt.Terminal
                };
                _sessions[evt.SessionId] = session;
            }
            else
            {
                // Enrich existing session metadata without clobbering valid values
                if (!string.IsNullOrWhiteSpace(evt.Cwd)) session.Cwd = evt.Cwd;
                if (!string.IsNullOrWhiteSpace(evt.Model)) session.Model = evt.Model;
                if (!string.IsNullOrWhiteSpace(evt.PermissionMode)) session.PermissionMode = evt.PermissionMode;
                if (!string.IsNullOrWhiteSpace(evt.TranscriptPath)) session.TranscriptPath = evt.TranscriptPath;
                if (!string.IsNullOrWhiteSpace(evt.Title)) session.Title = evt.Title;
                MergeTerminal(session.Terminal, evt.Terminal);
                session.IsHookManaged = true;
                session.IsProcessAlive = true;
                session.LastHeartbeatAt = DateTimeOffset.UtcNow;
            }

            // Heartbeat updates liveness only without mutating phase/tool/summary
            if (evt.EventName == "Heartbeat")
            {
                if (session.Phase == SessionPhase.Ended) session.Phase = SessionPhase.Idle;
                return (null, null);
            }

            session.UpdatedAt = DateTimeOffset.UtcNow;

            switch (evt.EventName)
            {
                case "SessionStart":
                    session.Phase = SessionPhase.Idle;
                    session.StatusSummary = "Session started";
                    if (!string.IsNullOrWhiteSpace(evt.Prompt))
                    {
                        session.LastPrompt = HookPayloadDecoder.Clip(evt.Prompt, 160);
                        session.Phase = SessionPhase.Running;
                    }
                    break;

                case "SessionEnd":
                    session.Phase = SessionPhase.Ended;
                    session.ActiveToolName = null;
                    session.ActiveToolPreview = null;
                    RememberClearedApproval(session.PendingApproval);
                    session.PendingApproval = null;
                    RememberClearedQuestion(session.PendingQuestion);
                    session.PendingQuestion = null;
                    break;

                case "UserPromptSubmit":
                    session.Phase = SessionPhase.Running;
                    RememberClearedApproval(session.PendingApproval);
                    session.PendingApproval = null;
                    RememberClearedQuestion(session.PendingQuestion);
                    session.PendingQuestion = null;
                    if (!string.IsNullOrWhiteSpace(evt.Prompt))
                    {
                        session.LastPrompt = HookPayloadDecoder.Clip(evt.Prompt, 160);
                        session.StatusSummary = session.LastPrompt;
                    }
                    break;

                case "PreToolUse":
                    session.ActiveToolName = evt.ToolName ?? "tool";
                    session.ActiveToolPreview = evt.ToolInputPreview;

                    // Universal HITL question tool detection across all 17 platforms
                    // (Claude AskUserQuestion, Antigravity ask_question, Codex request_user_input, Hermes clarify, OpenCode question, Gemini ask_user, etc.)
                    if (HookPayloadDecoder.IsQuestionToolName(evt.ToolName) || evt.Questions.Count > 0)
                    {
                        session.Phase = SessionPhase.WaitingForAnswer;
                        newQuestion = new PendingQuestionRequest
                        {
                            SessionId = session.SessionId,
                            Source = session.Source,
                            Questions = evt.Questions.Count > 0
                                ? evt.Questions
                                : new List<PendingQuestionItem>
                                {
                                    new() { Header = evt.ToolName ?? "Question", Question = evt.ToolInputPreview ?? Localization.Strings.Get("QuestionHint") }
                                },
                            IsBlocking = evt.ExpectsBlockingDirective
                        };
                        session.PendingQuestion = newQuestion;
                        session.StatusSummary = newQuestion.Questions.FirstOrDefault()?.Question ?? "Waiting for answer";
                    }
                    else if (HookPayloadDecoder.IsPlanApprovalToolName(evt.ToolName))
                    {
                        session.Phase = SessionPhase.WaitingForApproval;
                        var planOpts = evt.ApprovalOptions.Count > 0
                            ? evt.ApprovalOptions
                            : new List<QuestionOption>
                            {
                                new() { Label = Localization.Strings.Get("OptionProceedPlan") },
                                new() { Label = Localization.Strings.Get("OptionRequestChanges") }
                            };
                        newApproval = new PendingApprovalRequest
                        {
                            SessionId = session.SessionId,
                            Source = session.Source,
                            HookEventName = "PreToolUse",
                            ToolName = evt.ToolName ?? "Plan",
                            Summary = !string.IsNullOrWhiteSpace(evt.Title)
                                ? evt.Title
                                : Localization.Strings.Get("PlanApprovalSummary"),
                            CommandOrDetails = evt.ToolInputPreview,
                            Options = planOpts,
                            IsBlocking = evt.ExpectsBlockingDirective
                        };
                        session.PendingApproval = newApproval;
                        session.StatusSummary = newApproval.Summary;
                    }
                    else
                    {
                        session.Phase = SessionPhase.Running;
                        session.StatusSummary = string.IsNullOrWhiteSpace(evt.ToolInputPreview)
                            ? $"Running {session.ActiveToolName}"
                            : $"{session.ActiveToolName}: {evt.ToolInputPreview}";
                    }
                    break;

                case "PostToolUse":
                    session.ActiveToolName = null;
                    session.ActiveToolPreview = null;
                    if (session.Phase == SessionPhase.WaitingForApproval || session.Phase == SessionPhase.WaitingForAnswer)
                    {
                        RememberClearedApproval(session.PendingApproval);
                        session.PendingApproval = null;
                        RememberClearedQuestion(session.PendingQuestion);
                        session.PendingQuestion = null;
                        session.Phase = SessionPhase.Running;
                    }
                    break;

                case "PostToolUseFailure":
                    session.ActiveToolName = null;
                    session.ActiveToolPreview = null;
                    session.StatusSummary = evt.Error ?? "Tool execution failed";
                    if (evt.Source == AgentSource.Grok)
                    {
                        session.Phase = SessionPhase.Completed;
                    }
                    break;

                case "PermissionRequest":
                    session.Phase = SessionPhase.WaitingForApproval;
                    newApproval = new PendingApprovalRequest
                    {
                        SessionId = session.SessionId,
                        Source = session.Source,
                        HookEventName = "PermissionRequest",
                        ToolName = evt.ToolName ?? "Action",
                        Summary = !string.IsNullOrWhiteSpace(evt.Title)
                            ? evt.Title
                            : $"Approve {evt.ToolName ?? "command"}?",
                        CommandOrDetails = evt.ToolInputPreview ?? evt.Message,
                        Options = evt.ApprovalOptions,
                        IsBlocking = evt.ExpectsBlockingDirective
                    };
                    session.PendingApproval = newApproval;
                    session.StatusSummary = newApproval.Summary;
                    break;

                case "QuestionRequest":
                    session.Phase = SessionPhase.WaitingForAnswer;
                    newQuestion = new PendingQuestionRequest
                    {
                        SessionId = session.SessionId,
                        Source = session.Source,
                        Questions = evt.Questions.Count > 0
                            ? evt.Questions
                            : new List<PendingQuestionItem>
                            {
                                new() { Header = evt.ToolName ?? "Question", Question = evt.Message ?? evt.Title ?? Localization.Strings.Get("QuestionHint") }
                            },
                        IsBlocking = evt.ExpectsBlockingDirective
                    };
                    session.PendingQuestion = newQuestion;
                    session.StatusSummary = newQuestion.Questions.FirstOrDefault()?.Question ?? "Waiting for answer";
                    break;

                case "PermissionDenied":
                    RememberClearedApproval(session.PendingApproval);
                    session.PendingApproval = null;
                    session.StatusSummary = "Permission denied";
                    break;

                case "SubagentStart":
                    session.ActiveSubagentCount++;
                    session.StatusSummary = string.IsNullOrWhiteSpace(evt.AgentType)
                        ? $"Subagent started ({session.ActiveSubagentCount})"
                        : $"Subagent: {evt.AgentType}";
                    break;

                case "SubagentStop":
                    session.ActiveSubagentCount = Math.Max(0, session.ActiveSubagentCount - 1);
                    if (evt.Source == AgentSource.Hermes)
                    {
                        session.Phase = SessionPhase.Completed;
                        session.StatusSummary = string.IsNullOrWhiteSpace(evt.AgentType)
                            ? "Subagent completed"
                            : $"Subagent ({evt.AgentType}) completed";
                    }
                    break;

                case "Notification":
                    if (evt.Source == AgentSource.Grok && string.Equals(evt.NotificationType, "idle_prompt", StringComparison.OrdinalIgnoreCase))
                    {
                        if (session.Phase == SessionPhase.Running)
                        {
                            session.Phase = SessionPhase.Completed;
                        }
                    }
                    else if (!string.IsNullOrWhiteSpace(evt.NotificationType) &&
                             (evt.NotificationType.Contains("permission", StringComparison.OrdinalIgnoreCase) ||
                              evt.NotificationType.Contains("elicitation", StringComparison.OrdinalIgnoreCase)))
                    {
                        if (evt.Questions.Count > 0 && session.PendingQuestion == null)
                        {
                            session.Phase = SessionPhase.WaitingForAnswer;
                            newQuestion = new PendingQuestionRequest
                            {
                                SessionId = session.SessionId,
                                Source = session.Source,
                                Questions = evt.Questions,
                                IsBlocking = false
                            };
                            session.PendingQuestion = newQuestion;
                        }
                        else if (session.PendingApproval == null)
                        {
                            session.Phase = SessionPhase.WaitingForApproval;
                            newApproval = new PendingApprovalRequest
                            {
                                SessionId = session.SessionId,
                                Source = session.Source,
                                HookEventName = "Notification",
                                ToolName = evt.ToolName ?? evt.NotificationType,
                                Summary = evt.Title ?? evt.Message ?? Localization.Strings.Get("ApproveHint"),
                                CommandOrDetails = evt.ToolInputPreview ?? evt.Message,
                                Options = evt.ApprovalOptions.Count > 0
                                    ? evt.ApprovalOptions
                                    : new List<QuestionOption>
                                    {
                                        new() { Label = Localization.Strings.Get("OptionAllowOnce") },
                                        new() { Label = Localization.Strings.Get("OptionDeny") }
                                    },
                                IsBlocking = false
                            };
                            session.PendingApproval = newApproval;
                        }
                    }
                    if (!string.IsNullOrWhiteSpace(evt.Message))
                    {
                        session.StatusSummary = HookPayloadDecoder.Clip(evt.Message, 140);
                    }
                    break;

                case "Stop":
                case "StopFailure":
                case "StopCancelled":
                    // For Grok Stop, non-end_turn reasons are observe-only
                    if (evt.Source == AgentSource.Grok && evt.EventName == "Stop"
                        && !string.IsNullOrWhiteSpace(evt.Reason)
                        && !string.Equals(evt.Reason, "end_turn", StringComparison.OrdinalIgnoreCase))
                    {
                        break;
                    }

                    bool isErr = evt.EventName == "StopFailure";
                    bool isInt = evt.EventName == "StopCancelled" || evt.IsInterrupt;
                    session.Phase = isErr ? SessionPhase.Error : SessionPhase.Completed;
                    session.ActiveToolName = null;
                    session.ActiveToolPreview = null;
                    RememberClearedApproval(session.PendingApproval);
                    session.PendingApproval = null;
                    RememberClearedQuestion(session.PendingQuestion);
                    session.PendingQuestion = null;

                    var msg = evt.LastAssistantMessage ?? evt.Error ?? evt.Reason ?? "Turn completed";
                    session.LastAssistantMessage = HookPayloadDecoder.Clip(msg, 300);
                    session.StatusSummary = HookPayloadDecoder.Clip(msg, 120);

                    newCompletion = new CompletionCard
                    {
                        SessionId = session.SessionId,
                        Source = session.Source,
                        Title = session.DisplayTitle,
                        UserPrompt = session.LastPrompt,
                        AssistantMessage = session.LastAssistantMessage,
                        IsInterrupt = isInt,
                        IsError = isErr
                    };
                    _completionCards.Insert(0, newCompletion);
                    if (_completionCards.Count > 5) _completionCards.RemoveAt(_completionCards.Count - 1);
                    break;

                case "PreCompact":
                    session.StatusSummary = "Compacting context…";
                    break;
            }
        }

        StateChanged?.Invoke();
        if (newApproval != null) ApprovalRequested?.Invoke(newApproval);
        if (newQuestion != null) QuestionRequested?.Invoke(newQuestion);
        if (newCompletion != null) TurnCompleted?.Invoke(newCompletion);
        return (newApproval, newQuestion);
    }

    public void ClearPendingApproval(string sessionId, string requestId, bool allowed)
    {
        lock (_lock)
        {
            if (!string.IsNullOrWhiteSpace(requestId))
            {
                _dismissedApprovalIds.Add(requestId);
                if (_dismissedApprovalIds.Count > 500) _dismissedApprovalIds.Clear();
            }

            if (_sessions.TryGetValue(sessionId, out var session) && session.PendingApproval?.RequestId == requestId)
            {
                var wasBlocking = session.PendingApproval.IsBlocking;
                session.PendingApproval = null;
                session.Phase = wasBlocking ? (allowed ? SessionPhase.Running : SessionPhase.Idle) : SessionPhase.Idle;
                session.StatusSummary = wasBlocking ? (allowed ? "Approved" : "Denied") : session.StatusSummary;
                session.UpdatedAt = DateTimeOffset.UtcNow;
            }
        }
        StateChanged?.Invoke();
    }

    public void ClearPendingQuestion(string sessionId, string requestId)
    {
        lock (_lock)
        {
            if (!string.IsNullOrWhiteSpace(requestId))
            {
                _dismissedQuestionIds.Add(requestId);
                if (_dismissedQuestionIds.Count > 500) _dismissedQuestionIds.Clear();
            }

            if (_sessions.TryGetValue(sessionId, out var session) && session.PendingQuestion?.RequestId == requestId)
            {
                var wasBlocking = session.PendingQuestion.IsBlocking;
                session.PendingQuestion = null;
                session.Phase = wasBlocking ? SessionPhase.Running : SessionPhase.Idle;
                session.UpdatedAt = DateTimeOffset.UtcNow;
            }
        }
        StateChanged?.Invoke();
    }

    private void RememberClearedQuestion(PendingQuestionRequest? pending)
    {
        if (pending is null || string.IsNullOrWhiteSpace(pending.RequestId)) return;
        _dismissedQuestionIds.Add(pending.RequestId);
        if (_dismissedQuestionIds.Count > 500) _dismissedQuestionIds.Clear();
    }

    private void RememberClearedApproval(PendingApprovalRequest? pending)
    {
        if (pending is null || string.IsNullOrWhiteSpace(pending.RequestId)) return;
        _dismissedApprovalIds.Add(pending.RequestId);
        if (_dismissedApprovalIds.Count > 500) _dismissedApprovalIds.Clear();
    }

    private static bool IsTranscriptDiscoveredQuestionId(string requestId) =>
        requestId.StartsWith("agq-", StringComparison.OrdinalIgnoreCase) ||
        requestId.StartsWith("clq-", StringComparison.OrdinalIgnoreCase) ||
        requestId.StartsWith("cxq-", StringComparison.OrdinalIgnoreCase) ||
        requestId.StartsWith("dsq-", StringComparison.OrdinalIgnoreCase);

    private static bool IsTranscriptDiscoveredApprovalId(string requestId) =>
        requestId.StartsWith("aga-", StringComparison.OrdinalIgnoreCase) ||
        requestId.StartsWith("agp-", StringComparison.OrdinalIgnoreCase) ||
        requestId.StartsWith("agn-", StringComparison.OrdinalIgnoreCase) ||
        requestId.StartsWith("agw-", StringComparison.OrdinalIgnoreCase) ||
        requestId.StartsWith("clp-", StringComparison.OrdinalIgnoreCase) ||
        requestId.StartsWith("dsa-", StringComparison.OrdinalIgnoreCase);

    public void UpsertDiscoveredSession(AgentSession discovered)
    {
        PendingQuestionRequest? newlyDiscoveredQuestion = null;
        PendingApprovalRequest? newlyDiscoveredApproval = null;

        lock (_lock)
        {
            if (_sessionAliases.TryGetValue(discovered.SessionId, out var canonicalId))
            {
                discovered.SessionId = canonicalId;
            }

            // Drop transcript-discovered questions/approvals that were already resolved or dismissed.
            if (discovered.PendingQuestion != null &&
                !discovered.PendingQuestion.IsBlocking &&
                _dismissedQuestionIds.Contains(discovered.PendingQuestion.RequestId))
            {
                discovered.PendingQuestion = null;
                if (discovered.Phase == SessionPhase.WaitingForAnswer)
                {
                    discovered.Phase = discovered.PendingApproval != null ? SessionPhase.WaitingForApproval : SessionPhase.Idle;
                }
            }
            if (discovered.PendingApproval != null &&
                !discovered.PendingApproval.IsBlocking &&
                _dismissedApprovalIds.Contains(discovered.PendingApproval.RequestId))
            {
                discovered.PendingApproval = null;
                if (discovered.Phase == SessionPhase.WaitingForApproval)
                {
                    discovered.Phase = discovered.PendingQuestion != null ? SessionPhase.WaitingForAnswer : SessionPhase.Idle;
                }
            }

            if (_sessions.TryGetValue(discovered.SessionId, out var existing))
            {
                // Preserve pending approval/question controls and hook-managed liveness
                if (string.IsNullOrWhiteSpace(existing.Title) && !string.IsNullOrWhiteSpace(discovered.Title))
                    existing.Title = discovered.Title;
                if (string.IsNullOrWhiteSpace(existing.Cwd) && !string.IsNullOrWhiteSpace(discovered.Cwd))
                    existing.Cwd = discovered.Cwd;
                if (string.IsNullOrWhiteSpace(existing.Model) && !string.IsNullOrWhiteSpace(discovered.Model))
                    existing.Model = discovered.Model;
                if (string.IsNullOrWhiteSpace(existing.Terminal.DeepLinkUrl) && !string.IsNullOrWhiteSpace(discovered.Terminal.DeepLinkUrl))
                    existing.Terminal.DeepLinkUrl = discovered.Terminal.DeepLinkUrl;

                // Sync observational transcript-discovered questions across all platforms
                // even when the session also receives observational lifecycle hooks (IsHookManaged == true).
                if (discovered.PendingQuestion != null)
                {
                    if (existing.PendingQuestion == null ||
                        (!existing.PendingQuestion.IsBlocking &&
                         !string.Equals(existing.PendingQuestion.RequestId, discovered.PendingQuestion.RequestId, StringComparison.OrdinalIgnoreCase)))
                    {
                        existing.PendingQuestion = discovered.PendingQuestion;
                        existing.Phase = SessionPhase.WaitingForAnswer;
                        existing.ActiveToolName = discovered.ActiveToolName ?? "ask_question";
                        existing.StatusSummary = discovered.StatusSummary ?? existing.StatusSummary;
                        existing.UpdatedAt = discovered.UpdatedAt > existing.UpdatedAt ? discovered.UpdatedAt : DateTimeOffset.UtcNow;
                        newlyDiscoveredQuestion = discovered.PendingQuestion;
                    }
                }
                else if (existing.PendingQuestion != null &&
                         !existing.PendingQuestion.IsBlocking &&
                         IsTranscriptDiscoveredQuestionId(existing.PendingQuestion.RequestId))
                {
                    RememberClearedQuestion(existing.PendingQuestion);
                    existing.PendingQuestion = null;
                    existing.ActiveToolName = discovered.ActiveToolName;
                    existing.Phase = existing.PendingApproval != null ? SessionPhase.WaitingForApproval : discovered.Phase;
                    existing.StatusSummary = discovered.StatusSummary ?? existing.StatusSummary;
                    if (discovered.UpdatedAt > existing.UpdatedAt) existing.UpdatedAt = discovered.UpdatedAt;
                }

                // Sync observational transcript-discovered approvals (Antigravity BypassSandbox / Plan Proceed / notify_user,
                // Claude ExitPlanMode, DeepSeek Harness approvalState) even when IsHookManaged == true.
                if (discovered.PendingApproval != null)
                {
                    if (existing.PendingApproval == null ||
                        (!existing.PendingApproval.IsBlocking &&
                         !string.Equals(existing.PendingApproval.RequestId, discovered.PendingApproval.RequestId, StringComparison.OrdinalIgnoreCase)))
                    {
                        existing.PendingApproval = discovered.PendingApproval;
                        if (existing.PendingQuestion == null)
                        {
                            existing.Phase = SessionPhase.WaitingForApproval;
                        }
                        existing.ActiveToolName = discovered.ActiveToolName ?? discovered.PendingApproval.ToolName;
                        existing.StatusSummary = discovered.StatusSummary ?? discovered.PendingApproval.Summary;
                        existing.UpdatedAt = discovered.UpdatedAt > existing.UpdatedAt ? discovered.UpdatedAt : DateTimeOffset.UtcNow;
                        newlyDiscoveredApproval = discovered.PendingApproval;
                    }
                }
                else if (existing.PendingApproval != null &&
                         !existing.PendingApproval.IsBlocking &&
                         IsTranscriptDiscoveredApprovalId(existing.PendingApproval.RequestId))
                {
                    RememberClearedApproval(existing.PendingApproval);
                    existing.PendingApproval = null;
                    existing.ActiveToolName = discovered.ActiveToolName;
                    existing.Phase = existing.PendingQuestion != null ? SessionPhase.WaitingForAnswer : discovered.Phase;
                    existing.StatusSummary = discovered.StatusSummary ?? existing.StatusSummary;
                    if (discovered.UpdatedAt > existing.UpdatedAt) existing.UpdatedAt = discovered.UpdatedAt;
                }

                if (!existing.IsHookManaged && discovered.UpdatedAt > existing.UpdatedAt)
                {
                    existing.UpdatedAt = discovered.UpdatedAt;
                    existing.LastPrompt = discovered.LastPrompt ?? existing.LastPrompt;
                    existing.LastAssistantMessage = discovered.LastAssistantMessage ?? existing.LastAssistantMessage;
                    existing.StatusSummary = discovered.StatusSummary ?? existing.StatusSummary;
                    existing.Model = discovered.Model ?? existing.Model;
                    existing.ActiveToolName = discovered.ActiveToolName;
                    existing.ActiveToolPreview = discovered.ActiveToolPreview;
                    if (existing.PendingQuestion == null && existing.PendingApproval == null)
                    {
                        existing.Phase = discovered.Phase;
                    }
                }
                else if (!existing.IsHookManaged && existing.Phase == SessionPhase.Running && discovered.Phase != SessionPhase.Running)
                {
                    if (existing.PendingQuestion == null && existing.PendingApproval == null)
                    {
                        existing.Phase = discovered.Phase;
                        existing.ActiveToolName = discovered.ActiveToolName;
                        existing.ActiveToolPreview = discovered.ActiveToolPreview;
                    }
                }

                // A previously expired conversation becomes visible again when it gets new
                // activity (e.g. an automation thread that was idle for a while).
                if (existing.Phase == SessionPhase.Ended &&
                    (discovered.UpdatedAt > existing.UpdatedAt || discovered.PendingQuestion != null || discovered.PendingApproval != null))
                {
                    existing.Phase = existing.PendingQuestion != null
                        ? SessionPhase.WaitingForAnswer
                        : existing.PendingApproval != null
                            ? SessionPhase.WaitingForApproval
                            : discovered.Phase;
                    existing.UpdatedAt = discovered.UpdatedAt > existing.UpdatedAt ? discovered.UpdatedAt : DateTimeOffset.UtcNow;
                    existing.LastPrompt = discovered.LastPrompt ?? existing.LastPrompt;
                    existing.StatusSummary = discovered.StatusSummary ?? existing.StatusSummary;
                }
            }
            else
            {
                // Recurring automation sessions otherwise create one row per scheduled run.
                if (CoalesceRecurringSessions)
                {
                    var coalesced = FindCoalesceCandidate(discovered.Source, discovered.Cwd, discovered.Title, discovered.SessionId);
                    if (coalesced != null)
                    {
                        _sessionAliases[discovered.SessionId] = coalesced.SessionId;
                        if (discovered.UpdatedAt > coalesced.UpdatedAt)
                        {
                            coalesced.UpdatedAt = discovered.UpdatedAt;
                            coalesced.StatusSummary = discovered.StatusSummary ?? coalesced.StatusSummary;
                            coalesced.LastPrompt = discovered.LastPrompt ?? coalesced.LastPrompt;
                            coalesced.LastAssistantMessage = discovered.LastAssistantMessage ?? coalesced.LastAssistantMessage;
                            coalesced.Phase = discovered.Phase;
                            if (!string.IsNullOrWhiteSpace(discovered.Title) && string.IsNullOrWhiteSpace(coalesced.Title))
                                coalesced.Title = discovered.Title;
                            if (!string.IsNullOrWhiteSpace(discovered.Cwd) && string.IsNullOrWhiteSpace(coalesced.Cwd))
                                coalesced.Cwd = discovered.Cwd;
                            if (!string.IsNullOrWhiteSpace(discovered.Model) && string.IsNullOrWhiteSpace(coalesced.Model))
                                coalesced.Model = discovered.Model;
                        }
                        // Prefer the newest deep link so clicking opens the latest run.
                        if (!string.IsNullOrWhiteSpace(discovered.Terminal.DeepLinkUrl))
                            coalesced.Terminal.DeepLinkUrl = discovered.Terminal.DeepLinkUrl;
                        StateChanged?.Invoke();
                        return;
                    }
                }

                _sessions[discovered.SessionId] = discovered;
                if (discovered.PendingQuestion != null)
                {
                    newlyDiscoveredQuestion = discovered.PendingQuestion;
                }
                if (discovered.PendingApproval != null)
                {
                    newlyDiscoveredApproval = discovered.PendingApproval;
                }
            }
        }

        StateChanged?.Invoke();
        if (newlyDiscoveredQuestion != null)
        {
            QuestionRequested?.Invoke(newlyDiscoveredQuestion);
        }
        if (newlyDiscoveredApproval != null)
        {
            ApprovalRequested?.Invoke(newlyDiscoveredApproval);
        }
    }

    public void ReconcileProcessLiveness(HashSet<AgentSource> aliveAgentProcesses, HashSet<int> alivePids)
    {
        bool changed = false;
        var now = DateTimeOffset.UtcNow;

        lock (_lock)
        {
            var ended = new List<AgentSession>();
            foreach (var session in _sessions.Values)
            {
                if (session.Phase == SessionPhase.Ended) continue;

                // Pi & Oh My Pi liveness is strictly keyed by 45-second heartbeat timeout
                if (session.Source is AgentSource.Pi or AgentSource.OhMyPi)
                {
                    if ((now - session.LastHeartbeatAt).TotalSeconds > 45)
                    {
                        session.Phase = SessionPhase.Ended;
                        changed = true;
                    }
                    continue;
                }

                bool pidAlive = session.Terminal.HostProcessId is int pid && alivePids.Contains(pid);
                bool agentAlive = aliveAgentProcesses.Contains(session.Source);
                bool pending = session.PendingApproval != null || session.PendingQuestion != null;
                var idleSeconds = (now - session.UpdatedAt).TotalSeconds;

                bool alive = pidAlive || agentAlive;
                if (session.IsProcessAlive != alive)
                {
                    session.IsProcessAlive = alive;
                    changed = true;
                }

                // Pending approvals/questions must stay visible until resolved or timed out.
                if (pending) continue;

                // Age out stale sessions so recurring scheduled runs do not accumulate:
                //  - discovered (non-hook) sessions: gone after 5 minutes of silence, or
                //    90 seconds after their host process disappeared;
                //  - hook sessions: a grace period after the process is gone, and a hard TTL
                //    for finished turns / stuck "running" states.
                bool expired;
                if (!session.IsHookManaged)
                {
                    expired = (!alive && idleSeconds > 90) || idleSeconds > 180;
                }
                else
                {
                    expired = (!alive && idleSeconds > 120)
                              || (session.Phase is SessionPhase.Completed or SessionPhase.Error && idleSeconds > 180)
                              || (session.Phase == SessionPhase.Idle && idleSeconds > 1800 && !alive)
                              || (session.Phase == SessionPhase.Running && idleSeconds > 1800);
                }

                if (expired)
                {
                    session.Phase = SessionPhase.Ended;
                    ended.Add(session);
                    changed = true;
                }
            }

            foreach (var session in ended)
            {
                // Keep the row around for a short while (completion cards etc. reference it),
                // then drop it from memory entirely and forget aliases pointing at it.
                if ((now - session.UpdatedAt).TotalHours > 24)
                {
                    _sessions.Remove(session.SessionId);
                    foreach (var alias in _sessionAliases.Where(kv =>
                                 string.Equals(kv.Value, session.SessionId, StringComparison.OrdinalIgnoreCase)).ToList())
                    {
                        _sessionAliases.Remove(alias.Key);
                    }
                }
            }

            if (_sessionAliases.Count > 500) _sessionAliases.Clear();
        }

        if (changed) StateChanged?.Invoke();
    }

    // ------------------------------------------------------------------
    // Session coalescing helpers (recurring scheduled-task sessions)
    // ------------------------------------------------------------------

    private static readonly string[] GenericSessionTitles =
    [
        "", "new-chat", "new chat", "newchat", "新对话", "新会话", "untitled", "untitled session",
        "codex session", "claude code session", "session", "codex", "dsh session", "deepseek harness session"
    ];

    private static bool IsGenericSessionTitle(string? title)
    {
        var normalized = (title ?? "").Trim().ToLowerInvariant();
        return GenericSessionTitles.Contains(normalized);
    }

    private AgentSession? FindCoalesceCandidate(AgentSource source, string? cwd, string? title, string? excludeSessionId)
    {
        // Only Codex has the recurring "scheduled automation opens a new thread every run"
        // problem this heuristic targets; coalescing other agents' sessions by cwd would be
        // too aggressive.
        if (source != AgentSource.Codex) return null;

        var now = DateTimeOffset.UtcNow;
        var newCwd = NormalizeCwd(cwd);
        var newTitle = (title ?? "").Trim();
        var newTitleIsGeneric = IsGenericSessionTitle(newTitle);

        return _sessions.Values
            .Where(s => s.Source == source)
            .Where(s => !string.Equals(s.SessionId, excludeSessionId, StringComparison.OrdinalIgnoreCase))
            .Where(s => s.PendingApproval == null && s.PendingQuestion == null)
            // Automation runs may keep old rows in "Running" state indefinitely when their
            // Stop hook never fired; still merge them so the island only shows one row.
            .Where(s => (now - s.UpdatedAt).TotalMinutes <= 60)
            .Where(s =>
            {
                var candidateGeneric = IsGenericSessionTitle(s.Title);
                if (candidateGeneric && newTitleIsGeneric) return true;
                return !string.IsNullOrWhiteSpace(newTitle)
                       && string.Equals(s.Title.Trim(), newTitle, StringComparison.OrdinalIgnoreCase);
            })
            .Where(s => string.Equals(NormalizeCwd(s.Cwd), newCwd, StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(s => s.UpdatedAt)
            .FirstOrDefault();
    }

    private static string NormalizeCwd(string? cwd)
    {
        if (string.IsNullOrWhiteSpace(cwd)) return "";
        try
        {
            return Path.GetFullPath(cwd.Trim()).TrimEnd('\\', '/').ToLowerInvariant();
        }
        catch
        {
            return cwd.Trim().TrimEnd('\\', '/').ToLowerInvariant();
        }
    }

    private static void MergeTerminal(TerminalMetadata target, TerminalMetadata incoming)
    {
        if (!string.IsNullOrWhiteSpace(incoming.TerminalApp)) target.TerminalApp = incoming.TerminalApp;
        if (!string.IsNullOrWhiteSpace(incoming.TerminalSessionId)) target.TerminalSessionId = incoming.TerminalSessionId;
        if (!string.IsNullOrWhiteSpace(incoming.TerminalTitle)) target.TerminalTitle = incoming.TerminalTitle;
        if (incoming.HostProcessId.HasValue) target.HostProcessId = incoming.HostProcessId;
        if (incoming.WindowHandle.HasValue && incoming.WindowHandle != 0) target.WindowHandle = incoming.WindowHandle;
        if (!string.IsNullOrWhiteSpace(incoming.DeepLinkUrl)) target.DeepLinkUrl = incoming.DeepLinkUrl;
        if (incoming.IsDesktopApp)
        {
            target.IsDesktopApp = true;
            if (!string.IsNullOrWhiteSpace(incoming.DesktopAppName)) target.DesktopAppName = incoming.DesktopAppName;
        }
    }
}
