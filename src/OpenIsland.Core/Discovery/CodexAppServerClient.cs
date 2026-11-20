using System.Diagnostics;
using System.Text.Json;
using OpenIsland.Core.Hooks;
using OpenIsland.Core.Models;
using OpenIsland.Core.State;

namespace OpenIsland.Core.Discovery;

/// <summary>
/// Connects to `codex app-server` over stdio JSON-RPC (when Codex CLI is installed on Windows)
/// to receive real-time thread/started, turn/started, turn/completed, and thread/closed notifications.
/// </summary>
public sealed class CodexAppServerClient : IDisposable
{
    private readonly SessionStateReducer _reducer;
    private Process? _process;
    private CancellationTokenSource? _cts;

    public CodexAppServerClient(SessionStateReducer reducer)
    {
        _reducer = reducer;
    }

    public void StartIfAvailable()
    {
        try
        {
            _cts = new CancellationTokenSource();
            var psi = new ProcessStartInfo
            {
                FileName = "codex",
                Arguments = "app-server",
                UseShellExecute = false,
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true
            };

            _process = Process.Start(psi);
            if (_process == null) return;

            // Send JSON-RPC initialize handshake
            var initReq = JsonSerializer.Serialize(new
            {
                jsonrpc = "2.0",
                id = 1,
                method = "initialize",
                @params = new { clientInfo = new { name = "open-island-win11", version = "0.1.0" } }
            });
            _process.StandardInput.WriteLine(initReq);
            _process.StandardInput.Flush();

            _ = Task.Run(() => ReadLoopAsync(_cts.Token));
        }
        catch
        {
            // Codex CLI not installed or does not support app-server; fail silently
        }
    }

    private async Task ReadLoopAsync(CancellationToken ct)
    {
        if (_process == null) return;
        try
        {
            while (!ct.IsCancellationRequested && !_process.HasExited)
            {
                var line = await _process.StandardOutput.ReadLineAsync(ct).ConfigureAwait(false);
                if (line == null) break;
                if (string.IsNullOrWhiteSpace(line)) continue;
                HandleJsonRpcLine(line);
            }
        }
        catch { }
    }

    private void HandleJsonRpcLine(string line)
    {
        try
        {
            using var doc = JsonDocument.Parse(line);
            var root = doc.RootElement;
            if (!root.TryGetProperty("method", out var methodEl)) return;
            var method = methodEl.GetString();
            if (string.IsNullOrWhiteSpace(method)) return;

            string? threadId = null;
            string? cwd = null;
            if (root.TryGetProperty("params", out var p) && p.ValueKind == JsonValueKind.Object)
            {
                if (p.TryGetProperty("threadId", out var tid)) threadId = tid.GetString();
                else if (p.TryGetProperty("thread_id", out var tid2)) threadId = tid2.GetString();
                if (p.TryGetProperty("cwd", out var c)) cwd = c.GetString();
            }

            if (string.IsNullOrWhiteSpace(threadId)) return;

            var mappedEvent = method switch
            {
                "thread/started" => "SessionStart",
                "turn/started" => "UserPromptSubmit",
                "turn/completed" => "Stop",
                "thread/closed" => "SessionEnd",
                _ => null
            };

            if (mappedEvent == null) return;

            _reducer.ApplyHookEvent(new NormalizedHookEvent
            {
                Source = AgentSource.Codex,
                EventName = mappedEvent,
                SessionId = threadId,
                ThreadId = threadId,
                Cwd = cwd,
                Terminal = new TerminalMetadata
                {
                    IsDesktopApp = true,
                    DesktopAppName = "Codex Desktop",
                    TerminalApp = "Codex Desktop",
                    DeepLinkUrl = $"codex://threads/{threadId}"
                }
            });
        }
        catch { }
    }

    public void Dispose()
    {
        _cts?.Cancel();
        try
        {
            if (_process is { HasExited: false })
            {
                _process.Kill(entireProcessTree: true);
            }
        }
        catch { }
        _process?.Dispose();
        _cts?.Dispose();
    }
}
