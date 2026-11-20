using System.Collections.Concurrent;
using System.IO.Pipes;
using System.Text;
using System.Text.Json;
using OpenIsland.Core.Diagnostics;
using OpenIsland.Core.Hooks;
using OpenIsland.Core.Models;
using OpenIsland.Core.State;

namespace OpenIsland.Core.IPC;

/// <summary>
/// Asynchronous Windows Named Pipe server (\\.\pipe\open-island-bridge).
/// Replaces the macOS Unix Domain Socket BridgeServer while supporting both native hook CLIs
/// and Node.js / TypeScript extensions (OpenCode, Pi, Oh My Pi).
/// </summary>
public sealed class BridgeServer : IDisposable
{
    private readonly SessionStateReducer _reducer;
    private readonly CancellationTokenSource _cts = new();
    private readonly ConcurrentDictionary<string, TaskCompletionSource<BridgeResponse>> _pendingDirectives = new();
    private Task? _acceptLoopTask;

    public bool IsRunning { get; private set; }

    public BridgeServer(SessionStateReducer reducer)
    {
        _reducer = reducer;
    }

    public void Start()
    {
        if (IsRunning) return;
        IsRunning = true;
        _acceptLoopTask = Task.Run(() => AcceptLoopAsync(_cts.Token));
    }

    public void ResolveApproval(PendingApprovalRequest request, bool allow, string? reason = null, bool interrupt = false)
    {
        _reducer.ClearPendingApproval(request.SessionId, request.RequestId, allow);
        if (_pendingDirectives.TryRemove(request.RequestId, out var tcs))
        {
            AppLog.Info($"approval resolved allow={allow} request={request.RequestId}");
            var response = DirectiveEncoder.EncodeApprovalDecision(
                request.Source,
                request.HookEventName,
                allow,
                reason,
                interrupt);
            tcs.TrySetResult(response);
        }
    }

    public void ResolveQuestion(PendingQuestionRequest request, Dictionary<string, string> answers)
    {
        _reducer.ClearPendingQuestion(request.SessionId, request.RequestId);
        if (_pendingDirectives.TryRemove(request.RequestId, out var tcs))
        {
            tcs.TrySetResult(new BridgeResponse
            {
                Ok = true,
                Decision = "answer",
                Answers = answers
            });
        }
    }

    private async Task AcceptLoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            NamedPipeServerStream? pipe = null;
            try
            {
                pipe = new NamedPipeServerStream(
                    BridgeConstants.PipeName,
                    PipeDirection.InOut,
                    NamedPipeServerStream.MaxAllowedServerInstances,
                    PipeTransmissionMode.Byte,
                    PipeOptions.Asynchronous);

                await pipe.WaitForConnectionAsync(ct).ConfigureAwait(false);

                var connectedPipe = pipe;
                pipe = null; // Ownership transferred to connection handler
                _ = Task.Run(() => HandleClientAsync(connectedPipe, ct), ct);
            }
            catch (OperationCanceledException)
            {
                pipe?.Dispose();
                break;
            }
            catch
            {
                pipe?.Dispose();
                await Task.Delay(250, ct).ConfigureAwait(false);
            }
        }
    }

    private async Task HandleClientAsync(NamedPipeServerStream pipe, CancellationToken ct)
    {
        await using (pipe)
        {
            try
            {
                using var reader = new StreamReader(pipe, Encoding.UTF8, detectEncodingFromByteOrderMarks: false, bufferSize: 8192, leaveOpen: true);
                await using var writer = new StreamWriter(pipe, new UTF8Encoding(false), bufferSize: 4096, leaveOpen: true) { AutoFlush = true };

                var line = await reader.ReadLineAsync(ct).ConfigureAwait(false);
                if (string.IsNullOrWhiteSpace(line)) return;

                var envelope = JsonSerializer.Deserialize<BridgeEnvelope>(line, BridgeConstants.JsonOptions);
                if (envelope is null) return;

                var normalized = HookPayloadDecoder.Decode(envelope);
                var (approval, question) = _reducer.ApplyHookEvent(normalized);

                BridgeResponse response;

                if (envelope.ExpectsDirective && approval is not null)
                {
                    var tcs = new TaskCompletionSource<BridgeResponse>(TaskCreationOptions.RunContinuationsAsynchronously);
                    _pendingDirectives[approval.RequestId] = tcs;

                    var timeoutSec = envelope.TimeoutSeconds > 0 ? envelope.TimeoutSeconds : 3600;
                    using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                    timeoutCts.CancelAfter(TimeSpan.FromSeconds(timeoutSec));

                    try
                    {
                        response = await tcs.Task.WaitAsync(timeoutCts.Token).ConfigureAwait(false);
                    }
                    catch (OperationCanceledException)
                    {
                        _pendingDirectives.TryRemove(approval.RequestId, out _);
                        // Fail-open on timeout
                        response = new BridgeResponse { Ok = true };
                    }
                }
                else if (envelope.ExpectsDirective && question is not null)
                {
                    var tcs = new TaskCompletionSource<BridgeResponse>(TaskCreationOptions.RunContinuationsAsynchronously);
                    _pendingDirectives[question.RequestId] = tcs;

                    var timeoutSec = envelope.TimeoutSeconds > 0 ? envelope.TimeoutSeconds : 3600;
                    using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                    timeoutCts.CancelAfter(TimeSpan.FromSeconds(timeoutSec));

                    try
                    {
                        response = await tcs.Task.WaitAsync(timeoutCts.Token).ConfigureAwait(false);
                    }
                    catch (OperationCanceledException)
                    {
                        _pendingDirectives.TryRemove(question.RequestId, out _);
                        response = new BridgeResponse { Ok = true };
                    }
                }
                else
                {
                    response = new BridgeResponse { Ok = true };
                }

                var respJson = JsonSerializer.Serialize(response, BridgeConstants.JsonOptions);
                await writer.WriteLineAsync(respJson).ConfigureAwait(false);
            }
            catch
            {
                // Client disconnected or malformed payload; fail cleanly
            }
        }
    }

    public void Dispose()
    {
        IsRunning = false;
        _cts.Cancel();
        foreach (var kv in _pendingDirectives)
        {
            kv.Value.TrySetCanceled();
        }
        _pendingDirectives.Clear();
        _cts.Dispose();
    }
}
