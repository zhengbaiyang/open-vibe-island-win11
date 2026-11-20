using System.IO.Pipes;
using System.Text;
using System.Text.Json;
using OpenIsland.Core.Models;

namespace OpenIsland.Core.IPC;

/// <summary>
/// Fail-open Windows Named Pipe client used by OpenIslandHooks.exe.
/// If OpenIsland.App is not running, ConnectAsync times out quickly and returns null without failing the agent.
/// </summary>
public static class BridgeClient
{
    public static async Task<BridgeResponse?> SendAsync(
        BridgeEnvelope envelope,
        int connectTimeoutMs = 400,
        CancellationToken ct = default)
    {
        try
        {
            await using var pipe = new NamedPipeClientStream(
                ".",
                BridgeConstants.PipeName,
                PipeDirection.InOut,
                PipeOptions.Asynchronous);

            await pipe.ConnectAsync(connectTimeoutMs, ct).ConfigureAwait(false);

            await using var writer = new StreamWriter(pipe, new UTF8Encoding(false), bufferSize: 4096, leaveOpen: true) { AutoFlush = true };
            using var reader = new StreamReader(pipe, Encoding.UTF8, detectEncodingFromByteOrderMarks: false, bufferSize: 4096, leaveOpen: true);

            var payloadLine = JsonSerializer.Serialize(envelope, BridgeConstants.JsonOptions);
            await writer.WriteLineAsync(payloadLine).ConfigureAwait(false);

            var readTimeout = TimeSpan.FromSeconds(Math.Max(5, envelope.TimeoutSeconds));
            using var readCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            readCts.CancelAfter(readTimeout);

            var responseLine = await reader.ReadLineAsync(readCts.Token).ConfigureAwait(false);
            if (string.IsNullOrWhiteSpace(responseLine)) return null;

            return JsonSerializer.Deserialize<BridgeResponse>(responseLine, BridgeConstants.JsonOptions);
        }
        catch
        {
            // Fail-open principle: never block or crash the coding agent when Open Island is unavailable.
            return null;
        }
    }
}
