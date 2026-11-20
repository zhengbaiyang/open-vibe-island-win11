using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using OpenIsland.Core.Models;

namespace OpenIsland.Core.Setup;

public enum HookInstallStatus
{
    NotInstalled,
    Partial,
    Installed
}

public sealed record AgentInstallState(
    AgentMetadata Agent,
    HookInstallStatus Status,
    string ConfigPath,
    string Detail);

/// <summary>
/// Manages managed hook & extension installation, status checks, and clean uninstallation
/// for all 15 supported agents on Windows 11 while preserving user-authored entries.
/// </summary>
public sealed class HookInstallManager
{
    private const string ManagedMarker = "OpenIslandHooks";
    private const string OpenCodePluginFileName = "open-island.js";
    private readonly string _pluginsSourceDir;

    public HookInstallManager(string? pluginsSourceDir = null)
    {
        _pluginsSourceDir = pluginsSourceDir
                            ?? Path.Combine(AppContext.BaseDirectory, "plugins");
    }

    // ---- OpenCode plugin locations ------------------------------------------------
    private static string OpenCodeConfigDir => AgentRegistry.ExpandHomePath("~/.config/opencode");
    private static string OpenCodePluginsDir => Path.Combine(OpenCodeConfigDir, "plugins");
    private static string OpenCodePluginFile => Path.Combine(OpenCodePluginsDir, OpenCodePluginFileName);
    private static string OpenCodeConfigFile => Path.Combine(OpenCodeConfigDir, "config.json");

    /// <summary>
    /// OpenCode only loads plugins that are registered in <c>~/.config/opencode/config.json</c>
    /// (<c>"plugin": ["file:///…/open-island.js"]</c>). Copying the JS file alone is not enough.
    /// </summary>
    private static string OpenCodePluginReference()
        => "file://" + OpenCodePluginFile.Replace('\\', '/');

    // ---- Codex hooks -----------------------------------------------------------------
    // Current Codex (CLI, IDE extension and the ChatGPT/Codex App) reads user hooks from
    // ~/.codex/hooks.json in the same JSON schema as Claude Code, or from inline
    // [[hooks.<Event>]] tables in config.toml. It must never see the legacy
    // "[features] hooks = true + [[hooks]] event=..." block: config.toml is also used by
    // the ChatGPT App, and that invalid TOML/Toml-schema made the app fail to start.
    private const string CodexLegacyBegin = "# BEGIN OPEN-ISLAND MANAGED HOOKS";
    private const string CodexLegacyEnd = "# END OPEN-ISLAND MANAGED HOOKS";

    private static string CodexHomeDir => AgentRegistry.ExpandHomePath("~/.codex");
    private static string CodexHooksJsonFile => Path.Combine(CodexHomeDir, "hooks.json");
    private static string CodexConfigTomlFile => Path.Combine(CodexHomeDir, "config.toml");

    private static readonly (string Event, int Timeout)[] CodexManagedEvents =
    [
        ("SessionStart", 45),
        ("UserPromptSubmit", 45),
        ("PermissionRequest", 3600),
        ("Stop", 45)
    ];

    private static bool HasLegacyCodexHooksBlock()
    {
        try
        {
            return File.Exists(CodexConfigTomlFile)
                   && File.ReadAllText(CodexConfigTomlFile).Contains(CodexLegacyBegin, StringComparison.Ordinal);
        }
        catch
        {
            return false;
        }
    }

