using System.Diagnostics;
using OpenIsland.Core.Models;

namespace OpenIsland.Core.Platform;

/// <summary>
/// Detects the hosting Windows terminal, IDE, or Desktop App by inspecting environment variables
/// and walking the Win32 process ancestry tree.
/// </summary>
public static class TerminalDetector
{
    public static TerminalMetadata DetectCurrentHost()
    {
        var meta = new TerminalMetadata();
        int currentPid = Environment.ProcessId;
        var snapshot = Win32Native.GetProcessSnapshot();

        // 1. Desktop app environment signals (Claude Desktop, Codex Desktop, ZCode, Conductor)
        var claudeEntry = Environment.GetEnvironmentVariable("CLAUDE_CODE_ENTRYPOINT");
        if (string.Equals(claudeEntry, "claude-desktop", StringComparison.OrdinalIgnoreCase))
        {
            meta.IsDesktopApp = true;
            meta.DesktopAppName = "Claude";
            meta.TerminalApp = "Claude Desktop";
        }

        var codexOrigin = Environment.GetEnvironmentVariable("CODEX_INTERNAL_ORIGINATOR_OVERRIDE");
        if (!string.IsNullOrWhiteSpace(codexOrigin) && codexOrigin.Contains("desktop", StringComparison.OrdinalIgnoreCase))
        {
            meta.IsDesktopApp = true;
            meta.DesktopAppName = "Codex";
            meta.TerminalApp = "Codex Desktop";
        }

        if (!string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("CONDUCTOR_SESSION_ID")))
        {
            meta.IsDesktopApp = true;
            meta.DesktopAppName = "Conductor";
            meta.TerminalApp = "Conductor";
            meta.TerminalSessionId = Environment.GetEnvironmentVariable("CONDUCTOR_SESSION_ID");
        }

        // 2. Terminal / IDE environment variables
        if (string.IsNullOrWhiteSpace(meta.TerminalApp))
        {
            var wtSession = Environment.GetEnvironmentVariable("WT_SESSION");
            var termProgram = Environment.GetEnvironmentVariable("TERM_PROGRAM");
            var weztermPane = Environment.GetEnvironmentVariable("WEZTERM_PANE");
            var tmuxPane = Environment.GetEnvironmentVariable("TMUX_PANE");
            var zellijSession = Environment.GetEnvironmentVariable("ZELLIJ_SESSION_NAME");
            var warpFlag = Environment.GetEnvironmentVariable("WARP_IS_LOCAL_SHELL_SESSION");
            var ghosttyDir = Environment.GetEnvironmentVariable("GHOSTTY_RESOURCES_DIR");
            var jetbrains = Environment.GetEnvironmentVariable("TERMINAL_EMULATOR");

            if (!string.IsNullOrWhiteSpace(weztermPane))
            {
                meta.TerminalApp = "WezTerm";
                meta.TerminalSessionId = weztermPane;
            }
            else if (!string.IsNullOrWhiteSpace(wtSession))
            {
                meta.TerminalApp = "Windows Terminal";
                meta.TerminalSessionId = wtSession;
            }
            else if (!string.IsNullOrWhiteSpace(warpFlag))
            {
                meta.TerminalApp = "Warp";
            }
            else if (!string.IsNullOrWhiteSpace(ghosttyDir))
            {
                meta.TerminalApp = "Ghostty";
            }
            else if (!string.IsNullOrWhiteSpace(jetbrains) && jetbrains.Contains("JetBrains", StringComparison.OrdinalIgnoreCase))
            {
                meta.TerminalApp = "JetBrains";
            }
            else if (!string.IsNullOrWhiteSpace(termProgram))
            {
                meta.TerminalApp = termProgram.ToLowerInvariant() switch
                {
                    "vscode" => DetectVsCodeForkFromAncestry(snapshot, currentPid) ?? "VS Code",
                    "wezterm" => "WezTerm",
                    "zed" => "Zed",
                    _ => termProgram
                };
            }

            if (!string.IsNullOrWhiteSpace(tmuxPane))
            {
                meta.TerminalSessionId ??= tmuxPane;
                meta.TerminalApp ??= "tmux";
            }
            else if (!string.IsNullOrWhiteSpace(zellijSession))
            {
                meta.TerminalSessionId ??= zellijSession;
                meta.TerminalApp ??= "Zellij";
            }
        }

