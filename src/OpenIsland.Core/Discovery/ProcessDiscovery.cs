using OpenIsland.Core.Models;
using OpenIsland.Core.Platform;

namespace OpenIsland.Core.Discovery;

/// <summary>
/// Windows native process discovery (replaces macOS ps / lsof / NSWorkspace.runningApplications).
/// </summary>
public static class ProcessDiscovery
{
    public static (HashSet<AgentSource> AliveAgents, HashSet<int> AlivePids) ScanRunningAgents()
    {
        var aliveAgents = new HashSet<AgentSource>();
        var alivePids = new HashSet<int>();
        var snapshot = Win32Native.GetProcessSnapshot();

        foreach (var item in snapshot.Values)
        {
            alivePids.Add(item.Pid);
            var exe = item.ExeName.ToLowerInvariant();

            if (exe is "claude.exe" or "claude-code.exe") aliveAgents.Add(AgentSource.Claude);
            else if (exe is "codex.exe" or "codex-cli.exe" or "chatgpt.exe") aliveAgents.Add(AgentSource.Codex);
            else if (exe is "opencode.exe") aliveAgents.Add(AgentSource.OpenCode);
            else if (exe is "cursor.exe") aliveAgents.Add(AgentSource.Cursor);
            else if (exe is "gemini.exe") aliveAgents.Add(AgentSource.Gemini);
            else if (exe is "antigravity.exe" or "antigravity-ide.exe" or "antigravity-desktop.exe" or "agy.exe"
                     || exe.Contains("antigravity", StringComparison.Ordinal))
                aliveAgents.Add(AgentSource.Antigravity);
            else if (exe is "kimi.exe") aliveAgents.Add(AgentSource.Kimi);
            else if (exe is "grok.exe") aliveAgents.Add(AgentSource.Grok);
            else if (exe is "hermes.exe") aliveAgents.Add(AgentSource.Hermes);
            else if (exe is "qoder.exe") aliveAgents.Add(AgentSource.Qoder);
            else if (exe is "qwen.exe") aliveAgents.Add(AgentSource.Qwen);
            else if (exe is "factory.exe") aliveAgents.Add(AgentSource.Factory);
            else if (exe is "codebuddy.exe") aliveAgents.Add(AgentSource.CodeBuddy);
            else if (exe is "zcode.exe") aliveAgents.Add(AgentSource.ZCode);
        }

        return (aliveAgents, alivePids);
    }
}