    private static bool IsCodexHooksInstalled()
    {
        try
        {
            return File.Exists(CodexHooksJsonFile)
                   && File.ReadAllText(CodexHooksJsonFile).Contains(ManagedMarker, StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// Removes the invalid legacy "[features]/[[hooks]]" block that older Open Island
    /// builds appended to <c>~/.codex/config.toml</c>. Leaving it in place makes the
    /// ChatGPT/Codex App unable to start, so this runs automatically on every launch.
    /// Returns true when a block was found and removed.
    /// </summary>
    public bool RepairLegacyCodexHooksConfig()
    {
        try
        {
            if (!File.Exists(CodexConfigTomlFile)) return false;
            var text = File.ReadAllText(CodexConfigTomlFile);
            var start = text.IndexOf(CodexLegacyBegin, StringComparison.Ordinal);
            if (start < 0) return false;
            var end = text.IndexOf(CodexLegacyEnd, start, StringComparison.Ordinal);
            if (end < 0) return false;

            EnsureBackup(CodexConfigTomlFile);
            var cleaned = (text[..start] + text[(end + CodexLegacyEnd.Length)..]).TrimEnd() + Environment.NewLine;
            File.WriteAllText(CodexConfigTomlFile, cleaned, new UTF8Encoding(false));
            OpenIsland.Core.Diagnostics.AppLog.Warn("Removed legacy Open Island hook block from ~/.codex/config.toml");
            return true;
        }
        catch (Exception ex)
        {
            OpenIsland.Core.Diagnostics.AppLog.Error("failed to repair legacy Codex config", ex);
            return false;
        }
    }

    /// <summary>
    /// Copies OpenIslandHooks.exe to %LOCALAPPDATA%\OpenIsland\bin\OpenIslandHooks.exe so moving the app folder does not break hooks.
    /// </summary>
    public string EnsureHooksBinaryInstalled()
    {
        // Prefer the hooks executable that ships next to OpenIsland.exe. The packaged build is
        // self-contained, so this exe resolves its runtime and dependencies from its own folder.
        // Copying only the apphost into %LOCALAPPDATA%\OpenIsland\bin produced a non-runnable
        // executable ("OpenIslandHooks.dll not found"), which silently broke hook delivery.
        var bundled = Path.Combine(AppContext.BaseDirectory, "OpenIslandHooks.exe");
        if (File.Exists(bundled)) return bundled;

        // Unusual layouts (e.g. a bare setup exe without the published hooks): keep the legacy
        // stable per-user copy as a fallback.
        var dest = BridgeConstants.InstalledHooksExePath;
        Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
        return dest;
    }

    /// <summary>
    /// Older builds installed hooks at %LOCALAPPDATA%\OpenIsland\bin\OpenIslandHooks.exe. That
    /// copy is not runnable when the app ships hooks as an apphost plus dlls, so installed hook
    /// configurations must be rewritten to the hooks exe that lives next to the app.
    /// Returns the number of agent configurations migrated.
    /// </summary>
    public int MigrateHooksToBundledBinary()
    {
        try
        {
            var target = EnsureHooksBinaryInstalled();
            var legacy = BridgeConstants.InstalledHooksExePath;
            if (string.Equals(target, legacy, StringComparison.OrdinalIgnoreCase)) return 0;

            var legacyForms =
                new[] { legacy, legacy.Replace("\\", "\\\\"), legacy.Replace('\\', '/') }
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .ToArray();

            var migrated = 0;
            foreach (var agent in AgentRegistry.All)
            {
                if (agent.IsAutoDetected) continue;

                try
                {
                    var configs = new List<string>
                    {
                        AgentRegistry.ExpandHomePath(agent.ConfigPathTemplate)
                    };
                    if (agent.Source == AgentSource.Codex) configs.Add(CodexHooksJsonFile);

                    var referencesLegacyHooks = configs
                        .Where(File.Exists)
                        .Any(path =>
                        {
                            try
                            {
                                var content = File.ReadAllText(path);
                                return legacyForms.Any(form => content.Contains(form, StringComparison.OrdinalIgnoreCase));
                            }
                            catch
                            {
                                return false;
                            }
                        });

                    if (!referencesLegacyHooks) continue;

                    var (ok, message) = Install(agent.Source);
                    if (ok) migrated++;
                    else OpenIsland.Core.Diagnostics.AppLog.Warn(
                        $"hook path migration failed for {agent.Source}: {message}");
                }
                catch (Exception ex)
                {
                    OpenIsland.Core.Diagnostics.AppLog.Warn(
                        $"hook path migration failed for {agent.Source}: {ex.Message}");
                }
            }

            if (migrated > 0)
            {
                OpenIsland.Core.Diagnostics.AppLog.Warn(
                    $"migrated {migrated} agent hook configuration(s) to bundled hooks binary: {target}");
            }
            return migrated;
        }
        catch (Exception ex)
        {
            OpenIsland.Core.Diagnostics.AppLog.Warn($"hook path migration failed: {ex.Message}");
            return 0;
        }
    }

    public List<AgentInstallState> GetAllStatuses() =>
        AgentRegistry.All.Select(GetStatus).ToList();

    public AgentInstallState GetStatus(AgentMetadata agent)
    {
        var configPath = AgentRegistry.ExpandHomePath(agent.ConfigPathTemplate);

        // Agents without a hook system (e.g. DeepSeek Harness) are auto-discovered from
        // their local session storage and never need install/uninstall.
        if (agent.IsAutoDetected)
        {
            var detected = Directory.Exists(configPath);
            return new AgentInstallState(
                agent,
                detected ? HookInstallStatus.Installed : HookInstallStatus.NotInstalled,
                configPath,
                detected ? "自动发现（无需安装 Hook）" : "未检测到本地会话目录");
        }

        // OpenCode is a JS plugin that must be both copied and registered in config.json.
        if (agent.Source == AgentSource.OpenCode)
        {
            var filePresent = File.Exists(OpenCodePluginFile);
            var registered = IsOpenCodePluginRegistered();
            var status = filePresent && registered
                ? HookInstallStatus.Installed
                : filePresent || registered ? HookInstallStatus.Partial : HookInstallStatus.NotInstalled;
            var detail = status switch
            {
                HookInstallStatus.Installed => "Plugin file present and registered in config.json",
                HookInstallStatus.Partial => filePresent
                    ? "Plugin file exists but is not registered in config.json"
                    : "config.json references the plugin but the file is missing",
                _ => "Plugin not installed"
            };
            return new AgentInstallState(agent, status, OpenCodePluginFile, detail);
        }

        // Codex: hooks live in ~/.codex/hooks.json (config.toml stays user-owned).
        if (agent.Source == AgentSource.Codex)
        {
            var legacy = HasLegacyCodexHooksBlock();
            var installed = IsCodexHooksInstalled();
            var status = legacy
                ? HookInstallStatus.Partial
                : installed ? HookInstallStatus.Installed : HookInstallStatus.NotInstalled;
            var detail = legacy
                ? "检测到旧版 config.toml 写法（会导致 ChatGPT/Codex 无法打开），点击“修复 / 重装”自动迁移"
                : installed
                    ? "hooks.json 已注册（若 Codex 提示，请在 /hooks 中信任一次）"
                    : "未安装";
            return new AgentInstallState(agent, status, CodexHooksJsonFile, detail);
        }

        if (!File.Exists(configPath))
        {
            return new AgentInstallState(agent, HookInstallStatus.NotInstalled, configPath, "Config file not found");
        }

        try
        {
            var text = File.ReadAllText(configPath);
            bool hasMarker = text.Contains(ManagedMarker, StringComparison.OrdinalIgnoreCase)
                             || text.Contains("open-island-bridge", StringComparison.OrdinalIgnoreCase);

            if (!hasMarker)
                return new AgentInstallState(agent, HookInstallStatus.NotInstalled, configPath, "No Open Island entry");

            if (agent.Source == AgentSource.Grok)
            {
                string[] required = ["SessionStart", "SessionEnd", "UserPromptSubmit", "Stop", "StopCancelled", "Notification"];
                bool allPresent = required.All(e => text.Contains(e, StringComparison.Ordinal));
                return new AgentInstallState(agent, allPresent ? HookInstallStatus.Installed : HookInstallStatus.Partial,
                    configPath, allPresent ? "All managed events registered" : "Missing required Grok events");
            }

            return new AgentInstallState(agent, HookInstallStatus.Installed, configPath, "Managed hook/plugin active");
        }
        catch (Exception ex)
        {
            return new AgentInstallState(agent, HookInstallStatus.NotInstalled, configPath, ex.Message);
        }
    }

    public (bool Ok, string Message) Install(AgentSource source)
    {
        var agent = AgentRegistry.Get(source);
        if (agent.IsAutoDetected)
        {
            return (true, $"{agent.DisplayName} 会话由本地缓存自动发现，无需安装 Hook");
        }

        var hooksExe = EnsureHooksBinaryInstalled();
        var configPath = AgentRegistry.ExpandHomePath(agent.ConfigPathTemplate);

        try
        {
            if (source == AgentSource.Codex)
            {
                return InstallCodexHooks(hooksExe);
            }

            Directory.CreateDirectory(Path.GetDirectoryName(configPath)!);

            return source switch
            {
                AgentSource.Claude or AgentSource.Qoder or AgentSource.Qwen
                    or AgentSource.Factory or AgentSource.CodeBuddy
                    => InstallClaudeStyleJson(configPath, hooksExe, agent.CliKey),

                AgentSource.Cursor => InstallCursorJson(configPath, hooksExe),
                AgentSource.Gemini => InstallGeminiJson(configPath, hooksExe),
                AgentSource.Kimi => InstallKimiToml(configPath, hooksExe),
                AgentSource.Grok => InstallGrokJson(configPath, hooksExe),
                AgentSource.ZCode => InstallZCodeJson(configPath, hooksExe),
                AgentSource.Hermes => InstallHermesYaml(configPath, hooksExe),
                AgentSource.OpenCode => InstallOpenCodePlugin(),
                AgentSource.Pi => InstallBundledPlugin("pi", "open-island-pi.ts", configPath),
                AgentSource.OhMyPi => InstallBundledPlugin("pi", "open-island-pi.ts", configPath),
                _ => (false, $"Unsupported agent: {source}")
            };
        }
        catch (Exception ex)
        {
            return (false, $"Install failed: {ex.Message}");
        }
    }

    public (bool Ok, string Message) Uninstall(AgentSource source)
    {
        var agent = AgentRegistry.Get(source);
        if (agent.IsAutoDetected)
        {
            return (true, $"{agent.DisplayName} 为自动发现，无需卸载");
        }

        // Codex hooks.json + legacy config.toml block cleanup must run even when
        // config.toml itself is missing.
        if (source == AgentSource.Codex)
        {
            try
            {
                return UninstallCodexHooks();
            }
            catch (Exception ex)
            {
                return (false, $"Uninstall failed: {ex.Message}");
            }
        }

        var configPath = AgentRegistry.ExpandHomePath(agent.ConfigPathTemplate);
        if (!File.Exists(configPath)) return (true, "Already uninstalled");

        try
        {
            if (source == AgentSource.OpenCode)
            {
                return UninstallOpenCodePlugin();
            }

            if (source == AgentSource.Grok)
            {
                return RemoveManagedEntriesFromJsonHooks(configPath);
            }

            if (agent.IsExtensionBased)
            {
                File.Delete(configPath);
                return (true, $"Removed {configPath}");
            }

            if (source is AgentSource.Claude or AgentSource.Qoder or AgentSource.Qwen
                or AgentSource.Factory or AgentSource.CodeBuddy or AgentSource.Gemini)
            {
                return RemoveFromJsonHooksObject(configPath, "hooks");
            }

            if (source == AgentSource.Cursor)
            {
                return RemoveFromJsonHooksObject(configPath, "hooks");
            }

            if (source == AgentSource.ZCode)
            {
                return RemoveFromZCodeJson(configPath);
            }

            if (source is AgentSource.Kimi or AgentSource.Hermes)
            {
                return RemoveManagedBlock(configPath);
            }

            return (false, $"Unsupported uninstall target: {source}");
        }
        catch (Exception ex)
        {
            return (false, $"Uninstall failed: {ex.Message}");
        }
    }

    public (bool Ok, string Message) InstallClaudeStatusLine()
    {
        try
        {
            var scriptDest = AgentRegistry.ExpandHomePath("~/.open-island/bin/open-island-statusline.cmd");
            Directory.CreateDirectory(Path.GetDirectoryName(scriptDest)!);

            var bundled = Path.Combine(_pluginsSourceDir, "claude", "open-island-statusline.cmd");
            if (File.Exists(bundled))
            {
                File.Copy(bundled, scriptDest, overwrite: true);
            }
            else
            {
                File.WriteAllText(scriptDest, "@echo off\r\npowershell -NoProfile -Command \"$in = [Console]::In.ReadToEnd(); if ($in) { Set-Content -Path (Join-Path $env:TEMP 'open-island-rl.json') -Value $in -Encoding UTF8 }\"\r\n");
            }

            var claudeSettings = AgentRegistry.ExpandHomePath("~/.claude/settings.json");
            var root = LoadOrCreateJsonObject(claudeSettings);
            if (root["statusLine"] is JsonObject existing && existing["command"]?.GetValue<string>() is string cmd
                && !cmd.Contains("open-island-statusline", StringComparison.OrdinalIgnoreCase))
            {
                return (false, "Existing custom Claude statusLine detected; refused to overwrite.");
            }

            root["statusLine"] = new JsonObject
            {
                ["type"] = "command",
                ["command"] = $"\"{scriptDest}\""
            };
            WriteJsonObject(claudeSettings, root);
            return (true, $"Installed Claude statusLine bridge at {scriptDest}");
        }
        catch (Exception ex)
        {
            return (false, ex.Message);
        }
    }

    private static (bool, string) InstallClaudeStyleJson(string path, string hooksExe, string cliKey)
    {
        var root = LoadOrCreateJsonObject(path);
        var hooks = root["hooks"] as JsonObject ?? new JsonObject();
        root["hooks"] = hooks;

        var cmd = $"\"{hooksExe}\" --source {cliKey}";
        (string Event, int Timeout)[] events =
        [
            ("SessionStart", 45),
            ("SessionEnd", 45),
            ("UserPromptSubmit", 45),
            ("PreToolUse", 45),
            ("PostToolUse", 45),
            ("PostToolUseFailure", 45),
            ("PermissionRequest", 86400),
            ("PermissionDenied", 45),
            ("Notification", 45),
            ("Stop", 45),
            ("StopFailure", 45),
            ("SubagentStart", 45),
            ("SubagentStop", 45),
            ("PreCompact", 45)
        ];

        foreach (var (ev, timeout) in events)
        {
            var arr = hooks[ev] as JsonArray ?? new JsonArray();
            RemoveManagedEntriesFromArray(arr);
            arr.Add(new JsonObject
            {
                ["matcher"] = "",
                ["hooks"] = new JsonArray
                {
                    new JsonObject
                    {
                        ["type"] = "command",
                        ["command"] = cmd,
                        ["timeout"] = timeout
                    }
                }
            });
            hooks[ev] = arr;
        }

        WriteJsonObject(path, root);
        return (true, $"Installed {cliKey} hooks into {path}");
    }

    private static (bool, string) InstallGeminiJson(string path, string hooksExe)
    {
        var root = LoadOrCreateJsonObject(path);
        var hooks = root["hooks"] as JsonObject ?? new JsonObject();
        root["hooks"] = hooks;

        var cmd = $"\"{hooksExe}\" --source gemini";
        string[] events = ["SessionStart", "BeforeAgent", "AfterAgent", "SessionEnd", "Notification"];

        foreach (var ev in events)
        {
            var arr = hooks[ev] as JsonArray ?? new JsonArray();
            RemoveManagedEntriesFromArray(arr);
            arr.Add(new JsonObject
            {
                ["matcher"] = "",
                ["hooks"] = new JsonArray
                {
                    new JsonObject { ["type"] = "command", ["command"] = cmd }
                }
            });
            hooks[ev] = arr;
        }

        WriteJsonObject(path, root);
        return (true, $"Installed Gemini CLI hooks into {path}");
    }

    private static (bool, string) InstallCursorJson(string path, string hooksExe)
    {
        var root = LoadOrCreateJsonObject(path);
        root["version"] ??= 1;
        var hooks = root["hooks"] as JsonObject ?? new JsonObject();
        root["hooks"] = hooks;

        var cmd = $"\"{hooksExe}\" --source cursor";
        string[] events =
        [
            "beforeSubmitPrompt",
            "beforeShellExecution",
            "beforeMCPExecution",
            "beforeReadFile",
            "afterFileEdit",
            "stop"
        ];

        foreach (var ev in events)
        {
            var arr = hooks[ev] as JsonArray ?? new JsonArray();
            RemoveManagedEntriesFromArray(arr);
            arr.Add(new JsonObject { ["command"] = cmd });
            hooks[ev] = arr;
        }

        WriteJsonObject(path, root);
        return (true, $"Installed Cursor hooks into {path}");
    }

    private static (bool, string) InstallGrokJson(string path, string hooksExe)
    {
        var cmd = $"\"{hooksExe}\" --source grok";
        var root = LoadOrCreateJsonObject(path);
        var hooks = root["hooks"] as JsonObject ?? new JsonObject();
        root["hooks"] = hooks;
        root["managed_by"] ??= "open-island-win11";

        (string Event, string? Matcher)[] events =
        [
            ("SessionStart", null),
            ("SessionEnd", null),
            ("UserPromptSubmit", null),
            ("Stop", null),
            ("StopFailure", null),
            ("StopCancelled", null),
            ("Notification", "*"),
            ("PreToolUse", "*"),
            ("PostToolUse", "*"),
            ("PostToolUseFailure", "*"),
            ("SubagentStart", null),
            ("SubagentStop", null),
            ("PermissionDenied", null),
            ("PreCompact", null),
            ("PostCompact", null)
        ];

        foreach (var (ev, matcher) in events)
        {
            var existing = hooks[ev] as JsonArray ?? new JsonArray();
            RemoveManagedEntriesFromArray(existing);
            var group = new JsonObject
            {
                ["hooks"] = new JsonArray
                {
                    new JsonObject { ["type"] = "command", ["command"] = cmd, ["timeout"] = 45 }
                }
            };
            if (matcher != null) group["matcher"] = matcher;
            existing.Add(group);
            hooks[ev] = existing;
        }

        WriteJsonObject(path, root);
        return (true, $"Installed Grok Build hooks into {path}");
    }

    private static (bool, string) InstallZCodeJson(string path, string hooksExe)
    {
        var root = LoadOrCreateJsonObject(path);
        var hooks = root["hooks"] as JsonObject ?? new JsonObject();
        hooks["enabled"] = true;
        var eventsObj = hooks["events"] as JsonObject ?? new JsonObject();
        hooks["events"] = eventsObj;
        root["hooks"] = hooks;

        var cmd = $"\"{hooksExe}\" --source zcode";
        foreach (var ev in new[] { "SessionStart", "UserPromptSubmit", "Stop" })
        {
            var arr = eventsObj[ev] as JsonArray ?? new JsonArray();
            RemoveManagedEntriesFromArray(arr);
            arr.Add(new JsonObject
            {
                ["hooks"] = new JsonArray
                {
                    new JsonObject { ["type"] = "command", ["command"] = cmd, ["timeout"] = 45 }
                }
            });
            eventsObj[ev] = arr;
        }

        WriteJsonObject(path, root);
        return (true, $"Installed ZCode hooks into {path}");
    }

    // ---------------------------------------------------------------------
    // Codex hooks — official ~/.codex/hooks.json format
    // ---------------------------------------------------------------------
    private (bool, string) InstallCodexHooks(string hooksExe)
    {
        Directory.CreateDirectory(CodexHomeDir);
        var repairedLegacy = RepairLegacyCodexHooksConfig();

        JsonObject root;
        try
        {
            root = LoadOrCreateJsonObject(CodexHooksJsonFile);
        }
        catch
        {
            EnsureBackup(CodexHooksJsonFile);
            root = new JsonObject();
        }

        var hooks = root["hooks"] as JsonObject ?? new JsonObject();
        root["hooks"] = hooks;

        var command = $"\"{hooksExe}\" --source codex";
        foreach (var (evt, timeout) in CodexManagedEvents)
        {
            var groups = hooks[evt] as JsonArray ?? new JsonArray();
            RemoveManagedEntriesFromArray(groups);
            groups.Add(new JsonObject
            {
                ["matcher"] = "",
                ["hooks"] = new JsonArray
                {
                    new JsonObject
                    {
                        ["type"] = "command",
                        ["command"] = command,
                        ["timeout"] = timeout
                    }
                }
            });
            hooks[evt] = groups;
        }

        WriteJsonObject(CodexHooksJsonFile, root);
        var msg = repairedLegacy
            ? $"已修复 Codex 配置并安装 hooks 到 {CodexHooksJsonFile}"
            : $"已安装 Codex hooks 到 {CodexHooksJsonFile}";
        return (true, msg + " · 首次使用请在 Codex 的 /hooks 中确认信任（如提示）");
    }

    private (bool, string) UninstallCodexHooks()
    {
        var repairedLegacy = RepairLegacyCodexHooksConfig();
        if (!File.Exists(CodexHooksJsonFile))
        {
            return (true, repairedLegacy
                ? "已清除旧版 config.toml 中的无效 Hook 配置"
                : "Already uninstalled");
        }

        var root = LoadOrCreateJsonObject(CodexHooksJsonFile);
        if (root["hooks"] is JsonObject hooks)
        {
            foreach (var prop in hooks.ToList())
            {
                if (prop.Value is JsonArray groups) RemoveManagedEntriesFromArray(groups);
            }

            if (hooks.Count == 0) root.Remove("hooks");
        }

        if (root.Count == 0) File.Delete(CodexHooksJsonFile);
        else WriteJsonObject(CodexHooksJsonFile, root);

        return (true, repairedLegacy
            ? "已移除 Codex hooks 并清除旧版 config.toml 配置"
            : $"Removed Codex hooks from {CodexHooksJsonFile}");
    }

    private static (bool, string) InstallKimiToml(string path, string hooksExe)
    {
        var escapedExe = hooksExe.Replace("\\", "\\\\");
        var block = new StringBuilder();
        block.AppendLine("# BEGIN OPEN-ISLAND MANAGED HOOKS");
        foreach (var (ev, timeout) in new[]
        {
            ("SessionStart", 45), ("UserPromptSubmit", 45), ("Stop", 45),
            ("Notification", 45), ("PreToolUse", 45), ("PostToolUse", 45)
        })
        {
            block.AppendLine("[[hooks]]");
            block.AppendLine($"event = \"{ev}\"");
            block.AppendLine($"command = \"\\\"{escapedExe}\\\" --source kimi\"");
            block.AppendLine($"timeout = {timeout}");
            block.AppendLine();
        }
        block.AppendLine("# END OPEN-ISLAND MANAGED HOOKS");

        UpsertManagedBlock(path, block.ToString());
        return (true, $"Installed Kimi CLI hooks into {path}");
    }

    private static (bool, string) InstallHermesYaml(string path, string hooksExe)
    {
        var escaped = hooksExe.Replace("'", "''");
        var block = new StringBuilder();
        block.AppendLine("# BEGIN OPEN-ISLAND MANAGED HOOKS");
        block.AppendLine("hooks:");
        foreach (var ev in new[] { "on_session_start", "post_llm_call", "subagent_stop", "on_session_end", "pre_tool_call", "pre_approval_request" })
        {
            block.AppendLine($"  - event: {ev}");
            block.AppendLine($"    command: '\"{escaped}\" --source hermes'");
        }
        block.AppendLine("# END OPEN-ISLAND MANAGED HOOKS");

        UpsertManagedBlock(path, block.ToString());
        return (true, $"Installed Hermes Agent hooks into {path}");
    }

    private (bool, string) InstallBundledPlugin(string subFolder, string fileName, string destPath)
    {
        var src = Path.Combine(_pluginsSourceDir, subFolder, fileName);
        if (!File.Exists(src))
        {
            return (false, $"Bundled plugin not found at {src}");
        }
        Directory.CreateDirectory(Path.GetDirectoryName(destPath)!);
        File.Copy(src, destPath, overwrite: true);
        return (true, $"Installed plugin to {destPath}");
    }

    // ---------------------------------------------------------------------
    // OpenCode plugin (copy + register in config.json)
    // ---------------------------------------------------------------------

    private (bool, string) InstallOpenCodePlugin()
    {
        var src = Path.Combine(_pluginsSourceDir, "opencode", OpenCodePluginFileName);
        if (!File.Exists(src)) return (false, $"Bundled plugin not found at {src}");

        Directory.CreateDirectory(OpenCodePluginsDir);
        File.Copy(src, OpenCodePluginFile, overwrite: true);
        RegisterOpenCodePlugin();
        return (true, $"Installed OpenCode plugin to {OpenCodePluginFile} and registered it in config.json");
    }

    private (bool, string) UninstallOpenCodePlugin()
    {
        var removedFile = false;
        if (File.Exists(OpenCodePluginFile))
        {
            File.Delete(OpenCodePluginFile);
            removedFile = true;
        }
        var unregistered = UnregisterOpenCodePlugin();
        return (true, removedFile || unregistered
            ? "Removed OpenCode plugin and its config.json registration"
            : "OpenCode plugin was not installed");
    }

    private static bool IsOpenCodePluginRegistered()
    {
        try
        {
            if (!File.Exists(OpenCodeConfigFile)) return false;
            var root = JsonNode.Parse(File.ReadAllText(OpenCodeConfigFile)) as JsonObject;
            if (root?["plugin"] is not JsonArray plugins) return false;
            var reference = OpenCodePluginReference();
            return plugins.Any(p => p is JsonValue v
                && v.TryGetValue<string>(out var s)
                && (string.Equals(s, reference, StringComparison.OrdinalIgnoreCase)
                    || s.EndsWith("/" + OpenCodePluginFileName, StringComparison.OrdinalIgnoreCase)));
        }
        catch
        {
            return false;
        }
    }

    private static void RegisterOpenCodePlugin()
    {
        var root = LoadOrCreateJsonObject(OpenCodeConfigFile);
        var plugins = root["plugin"] as JsonArray ?? new JsonArray();
        var reference = OpenCodePluginReference();

        for (int i = plugins.Count - 1; i >= 0; i--)
        {
            if (plugins[i] is JsonValue v && v.TryGetValue<string>(out var s)
                && (string.Equals(s, reference, StringComparison.OrdinalIgnoreCase)
                    || s.EndsWith("/" + OpenCodePluginFileName, StringComparison.OrdinalIgnoreCase)))
            {
                plugins.RemoveAt(i);
            }
        }
        plugins.Add(JsonValue.Create(reference));
        root["plugin"] = plugins;
        WriteJsonObject(OpenCodeConfigFile, root);
    }

    private static bool UnregisterOpenCodePlugin()
    {
        if (!File.Exists(OpenCodeConfigFile)) return false;
        var root = LoadOrCreateJsonObject(OpenCodeConfigFile);
        if (root["plugin"] is not JsonArray plugins) return false;

        bool changed = false;
        for (int i = plugins.Count - 1; i >= 0; i--)
        {
            if (plugins[i] is JsonValue v && v.TryGetValue<string>(out var s)
                && (string.Equals(s, OpenCodePluginReference(), StringComparison.OrdinalIgnoreCase)
                    || s.EndsWith("/" + OpenCodePluginFileName, StringComparison.OrdinalIgnoreCase)))
            {
                plugins.RemoveAt(i);
                changed = true;
            }
        }
        if (!changed) return false;

        if (plugins.Count == 0) root.Remove("plugin");
        else root["plugin"] = plugins;
        WriteJsonObject(OpenCodeConfigFile, root);
        return true;
    }

    private static (bool, string) RemoveManagedEntriesFromJsonHooks(string path)
    {
        if (!File.Exists(path)) return (true, "Already uninstalled");
        var root = LoadOrCreateJsonObject(path);
        if (root["hooks"] is JsonObject hooks)
        {
            foreach (var prop in hooks.ToList())
            {
                if (prop.Value is JsonArray arr) RemoveManagedEntriesFromArray(arr);
            }
        }
        WriteJsonObject(path, root);
        return (true, $"Removed Open Island hooks from {path}");
    }

    private static (bool, string) RemoveFromJsonHooksObject(string path, string key)
    {
        var root = LoadOrCreateJsonObject(path);
        if (root[key] is JsonObject hooks)
        {
            foreach (var prop in hooks.ToList())
            {
                if (prop.Value is JsonArray arr)
                {
                    RemoveManagedEntriesFromArray(arr);
                }
            }
        }
        WriteJsonObject(path, root);
        return (true, $"Removed Open Island hooks from {path}");
    }

    private static (bool, string) RemoveFromZCodeJson(string path)
    {
        var root = LoadOrCreateJsonObject(path);
        if (root["hooks"] is JsonObject hooks && hooks["events"] is JsonObject eventsObj)
        {
            foreach (var prop in eventsObj.ToList())
            {
                if (prop.Value is JsonArray arr) RemoveManagedEntriesFromArray(arr);
            }
        }
        WriteJsonObject(path, root);
        return (true, $"Removed Open Island hooks from {path}");
    }

    private static void RemoveManagedEntriesFromArray(JsonArray arr)
    {
        for (int i = arr.Count - 1; i >= 0; i--)
        {
            var raw = arr[i]?.ToJsonString() ?? "";
            if (raw.Contains(ManagedMarker, StringComparison.OrdinalIgnoreCase))
            {
                arr.RemoveAt(i);
            }
        }
    }

    private static void UpsertManagedBlock(string path, string block)
    {
        var existing = File.Exists(path) ? File.ReadAllText(path) : "";
        var cleaned = StripManagedBlock(existing).TrimEnd();
        var merged = string.IsNullOrWhiteSpace(cleaned)
            ? block
            : cleaned + Environment.NewLine + Environment.NewLine + block;
        EnsureBackup(path);
        File.WriteAllText(path, merged, new UTF8Encoding(false));
    }

    private static (bool, string) RemoveManagedBlock(string path)
    {
        var existing = File.ReadAllText(path);
        var cleaned = StripManagedBlock(existing);
        EnsureBackup(path);
        File.WriteAllText(path, cleaned, new UTF8Encoding(false));
        return (true, $"Removed Open Island managed block from {path}");
    }

    /// <summary>
    /// Keeps a one-time backup of a user config file before Open Island modifies it
    /// (<c>&lt;file&gt;.open-island-backup</c>), so a bad write can always be recovered.
    /// </summary>
    private static void EnsureBackup(string path)
    {
        try
        {
            if (!File.Exists(path)) return;
            var backup = path + ".open-island-backup";
            if (!File.Exists(backup))
            {
                File.Copy(path, backup, overwrite: false);
            }
        }
        catch
        {
            // Backups are best-effort; never block installation.
        }
    }

    private static string StripManagedBlock(string text)
    {
        const string startTag = "# BEGIN OPEN-ISLAND MANAGED HOOKS";
        const string endTag = "# END OPEN-ISLAND MANAGED HOOKS";
        int start = text.IndexOf(startTag, StringComparison.Ordinal);
        if (start < 0) return text;
        int end = text.IndexOf(endTag, start, StringComparison.Ordinal);
        if (end < 0) return text;
        return (text[..start] + text[(end + endTag.Length)..]).TrimStart('\r', '\n');
    }

    private static JsonObject LoadOrCreateJsonObject(string path)
    {
        if (File.Exists(path))
        {
            var raw = File.ReadAllText(path);
            if (!string.IsNullOrWhiteSpace(raw) && JsonNode.Parse(raw) is JsonObject obj)
                return obj;
        }
        return new JsonObject();
    }

    private static void WriteJsonObject(string path, JsonObject obj)
    {
        var json = obj.ToJsonString(new JsonSerializerOptions { WriteIndented = true });
        EnsureBackup(path);
        File.WriteAllText(path, json, new UTF8Encoding(false));
    }
}
