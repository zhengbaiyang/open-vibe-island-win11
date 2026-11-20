using System.Text;
using System.Text.Json;
using OpenIsland.Core.IPC;
using OpenIsland.Core.Models;
using OpenIsland.Core.Platform;

namespace OpenIsland.Hooks;

internal static class Program
{
    private static async Task<int> Main(string[] args)
    {
        // Delegated-control skip guard (mirrors macOS OpenIslandHooks)
        if (Environment.GetEnvironmentVariable("OPEN_ISLAND_SKIP_HOOKS") == "1"
            || Environment.GetEnvironmentVariable("VIBE_ISLAND_SKIP") == "1")
        {
            return 0;
        }

        string sourceArg = "claude";
        for (int i = 0; i < args.Length; i++)
        {
            if ((args[i] == "--source" || args[i] == "-s") && i + 1 < args.Length)
            {
                sourceArg = args[i + 1];
                i++;
            }
        }

        var source = AgentRegistry.Parse(sourceArg);

        string rawPayload = "{}";
        try
        {
            if (Console.IsInputRedirected)
            {
                using var reader = new StreamReader(Console.OpenStandardInput(), Encoding.UTF8);
                var input = await reader.ReadToEndAsync();
                if (!string.IsNullOrWhiteSpace(input)) rawPayload = input.Trim();
            }
        }
        catch
        {
            // Fail open
            return 0;
        }

        var eventName = PeekEventName(rawPayload);
        bool expectsDirective = ShouldBlockForDirective(source, eventName);
        int timeoutSeconds = ComputeTimeoutSeconds(source, eventName);

        var host = TerminalDetector.DetectCurrentHost();
        var envelope = new BridgeEnvelope
        {
            Source = sourceArg,
            RawPayload = rawPayload,
            Cwd = Environment.CurrentDirectory,
            ProcessId = Environment.ProcessId,
            ParentProcessId = host.HostProcessId,
            TerminalApp = host.TerminalApp,
            TerminalSessionId = host.TerminalSessionId,
            TerminalTitle = host.TerminalTitle,
            WindowHandle = host.WindowHandle,
            IsDesktopApp = host.IsDesktopApp,
            DesktopAppName = host.DesktopAppName,
            ExpectsDirective = expectsDirective,
            TimeoutSeconds = timeoutSeconds
        };

        var response = await BridgeClient.SendAsync(envelope);

        // Only write directive JSON to stdout for blocking hook sources (fire-and-forget never writes stdout)
        if (expectsDirective && response is { Ok: true } && !string.IsNullOrWhiteSpace(response.StdoutDirective))
        {
            Console.OutputEncoding = new UTF8Encoding(false);
            Console.Write(response.StdoutDirective);
            Console.Out.Flush();
        }

        return 0;
    }

    private static string PeekEventName(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            foreach (var key in new[] { "hook_event_name", "hookEventName", "event", "type" })
            {
                if (root.TryGetProperty(key, out var prop) && prop.ValueKind == JsonValueKind.String)
                    return prop.GetString() ?? "";
            }
        }
        catch { }
        return "";
    }

    private static bool ShouldBlockForDirective(AgentSource source, string eventName)
    {
        if (string.IsNullOrWhiteSpace(eventName)) return false;
        var norm = eventName.Trim().ToLowerInvariant().Replace("_", "");

        if (source is AgentSource.Claude or AgentSource.Qoder or AgentSource.Qwen
            or AgentSource.Factory or AgentSource.CodeBuddy or AgentSource.Kimi)
        {
            return norm == "permissionrequest";
        }

        if (source == AgentSource.Codex)
        {
            return norm == "permissionrequest";
        }

        // Gemini, Grok, Hermes, Cursor, ZCode, Pi, OMP are fire-and-forget
        return false;
    }

    private static int ComputeTimeoutSeconds(AgentSource source, string eventName)
    {
        var norm = eventName.Trim().ToLowerInvariant().Replace("_", "");
        if (norm == "permissionrequest")
        {
            if (source == AgentSource.Codex) return 3600;       // 1 hour
            return 86400;                                        // 24 hours (Claude & forks)
        }
        return 45;
    }
}