        // 3. Walk Win32 process ancestry to find the host GUI window HWND & fallback app name
        WalkAncestryForHostWindow(snapshot, currentPid, meta);

        return meta;
    }

    private static string? DetectVsCodeForkFromAncestry(Dictionary<int, Win32Native.ProcessSnapshotItem> snapshot, int pid)
    {
        int cur = pid;
        for (int depth = 0; depth < 12 && snapshot.TryGetValue(cur, out var item); depth++)
        {
            var exe = item.ExeName.ToLowerInvariant();
            if (exe.Contains("cursor")) return "Cursor";
            if (exe.Contains("windsurf")) return "Windsurf";
            if (exe.Contains("trae")) return "Trae";
            if (exe.Contains("code - insiders")) return "VS Code Insiders";
            if (exe == "code.exe") return "VS Code";
            if (item.ParentPid == cur || item.ParentPid <= 0) break;
            cur = item.ParentPid;
        }
        return null;
    }

    private static void WalkAncestryForHostWindow(
        Dictionary<int, Win32Native.ProcessSnapshotItem> snapshot,
        int startPid,
        TerminalMetadata meta)
    {
        int cur = startPid;
        for (int depth = 0; depth < 14 && snapshot.TryGetValue(cur, out var item); depth++)
        {
            var exe = item.ExeName.ToLowerInvariant();
            if (depth > 0)
            {
                string? matchedApp = exe switch
                {
                    "windowsterminal.exe" => "Windows Terminal",
                    "wezterm-gui.exe" => "WezTerm",
                    "alacritty.exe" => "Alacritty",
                    "warp.exe" => "Warp",
                    "ghostty.exe" => "Ghostty",
                    "code.exe" => "VS Code",
                    "cursor.exe" => "Cursor",
                    "windsurf.exe" => "Windsurf",
                    "trae.exe" => "Trae",
                    "zed.exe" => "Zed",
                    "idea64.exe" => "IntelliJ IDEA",
                    "webstorm64.exe" => "WebStorm",
                    "pycharm64.exe" => "PyCharm",
                    "goland64.exe" => "GoLand",
                    "clion64.exe" => "CLion",
                    "rider64.exe" => "Rider",
                    "rustrover64.exe" => "RustRover",
                    "claude.exe" => "Claude Desktop",
                    "chatgpt.exe" => "Codex Desktop",
                    "codex.exe" => "Codex Desktop",
                    "zcode.exe" => "ZCode Desktop",
                    "conhost.exe" or "powershell.exe" or "pwsh.exe" or "cmd.exe" => null,
                    _ => null
                };

                if (matchedApp != null)
                {
                    meta.TerminalApp ??= matchedApp;
                    meta.HostProcessId ??= item.Pid;
                    if (matchedApp is "Claude Desktop" or "Codex Desktop" or "ZCode Desktop")
                    {
                        meta.IsDesktopApp = true;
                        meta.DesktopAppName ??= matchedApp;
                    }
                }

                try
                {
                    using var proc = Process.GetProcessById(item.Pid);
                    if (proc.MainWindowHandle != IntPtr.Zero && meta.WindowHandle is null or 0)
                    {
                        meta.WindowHandle = proc.MainWindowHandle.ToInt64();
                        meta.HostProcessId ??= item.Pid;
                        if (string.IsNullOrWhiteSpace(meta.TerminalTitle) && !string.IsNullOrWhiteSpace(proc.MainWindowTitle))
                        {
                            meta.TerminalTitle = proc.MainWindowTitle;
                        }
                    }
                }
                catch
                {
                    // Process may have exited or denied access
                }
            }

            if (item.ParentPid == cur || item.ParentPid <= 0) break;
            cur = item.ParentPid;
        }
    }
}
