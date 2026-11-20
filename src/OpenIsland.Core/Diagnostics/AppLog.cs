using System.Text;
using OpenIsland.Core.Models;

namespace OpenIsland.Core.Diagnostics;

/// <summary>
/// Minimal thread-safe file logger. Everything the app does with agents (hook events, installs,
/// bridge lifecycle) is appended to <c>%LOCALAPPDATA%\OpenIsland\logs\open-island.log</c> so a
/// user can diagnose "why is my agent not showing up?" without a debugger.
/// If the primary location is not writable (locked-down machine, portable/USB install, sandbox),
/// the logger transparently falls back to the application folder and then to %TEMP%.
/// </summary>
public static class AppLog
{
    private static readonly object Gate = new();
    private const long MaxBytes = 512 * 1024;
    private static string? _effectivePath;
    private static string? _fallbackPath;

    public static string LogPath => BridgeConstants.LogPath;

    /// <summary>Path actually being written to (primary or fallback).</summary>
    public static string EffectiveLogPath => _effectivePath ?? _fallbackPath ?? BridgeConstants.LogPath;

    public static void Info(string message) => Write("INFO", message);
    public static void Warn(string message) => Write("WARN", message);

    public static void Error(string message, Exception? ex = null) =>
        Write("ERROR", ex is null ? message : $"{message} :: {ex.GetType().Name}: {ex.Message}");

    public static void Hook(string message) => Write("HOOK", message);

    public static void Write(string level, string message)
    {
        lock (Gate)
        {
            var line = $"{DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss.fff} [{level,-5}] {message}{Environment.NewLine}";

            if (TryAppend(BridgeConstants.LogPath, line))
            {
                _effectivePath = BridgeConstants.LogPath;
                return;
            }

            var fallback = _fallbackPath ??= ResolveFallbackPath();
            if (fallback != null && TryAppend(fallback, line))
            {
                _effectivePath = fallback;
            }
        }
    }

    private static bool TryAppend(string path, string line)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            RotateIfNeeded(path);
            File.AppendAllText(path, line, Encoding.UTF8);
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static string? ResolveFallbackPath()
    {
        var candidates = new[]
        {
            Path.Combine(AppContext.BaseDirectory, "open-island.log"),
            Path.Combine(Path.GetTempPath(), "open-island.log")
        };
        foreach (var candidate in candidates)
        {
            if (TryAppend(candidate, "")) return candidate;
        }
        return null;
    }

    /// <summary>Returns the last <paramref name="count"/> log lines (newest last).</summary>
    public static string[] Tail(int count = 200)
    {
        foreach (var path in new[] { BridgeConstants.LogPath, EffectiveLogPath })
        {
            try
            {
                if (!File.Exists(path)) continue;
                var lines = new Queue<string>(count + 1);
                using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                using var reader = new StreamReader(fs, Encoding.UTF8);
                string? line;
                while ((line = reader.ReadLine()) != null)
                {
                    if (lines.Count >= count) lines.Dequeue();
                    lines.Enqueue(line);
                }
                return lines.ToArray();
            }
            catch
            {
                // Try next candidate.
            }
        }
        return Array.Empty<string>();
    }

    private static void RotateIfNeeded(string path)
    {
        var info = new FileInfo(path);
        if (!info.Exists || info.Length <= MaxBytes) return;
        var backup = path + ".old";
        try
        {
            if (File.Exists(backup)) File.Delete(backup);
            File.Move(path, backup);
        }
        catch
        {
            // If rotation fails, just keep appending.
        }
    }
}
