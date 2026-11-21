using OpenIsland.Core.Models;
using OpenIsland.Core.Setup;

namespace OpenIsland.Setup;

internal static class Program
{
    private static int Main(string[] args)
    {
        var manager = new HookInstallManager();

        if (args.Length == 0 || args[0] is "-h" or "--help" or "help")
        {
            PrintUsage();
            return 0;
        }

        var cmd = args[0].Trim();

        // Support macOS OpenIslandSetup camelCase aliases (installKimi, statusGrok, uninstallHermes, etc.)
        if (TryHandleCamelCaseAlias(manager, cmd, out int aliasExit))
        {
            return aliasExit;
        }

        switch (cmd.ToLowerInvariant())
        {
            case "list":
                foreach (var a in AgentRegistry.All)
                {
                    Console.WriteLine($"  {a.CliKey,-12} {a.DisplayName,-18} {a.ConfigPathTemplate}");
                }
                return 0;

            case "status":
                if (args.Length >= 2)
                {
                    var s = AgentRegistry.Parse(args[1]);
                    if (s == AgentSource.Unknown) return UnknownAgent(args[1]);
                    PrintStatus(manager.GetStatus(AgentRegistry.Get(s)));
                    return 0;
                }
                foreach (var st in manager.GetAllStatuses()) PrintStatus(st);
                return 0;

            case "install":
                if (args.Length < 2)
                {
                    // Default matches original OpenIslandSetup install -> Codex
                    return RunInstall(manager, AgentSource.Codex);
                }
                var installSrc = AgentRegistry.Parse(args[1]);
                return installSrc == AgentSource.Unknown ? UnknownAgent(args[1]) : RunInstall(manager, installSrc);

            case "uninstall":
                if (args.Length < 2)
                {
                    return RunUninstall(manager, AgentSource.Codex);
                }
                var uninstallSrc = AgentRegistry.Parse(args[1]);
                return uninstallSrc == AgentSource.Unknown ? UnknownAgent(args[1]) : RunUninstall(manager, uninstallSrc);

            case "install-all":
            case "installall":
                foreach (var a in AgentRegistry.All)
                {
                    var (ok, msg) = manager.Install(a.Source);
                    Console.WriteLine($"[{(ok ? "OK" : "FAIL")}] {a.DisplayName}: {msg}");
                }
                return 0;

            case "install-claude-statusline":
                var (slOk, slMsg) = manager.InstallClaudeStatusLine();
                Console.WriteLine($"[{(slOk ? "OK" : "FAIL")}] {slMsg}");
                return slOk ? 0 : 1;

            case "repair-codex":
            case "fix-codex":
                var repaired = manager.RepairLegacyCodexHooksConfig();
                Console.WriteLine(repaired
                    ? "[OK] 已删除 ~/.codex/config.toml 中会导致 ChatGPT/Codex 无法启动的旧版 Hook 配置块"
                    : "[OK] ~/.codex/config.toml 中没有旧版 Open Island 配置块，无需修复");
                return 0;

            default:
                Console.Error.WriteLine($"Unknown command: {cmd}");
                PrintUsage();
                return 1;
        }
    }

    private static bool TryHandleCamelCaseAlias(HookInstallManager manager, string cmd, out int exitCode)
    {
        exitCode = 0;
        string[] prefixes = ["install", "uninstall", "status"];
        foreach (var prefix in prefixes)
        {
            if (cmd.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) && cmd.Length > prefix.Length)
            {
                var suffix = cmd[prefix.Length..];
                var src = AgentRegistry.Parse(suffix);
                if (src != AgentSource.Unknown)
                {
                    if (prefix == "install") exitCode = RunInstall(manager, src);
                    else if (prefix == "uninstall") exitCode = RunUninstall(manager, src);
                    else PrintStatus(manager.GetStatus(AgentRegistry.Get(src)));
                    return true;
                }
            }
        }
        return false;
    }

    private static int RunInstall(HookInstallManager manager, AgentSource src)
    {
        var (ok, msg) = manager.Install(src);
        Console.WriteLine($"[{(ok ? "OK" : "FAIL")}] {msg}");
        return ok ? 0 : 1;
    }

    private static int RunUninstall(HookInstallManager manager, AgentSource src)
    {
        var (ok, msg) = manager.Uninstall(src);
        Console.WriteLine($"[{(ok ? "OK" : "FAIL")}] {msg}");
        return ok ? 0 : 1;
    }

    private static void PrintStatus(AgentInstallState st)
    {
        var detectedTag = st.IsDetectedOnMachine && st.Status != HookInstallStatus.Installed ? " [Detected]" : "";
        Console.WriteLine($"  {st.Agent.DisplayName,-18} [{st.Status,-12}]{detectedTag} {st.ConfigPath} ({st.Detail})");
    }

    private static int UnknownAgent(string name)
    {
        Console.Error.WriteLine($"Unknown agent '{name}'. Run 'OpenIslandSetup list' to see supported agents.");
        return 1;
    }

    private static void PrintUsage()
    {
        Console.WriteLine("OpenIslandSetup (Windows 11) — Manage AI coding agent hooks & extensions");
        Console.WriteLine();
        Console.WriteLine("Usage:");
        Console.WriteLine("  OpenIslandSetup list                      List all supported agents");
        Console.WriteLine("  OpenIslandSetup status [agent]            Check hook installation status");
        Console.WriteLine("  OpenIslandSetup install <agent>           Install managed hooks/extension for an agent");
        Console.WriteLine("  OpenIslandSetup uninstall <agent>         Uninstall managed hooks (preserves user hooks)");
        Console.WriteLine("  OpenIslandSetup install-all               Install hooks for all supported agents");
        Console.WriteLine("  OpenIslandSetup install-claude-statusline Install Claude 5h/7d usage statusLine bridge");
        Console.WriteLine("  OpenIslandSetup repair-codex              Remove legacy invalid Open Island block from ~/.codex/config.toml");
    }
}
