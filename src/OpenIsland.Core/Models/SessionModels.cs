using System.Text.Json.Serialization;

namespace OpenIsland.Core.Models;

public enum SessionPhase
{
    Idle,
    Running,
    WaitingForApproval,
    WaitingForAnswer,
    Completed,
    Error,
    Ended
}

public sealed class TerminalMetadata
{
    public string? TerminalApp { get; set; }
    public string? TerminalSessionId { get; set; }
    public string? TerminalTitle { get; set; }
    public int? HostProcessId { get; set; }
    public long? WindowHandle { get; set; }
    public string? DeepLinkUrl { get; set; }
    public bool IsDesktopApp { get; set; }
    public string? DesktopAppName { get; set; }
}

public sealed class PendingApprovalRequest
{
    public string RequestId { get; set; } = Guid.NewGuid().ToString("N");
    public string SessionId { get; set; } = "";
    public AgentSource Source { get; set; }
    public string HookEventName { get; set; } = "PermissionRequest";
    public string ToolName { get; set; } = "";
    public string Summary { get; set; } = "";
    public string? CommandOrDetails { get; set; }
    public bool IsBlocking { get; set; } = true;
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
}

public sealed class QuestionOption
{
    public string Label { get; set; } = "";
    public string? Description { get; set; }
}

public sealed class PendingQuestionItem
{
    public string Header { get; set; } = "";
    public string Question { get; set; } = "";
    public bool MultiSelect { get; set; }
    public List<QuestionOption> Options { get; set; } = new();
}

public sealed class PendingQuestionRequest
{
    public string RequestId { get; set; } = Guid.NewGuid().ToString("N");
    public string SessionId { get; set; } = "";
    public AgentSource Source { get; set; }
    public List<PendingQuestionItem> Questions { get; set; } = new();
    public bool IsBlocking { get; set; }
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
}

public sealed class CompletionCard
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string SessionId { get; set; } = "";
    public AgentSource Source { get; set; }
    public string Title { get; set; } = "";
    public string? UserPrompt { get; set; }
    public string? AssistantMessage { get; set; }
    public bool IsInterrupt { get; set; }
    public bool IsError { get; set; }
    public DateTimeOffset Timestamp { get; set; } = DateTimeOffset.UtcNow;
}

public sealed class AgentSession
{
    public string SessionId { get; set; } = "";
    public AgentSource Source { get; set; } = AgentSource.Unknown;
    public string Title { get; set; } = "";
    public string? Cwd { get; set; }
    public string? Model { get; set; }
    public string? PermissionMode { get; set; }
    public string? TranscriptPath { get; set; }
    public SessionPhase Phase { get; set; } = SessionPhase.Idle;
    public string? ActiveToolName { get; set; }
    public string? ActiveToolPreview { get; set; }
    public string? LastPrompt { get; set; }
    public string? LastAssistantMessage { get; set; }
    public string? StatusSummary { get; set; }
    public int ActiveSubagentCount { get; set; }
    public bool IsProcessAlive { get; set; } = true;
    public bool IsHookManaged { get; set; }
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset LastHeartbeatAt { get; set; } = DateTimeOffset.UtcNow;
    public TerminalMetadata Terminal { get; set; } = new();

    [JsonIgnore]
    public PendingApprovalRequest? PendingApproval { get; set; }

    [JsonIgnore]
    public PendingQuestionRequest? PendingQuestion { get; set; }

    [JsonIgnore]
    public string DisplayTitle
    {
        get
        {
            if (!string.IsNullOrWhiteSpace(Title)) return Title;
            if (!string.IsNullOrWhiteSpace(LastPrompt))
            {
                var prompt = LastPrompt.Trim().Replace('\r', ' ').Replace('\n', ' ');
                if (prompt.Length > 60) prompt = prompt[..60] + "…";
                if (!string.IsNullOrWhiteSpace(prompt)) return prompt;
            }
            if (!string.IsNullOrWhiteSpace(Cwd))
            {
                var trimmed = Cwd.TrimEnd('\\', '/');
                var leaf = Path.GetFileName(trimmed);
                if (!string.IsNullOrWhiteSpace(leaf)) return leaf;
            }
            return AgentRegistry.Get(Source).DisplayName;
        }
    }
}

public sealed class UsageTokenWindow
{
    public long InputTokens { get; set; }
    public long CacheReadTokens { get; set; }
    public long OutputTokens { get; set; }
    public int RequestCount { get; set; }

    [JsonIgnore]
    public long TotalTokens => InputTokens + CacheReadTokens + OutputTokens;
}

public sealed class ModelUsageBreakdown
{
    public string Model { get; set; } = "";
    public long FiveHourTokens { get; set; }
    public long SevenDayTokens { get; set; }
    public int RequestCount { get; set; }
}

public sealed class UsageWindowInfo
{
    public AgentSource Source { get; set; } = AgentSource.Unknown;
    public string AgentName { get; set; } = "";
    public bool IsConnected { get; set; }
    public bool SupportsRemainingQuota { get; set; } = true;
    public bool HasQuotaData { get; set; }
    public double FiveHourPercent { get; set; }
    public string? FiveHourResetText { get; set; }
    public double SevenDayPercent { get; set; }
    public string? SevenDayResetText { get; set; }
    public DateTimeOffset? LastUpdated { get; set; }
    public string? Note { get; set; }

    [JsonIgnore]
    public double FiveHourRemainingPercent => Math.Clamp(100.0 - FiveHourPercent, 0.0, 100.0);

    [JsonIgnore]
    public double SevenDayRemainingPercent => Math.Clamp(100.0 - SevenDayPercent, 0.0, 100.0);

    [JsonIgnore]
    public bool HasTokenData =>
        SevenDayTokens.RequestCount > 0 ||
        FiveHourTokens.RequestCount > 0 ||
        SevenDayTokens.TotalTokens > 0 ||
        FiveHourTokens.TotalTokens > 0;

    /// <summary>
    /// Legacy compatibility flag; unified usage cards now check SupportsRemainingQuota / HasQuotaData.
    /// </summary>
    public bool IsTokenMetricMode { get; set; }
    public UsageTokenWindow FiveHourTokens { get; set; } = new();
    public UsageTokenWindow SevenDayTokens { get; set; } = new();
    public List<ModelUsageBreakdown> TopModels { get; set; } = new();
    public string? OfficialQuotaHint { get; set; }
}
