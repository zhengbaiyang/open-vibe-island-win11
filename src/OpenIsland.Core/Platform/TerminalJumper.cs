using System.Diagnostics;
using OpenIsland.Core.Diagnostics;
using OpenIsland.Core.Models;

namespace OpenIsland.Core.Platform;

/// <summary>
/// Jumps back to the host of an agent session. Priority:
/// 1. the live terminal window that hosts the session;
/// 2. exact conversation deep link (codex://threads/&lt;id&gt; opens that chat in Codex App/ChatGPT);
/// 3. resuming the exact conversation in a new terminal (codex resume / claude --resume);
/// 4. activating the desktop app window (Codex/ChatGPT) or opening its conversation list;
/// 5. IDE workspace / process / window-title matching;
/// 6. last resort: open the working directory in Explorer.
///
/// Every attempt is written to the Open Island log (Settings → 打开日志) so a missing jump can
/// be diagnosed on another machine.
/// </summary>
public static class TerminalJumper
{
    public static bool JumpToSession(AgentSession session)
    {
        try
        {
            var ok = JumpCore(session);
            AppLog.Info(
                $"jump {(ok ? "ok" : "failed")}: source={session.Source} id={session.SessionId} " +
                $"title='{session.DisplayTitle}' cwd='{session.Cwd}' app='{session.Terminal.TerminalApp}' " +
                $"deep='{session.Terminal.DeepLinkUrl}' hwnd={session.Terminal.WindowHandle}");
            return ok;
        }
        catch (Exception ex)
        {
            AppLog.Warn($"jump crashed for {session.Source}/{session.SessionId}: {ex.Message}");
            return false;
        }
    }

    private static bool JumpCore(AgentSession session)
    {
        var term = session.Terminal;

        // DeepSeek Harness is a desktop app: bring its window to the foreground.
        if (session.Source == AgentSource.DeepSeekHarness)
        {
            if (TryActivateProcessWindow("DeepSeek Harness")) return true;
            if (TryOpenUrl("dsh://") && TryActivateProcessWindow("DeepSeek Harness")) return true;
            return false;
        }

        // 1. Live host window (exact TUI / IDE window).
        if (term.WindowHandle is long rawHwnd && rawHwnd != 0)
        {
            var hwnd = new IntPtr(rawHwnd);
            if (Win32Native.IsWindowVisible(hwnd) || Win32Native.IsIconic(hwnd))
            {
                if (Win32Native.ForceActivateWindow(hwnd)) return true;
            }
        }

        // 2. Exact conversation deep link (Codex App / ChatGPT register codex://).
        if (TryOpenCodexDeepLink(session)) return true;

        // 3. Resume the exact conversation / activate the desktop app.
        if (session.Source == AgentSource.Codex)
        {
            if (IsConversationGuid(session.SessionId) && TryResumeCodex(session.SessionId, session.Cwd))
                return true;

            // A running/hidden Codex or ChatGPT app window is better than opening a folder.
            if (TryActivateProcessWindow("ChatGPT", "Codex", "codex")) return true;

            // Launch the app at its conversation list, then activate it.
            if (TryOpenUrl("codex://") && TryActivateProcessWindow("ChatGPT", "Codex", "codex"))
                return true;

            // CLI fallback: resume the most recent conversation in this cwd.
            if (TryResumeCodex(null, session.Cwd)) return true;
        }

        if (session.Source == AgentSource.Claude
            && IsConversationGuid(session.SessionId)
            && TryResumeClaude(session.SessionId, session.Cwd))
        {
            return true;
        }

        // 4. WezTerm CLI pane targeting
        if (string.Equals(term.TerminalApp, "WezTerm", StringComparison.OrdinalIgnoreCase)
            && !string.IsNullOrWhiteSpace(term.TerminalSessionId))
        {
            TryRunSilent("wezterm", $"cli activate-pane --pane-id {term.TerminalSessionId}");
        }

        // 5. VS Code / Cursor / Windsurf / Trae CLI workspace jump (-r reuses matching workspace window)
        var appLower = (term.TerminalApp ?? "").ToLowerInvariant();
        if (!string.IsNullOrWhiteSpace(session.Cwd) && Directory.Exists(session.Cwd))
        {
            string? ideCli = appLower switch
            {
                "vs code" or "vscode" => "code",
                "vs code insiders" => "code-insiders",
                "cursor" => "cursor",
                "windsurf" => "windsurf",
                "trae" => "trae",
                "zed" => "zed",
                _ => null
            };

            if (ideCli != null && TryRunSilent("cmd.exe", $"/c {ideCli} -r \"{session.Cwd}\""))
            {
                return true;
            }
        }

        // 6. Find visible window matching HostProcessId, TerminalApp, DesktopAppName, or CWD folder name
        if (term.HostProcessId is int hostPid)
        {
            try
            {
                using var proc = Process.GetProcessById(hostPid);
                if (proc.MainWindowHandle != IntPtr.Zero)
                {
                    return Win32Native.ForceActivateWindow(proc.MainWindowHandle);
                }
            }
            catch { }
        }

        var folderName = !string.IsNullOrWhiteSpace(session.Cwd)
            ? Path.GetFileName(session.Cwd.TrimEnd('\\', '/'))
            : null;

        IntPtr bestMatch = IntPtr.Zero;
        IntPtr fallbackMatch = IntPtr.Zero;

        Win32Native.EnumWindows((hWnd, _) =>
        {
            if (!Win32Native.IsWindowVisible(hWnd)) return true;
            var title = Win32Native.GetWindowTitle(hWnd);
            if (string.IsNullOrWhiteSpace(title)) return true;

            bool matchesFolder = !string.IsNullOrWhiteSpace(folderName)
                                 && title.Contains(folderName, StringComparison.OrdinalIgnoreCase);
            bool matchesApp = !string.IsNullOrWhiteSpace(term.TerminalApp)
                              && title.Contains(term.TerminalApp, StringComparison.OrdinalIgnoreCase);
            bool matchesDesktop = !string.IsNullOrWhiteSpace(term.DesktopAppName)
                                  && title.Contains(term.DesktopAppName, StringComparison.OrdinalIgnoreCase);

            if (matchesFolder && (matchesApp || matchesDesktop))
            {
                bestMatch = hWnd;
                return false;
            }
            if ((matchesFolder || matchesApp || matchesDesktop) && fallbackMatch == IntPtr.Zero)
            {
                fallbackMatch = hWnd;
            }
            return true;
        }, IntPtr.Zero);

        var target = bestMatch != IntPtr.Zero ? bestMatch : fallbackMatch;
        if (target != IntPtr.Zero)
        {
            return Win32Native.ForceActivateWindow(target);
        }

        // 7. Final fallbacks: desktop app windows, then the working folder in Explorer.
        if (session.Source == AgentSource.Codex && TryActivateProcessWindow("ChatGPT", "Codex")) return true;
        if (!string.IsNullOrWhiteSpace(session.Cwd) && Directory.Exists(session.Cwd))
        {
            return TryOpenFolder(session.Cwd);
        }

        return false;
    }

