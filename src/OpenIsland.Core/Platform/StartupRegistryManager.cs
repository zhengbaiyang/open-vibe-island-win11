using System.Diagnostics;
using Microsoft.Win32;
using OpenIsland.Core.Diagnostics;

namespace OpenIsland.Core.Platform;

/// <summary>
/// Manages the per-user Windows startup registry entry under
/// <c>HKCU\Software\Microsoft\Windows\CurrentVersion\Run</c>.
/// Value name <c>"OpenIsland"</c> matches <c>installer/OpenIsland.iss</c> uninstall cleanup.
/// </summary>
public static class StartupRegistryManager
{
    public const string RunKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
    public const string ValueName = "OpenIsland";

    /// <summary>Returns true if the Open Island startup registry entry currently exists and is non-empty.</summary>
    public static bool IsEnabled() => IsEnabled(RunKeyPath, ValueName);

    /// <summary>Returns the currently registered startup command string, or null if not set.</summary>
    public static string? GetRegisteredCommand() => GetRegisteredCommand(RunKeyPath, ValueName);

    /// <summary>
    /// Enables or disables launching Open Island at Windows sign-in.
    /// Wraps the executable path in double quotes when enabling and deletes the value when disabling.
    /// </summary>
    public static (bool Ok, string? Error) Apply(bool enabled, string? executablePath = null) =>
        Apply(enabled, executablePath, RunKeyPath, ValueName);

    /// <summary>Resolves the executable path to register for Windows startup.</summary>
    public static string? ResolveExecutablePath(string? executablePath = null)
    {
        if (!string.IsNullOrWhiteSpace(executablePath))
        {
            var trimmed = executablePath.Trim().Trim('"');
            return string.IsNullOrWhiteSpace(trimmed) ? null : trimmed;
        }

        string? candidate = Environment.ProcessPath;
        if (string.IsNullOrWhiteSpace(candidate))
        {
            try
            {
                candidate = Process.GetCurrentProcess().MainModule?.FileName;
            }
            catch
            {
                candidate = null;
            }
        }

        // If launched via `dotnet OpenIsland.dll`, prefer a sibling OpenIsland.exe apphost if present.
        if (!string.IsNullOrWhiteSpace(candidate) &&
            string.Equals(Path.GetFileName(candidate), "dotnet.exe", StringComparison.OrdinalIgnoreCase))
        {
            var siblingExe = Path.Combine(AppContext.BaseDirectory, "OpenIsland.exe");
            if (File.Exists(siblingExe))
            {
                return siblingExe;
            }
        }

        if (!string.IsNullOrWhiteSpace(candidate))
        {
            return candidate;
        }

        var fallbackExe = Path.Combine(AppContext.BaseDirectory, "OpenIsland.exe");
        return File.Exists(fallbackExe) ? fallbackExe : null;
    }

    internal static bool IsEnabled(string runKeyPath, string valueName)
    {
        return !string.IsNullOrWhiteSpace(GetRegisteredCommand(runKeyPath, valueName));
    }

    internal static string? GetRegisteredCommand(string runKeyPath, string valueName)
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(runKeyPath, writable: false);
            return key?.GetValue(valueName) as string;
        }
        catch (Exception ex)
        {
            AppLog.Warn($"failed to read startup registry value '{valueName}': {ex.Message}");
            return null;
        }
    }

    internal static (bool Ok, string? Error) Apply(
        bool enabled,
        string? executablePath,
        string runKeyPath,
        string valueName)
    {
        try
        {
            if (enabled)
            {
                var resolved = ResolveExecutablePath(executablePath);
                if (string.IsNullOrWhiteSpace(resolved))
                {
                    const string err = "Unable to resolve Open Island executable path for startup registration.";
                    AppLog.Warn(err);
                    return (false, err);
                }

                var command = $"\"{resolved}\"";
                using var key = Registry.CurrentUser.CreateSubKey(runKeyPath, writable: true);
                if (key is null)
                {
                    const string err = "Unable to open HKCU startup registry key for writing.";
                    AppLog.Warn(err);
                    return (false, err);
                }

                key.SetValue(valueName, command, RegistryValueKind.String);
                AppLog.Info($"startup registry enabled: {valueName}={command}");
                return (true, null);
            }
            else
            {
                using var key = Registry.CurrentUser.OpenSubKey(runKeyPath, writable: true);
                if (key is not null)
                {
                    key.DeleteValue(valueName, throwOnMissingValue: false);
                }

                AppLog.Info($"startup registry disabled: {valueName}");
                return (true, null);
            }
        }
        catch (Exception ex)
        {
            AppLog.Error($"failed to update startup registry (enabled={enabled})", ex);
            return (false, ex.Message);
        }
    }
}
