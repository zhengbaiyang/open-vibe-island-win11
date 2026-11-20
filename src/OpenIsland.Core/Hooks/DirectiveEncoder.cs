using System.Text.Json;
using OpenIsland.Core.Models;

namespace OpenIsland.Core.Hooks;

/// <summary>
/// Encodes exact stdout JSON directive responses matching Octane0411/open-vibe-island docs/hooks.md.
/// </summary>
public static class DirectiveEncoder
{
    public static BridgeResponse EncodeApprovalDecision(
        AgentSource source,
        string hookEventName,
        bool allow,
        string? reason = null,
        bool interrupt = false)
    {
        var resp = new BridgeResponse
        {
            Ok = true,
            Decision = allow ? "allow" : "deny",
            Reason = reason
        };

        // Claude Code & all Claude-compatible forks + Kimi CLI
        if (source is AgentSource.Claude or AgentSource.Qoder or AgentSource.Qwen
            or AgentSource.Factory or AgentSource.CodeBuddy or AgentSource.Kimi or AgentSource.ZCode)
        {
            if (string.Equals(hookEventName, "PermissionRequest", StringComparison.OrdinalIgnoreCase))
            {
                object decisionObj = allow
                    ? new { behavior = "allow" }
                    : new
                    {
                        behavior = "deny",
                        message = reason ?? "User denied the permission request in Open Island",
                        interrupt
                    };

                var payload = new
                {
                    @continue = true,
                    suppressOutput = true,
                    hookSpecificOutput = new
                    {
                        hookEventName = "PermissionRequest",
                        decision = decisionObj
                    }
                };
                resp.StdoutDirective = JsonSerializer.Serialize(payload);
            }
            else if (string.Equals(hookEventName, "PreToolUse", StringComparison.OrdinalIgnoreCase))
            {
                var payload = new
                {
                    @continue = true,
                    suppressOutput = true,
                    hookSpecificOutput = new
                    {
                        hookEventName = "PreToolUse",
                        permissionDecision = allow ? "allow" : "deny",
                        permissionDecisionReason = reason ?? (allow ? "Approved in Open Island" : "Denied in Open Island")
                    }
                };
                resp.StdoutDirective = JsonSerializer.Serialize(payload);
            }
        }
        // Codex CLI / Desktop
        else if (source == AgentSource.Codex)
        {
            if (string.Equals(hookEventName, "PermissionRequest", StringComparison.OrdinalIgnoreCase))
            {
                object decisionObj = allow
                    ? new { behavior = "allow" }
                    : new
                    {
                        behavior = "deny",
                        message = reason ?? "User denied the permission request"
                    };

                var payload = new
                {
                    @continue = true,
                    hookSpecificOutput = new
                    {
                        hookEventName = "PermissionRequest",
                        decision = decisionObj
                    }
                };
                resp.StdoutDirective = JsonSerializer.Serialize(payload);
            }
            else if (string.Equals(hookEventName, "PreToolUse", StringComparison.OrdinalIgnoreCase) && !allow)
            {
                var payload = new
                {
                    decision = "block",
                    reason = reason ?? "Blocked by Open Island"
                };
                resp.StdoutDirective = JsonSerializer.Serialize(payload);
            }
        }
        // Fire-and-forget agents (Gemini, Grok, Hermes, Pi, OMP, Cursor): no stdout directive
        return resp;
    }
}