    // ---------------------------------------------------------------------
    // Conversation-level jumps
    // ---------------------------------------------------------------------

    private static bool TryOpenCodexDeepLink(AgentSession session)
    {
        if (session.Source != AgentSource.Codex) return false;

        var candidates = new List<string>();
        if (!string.IsNullOrWhiteSpace(session.Terminal.DeepLinkUrl)
            && IsUsableDeepLink(session, session.Terminal.DeepLinkUrl))
        {
            candidates.Add(session.Terminal.DeepLinkUrl);
        }
        if (IsConversationGuid(session.SessionId))
        {
            var url = $"codex://threads/{session.SessionId}";
            if (!candidates.Contains(url, StringComparer.OrdinalIgnoreCase)) candidates.Add(url);
        }

        foreach (var url in candidates)
        {
            if (TryOpenUrl(url))
            {
                // Some Codex builds open the conversation without activating the window.
                TryActivateProcessWindow("ChatGPT", "Codex");
                return true;
            }
        }
        return false;
    }

    private static bool TryOpenUrl(string url)
    {
        try
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = url,
                UseShellExecute = true
            });
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static bool TryResumeCodex(string? sessionId, string? cwd)
    {
        var codexExe = FindCodexExecutable();
        if (string.IsNullOrWhiteSpace(codexExe))
        {
            AppLog.Warn("jump: codex executable not found (PATH / %LOCALAPPDATA%\\OpenAI\\Codex)");
            return false;
        }
        var args = !string.IsNullOrWhiteSpace(sessionId)
            ? $"resume {sessionId}"
            : "resume --last";
        return LaunchInNewConsole(codexExe, args, cwd);
    }

    private static bool TryResumeClaude(string sessionId, string? cwd)
    {
        var claudeExe = FindOnPath("claude.exe") ?? FindOnPath("claude.cmd") ?? "claude";
        return LaunchInNewConsole(claudeExe, $"--resume {sessionId}", cwd);
    }

    /// <summary>Opens a new console window running the given console program.</summary>
    private static bool LaunchInNewConsole(string fileName, string arguments, string? cwd)
    {
        try
        {
            var workingDir = !string.IsNullOrWhiteSpace(cwd) && Directory.Exists(cwd)
                ? cwd
                : Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

            var quotedExe = fileName.Contains(' ')
                ? $"\"{fileName}\""
                : fileName;

            // "start" creates a new console window (Windows Terminal is the default
            // console host on Windows 11) running the resumed conversation.
            Process.Start(new ProcessStartInfo
            {
                FileName = "cmd.exe",
                Arguments = $"/c start \"Open Island\" {quotedExe} {arguments}",
                WorkingDirectory = workingDir,
                UseShellExecute = false,
                CreateNoWindow = true
            });
            return true;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>Brings the first visible window of the given process names to the foreground.</summary>
    private static bool TryActivateProcessWindow(params string[] processNames)
    {
        foreach (var name in processNames)
        {
            try
            {
                foreach (var process in Process.GetProcessesByName(name))
                {
                    try
                    {
                        var hwnd = process.MainWindowHandle;
                        if (hwnd != IntPtr.Zero && Win32Native.ForceActivateWindow(hwnd))
                        {
                            return true;
                        }
                    }
                    finally
                    {
                        process.Dispose();
                    }
                }
            }
            catch { }
        }
        return false;
    }

    private static bool TryOpenFolder(string cwd)
    {
        try
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = "explorer.exe",
                Arguments = $"\"{cwd}\"",
                UseShellExecute = true
            });
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static string? FindCodexExecutable()
    {
        var fromPath = FindOnPath("codex.exe") ?? FindOnPath("codex.cmd");
        if (fromPath != null) return fromPath;

        var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        var candidates = new List<string>
        {
            Path.Combine(localAppData, "OpenAI", "Codex", "bin", "codex.exe"),
            Path.Combine(localAppData, "OpenAI", "Codex", "bin", "codex.cmd"),
            Path.Combine(appData, "npm", "codex.cmd"),
            Path.Combine(appData, "npm", "codex"),
            Path.Combine(localAppData, "Programs", "codex", "codex.exe")
        };

        try
        {
            var versionedRoot = Path.Combine(localAppData, "OpenAI", "Codex", "bin");
            if (Directory.Exists(versionedRoot))
            {
                candidates.AddRange(Directory.EnumerateFiles(versionedRoot, "codex.exe", SearchOption.AllDirectories));
                candidates.AddRange(Directory.EnumerateFiles(versionedRoot, "codex.cmd", SearchOption.AllDirectories));
            }
        }
        catch { }

        // `where codex` catches installs that are only on the user PATH of another shell.
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = "cmd.exe",
                Arguments = "/c where codex.exe 2>nul & where codex.cmd 2>nul",
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true
            };
            using var proc = Process.Start(psi);
            if (proc != null)
            {
                var output = proc.StandardOutput.ReadToEnd();
                proc.WaitForExit(1500);
                candidates.AddRange(output
                    .Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries)
                    .Select(line => line.Trim())
                    .Where(File.Exists));
            }
        }
        catch { }

        return candidates
            .Where(File.Exists)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderByDescending(File.GetLastWriteTimeUtc)
            .FirstOrDefault();
    }

    private static string? FindOnPath(string fileName)
    {
        try
        {
            var path = Environment.GetEnvironmentVariable("PATH") ?? "";
            foreach (var dir in path.Split(';', StringSplitOptions.RemoveEmptyEntries))
            {
                try
                {
                    var candidate = Path.Combine(dir.Trim(), fileName);
                    if (File.Exists(candidate)) return candidate;
                }
                catch { }
            }
        }
        catch { }
        return null;
    }

    private static bool IsConversationGuid(string? id)
        => !string.IsNullOrWhiteSpace(id) && Guid.TryParse(id, out _);

    /// <summary>Validates a codex:// deep link so we never open a bogus/empty thread.</summary>
    private static bool IsUsableDeepLink(AgentSession session, string url)
    {
        if (session.Source != AgentSource.Codex) return true;
        const string prefix = "codex://threads/";
        if (!url.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) return true;
        var threadId = url[prefix.Length..].Trim('/');
        return IsConversationGuid(threadId);
    }

    private static bool TryRunSilent(string fileName, string arguments)
    {
        try
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = fileName,
                Arguments = arguments,
                UseShellExecute = false,
                CreateNoWindow = true
            });
            return true;
        }
        catch
        {
            return false;
        }
    }
}
