<div align="center">

# Open Island — Windows 11 Port

**A Dynamic Island control center for AI coding agents on Windows 11**

<sub>
  Native <b>Windows 11 port</b> of <a href="https://github.com/Octane0411/open-vibe-island">Octane0411/open-vibe-island</a>, re-implementing its interaction model and visual language with .NET 8 and WPF.
</sub>

<p>
  <a href="https://github.com/zhengbaiyang/open-vibe-island-win11/releases/latest">
    <img src="https://img.shields.io/github/v/release/zhengbaiyang/open-vibe-island-win11?style=flat-square&color=6EA7FF&label=Release" alt="Latest Release"/>
  </a>
  <img src="https://img.shields.io/badge/Platform-Windows%2010%20%2F%2011%20x64-1F2937?style=flat-square" alt="Windows 10/11 x64"/>
  <img src="https://img.shields.io/badge/.NET-8%20%C2%B7%20WPF-512BD4?style=flat-square" alt=".NET 8 WPF"/>
  <a href="LICENSE">
    <img src="https://img.shields.io/badge/License-GPL--3.0-6FB982?style=flat-square" alt="GPL-3.0 License"/>
  </a>
</p>

<p>
  <a href="README.md">简体中文</a> · <b>English</b>
  &nbsp;&nbsp;|&nbsp;&nbsp;
  <a href="#features">Features</a> ·
  <a href="#screenshots">Screenshots</a> ·
  <a href="#installation">Installation</a> ·
  <a href="#supported-agents">Supported Agents</a> ·
  <a href="#build-from-source">Build from Source</a>
</p>

<br/>

<img src="docs/screenshots/island-demo.gif" alt="Open Island demo: hover to expand, collapse and auto-hide" width="760"/>

<p>
  <img src="docs/screenshots/island-compact.png" alt="Compact island" width="380"/>
  <img src="docs/screenshots/island-expanded.png" alt="Expanded island" width="380"/>
</p>

</div>

---

## Why Open Island?

When running multiple AI coding agents across terminals, IDEs, and desktop apps, it is easy to miss **command approval prompts, follow-up questions, or finished turns**.

Open Island brings live agent activity into a lightweight Dynamic Island docked at your screen edge:

- **Glanceable status**: see who is running, which tool is executing, and the latest assistant message;
- **Never miss a prompt**: automatically slides out and expands with optional sound alerts when an agent needs approval or an answer;
- **Instant context switching**: jump straight back into the exact terminal session or desktop thread;
- **Local-first, zero telemetry**: all session state, preferences, and usage caches stay strictly on your machine.

> [!NOTE]
> This project is not affiliated with OpenAI, Anthropic, Google, or any agent vendor. All product names and trademarks belong to their respective owners.

---

## Features

| Capability | Description |
| --- | --- |
| **Live multi-agent sessions** | Supports 16+ popular AI coding agents (including **Google Antigravity Desktop / IDE / CLI**), plus zero-config **DeepSeek Harness (DSH)** discovery. |
| **Auto-detect & confirm onboarding** | Automatically detects installed-but-unconnected agents on startup and asks for your confirmation before writing managed hooks (with automatic config backup). |
| **Approvals, questions & completions** | Slides out and expands when attention is required, with inline responses and customizable Windows notification sounds. |
| **Click to jump back** | Opens the exact Codex thread via `codex://threads/<id>` (or `codex resume`), runs `claude --resume` for Claude, or focuses Antigravity and DSH windows. |
| **Idle auto-hide** | Tucks neatly into the screen edge after inactivity and slides back when your pointer reaches the edge; never hides with pending approvals. |
| **Local usage monitoring** | Reads Claude Code & Codex 5-hour / 7-day quota windows plus **Google Antigravity** local 5-hour / 7-day token usage and model breakdown. |
| **Session de-duplication & safe hooks** | Coalesces recurring Codex scheduled tasks and subagent threads; uses official `hooks.json` without corrupting `config.toml`. |
| **In-app auto-updates** | Checks GitHub Releases on startup, enforces strict `SHA256SUMS.txt` verification, and upgrades both installed and portable builds smoothly. |
| **Native polish & customization** | Pure Black / Graphite themes, top/bottom docking, multi-monitor picker, Chinese/English UI, Windows startup sync, and `Ctrl+Alt+I` toggle. |

---

## Screenshots

| Island · Approvals & Multi-Agent Sessions | Settings · General & Sound |
| :---: | :---: |
| <img src="docs/screenshots/island-expanded.png" width="420" alt="Expanded island"/> | <img src="docs/screenshots/settings-general.png" width="420" alt="Settings - General & Sound"/> |

| Settings · Appearance & Live Preview |
| :---: |
| <img src="docs/screenshots/settings-appearance.png" width="760" alt="Settings - Appearance"/> |

---

## Supported Agents

<details open>
<summary><b>17 supported AI coding agents (click to expand / collapse)</b></summary>

- **Managed Hooks / Plugins (auto-detected on startup with confirmation prompt, or one-click Install & Repair in Settings → Agents)**:
  `Claude Code` · `Codex` · `Google Antigravity (Desktop / IDE / CLI)` · `OpenCode` · `Cursor` · `Gemini CLI` · `Kimi CLI` · `Grok Build` · `Hermes Agent` · `Pi` · `Oh My Pi (OMP)` · `Qoder` · `Qwen Code` · `Factory` · `CodeBuddy` · `ZCode Desktop`
- **Zero-config Auto-discovery**:
  `Google Antigravity` (supports both local `transcript.jsonl` discovery and `~/.gemini/config/hooks.json` real-time push) · `DeepSeek Harness (DSH)` (no hook required; reads local session caches and focuses the window on click)

</details>

---

## Installation

**Requirements**: Windows 10 / Windows 11 (x64). Both installer and portable builds are self-contained — no pre-installed .NET runtime needed.

1. Download from [GitHub Releases](https://github.com/zhengbaiyang/open-vibe-island-win11/releases/latest):
   - **Installer (recommended)**: `OpenIsland-Setup-<version>-win-x64.exe` (per-user install, no admin privileges required)
   - **Portable**: `OpenIsland-<version>-portable-win-x64.zip` (extract and run)
2. Open **Settings → Agents** and click **Install / Repair** for the agents you use.
   - If Codex prompts to trust hooks, run `/hooks` once inside Codex to confirm.

### In-App Auto-Updates

- Open Island checks [GitHub Releases](https://github.com/zhengbaiyang/open-vibe-island-win11/releases) automatically on startup for new stable releases and shows release notes with a live download progress bar.
- Every download is **strictly verified against `SHA256SUMS.txt` (SHA-256)** before execution — missing or mismatched checksums are rejected.
- **Installed builds** perform a silent in-place upgrade and relaunch automatically; **portable builds** verify, stage, update files in place, and relaunch.
- Toggle **Auto-check for updates** anytime in **Settings → General**, or trigger a manual check from **Settings → About** or the tray menu (**Check for updates**).

<details>
<summary><b>Seeing a Windows SmartScreen warning or Temp Error 5?</b></summary>

1. **Windows SmartScreen ("Windows protected your PC")**:
   - Releases are currently unsigned. Right-click the downloaded installer → **Properties** → check **Unblock** → **OK**, then run it;
   - Or download `OpenIsland-<version>-smartscreen-helper.zip` from the same release and run `Unblock-and-Install.cmd`.
2. **Installer error "Unable to create directory …\Temp\is-*.tmp (Error 5: Access is denied)"**:
   - Your per-user temp directory has restricted permissions. Use `Unblock-and-Install.cmd` from the helper zip (it redirects the installer temp folder), or use the portable ZIP.

</details>

---

## Build from Source

**Prerequisites**: [.NET 8 SDK](https://dotnet.microsoft.com/download/dotnet/8.0)

```powershell
git clone https://github.com/zhengbaiyang/open-vibe-island-win11.git
cd open-vibe-island-win11

# Development build (outputs to dist\win-x64)
.\scripts\build.ps1

# Run
.\dist\win-x64\OpenIsland.exe

# Check / install hooks via CLI
.\dist\win-x64\OpenIslandSetup.exe status
.\dist\win-x64\OpenIslandSetup.exe install claude
.\dist\win-x64\OpenIslandSetup.exe install codex
```

---

## Architecture

```text
src/
  OpenIsland.App/       WPF island UI, settings window, update dialog, and tray icon
  OpenIsland.Core/      session reducer, hook protocol, discovery, bridge, and auto-updater
  OpenIsland.Hooks/     agent hook CLI receiver
  OpenIsland.Setup/     OpenIslandSetup CLI (install / status / uninstall hooks)
plugins/                OpenCode / Pi / Oh My Pi extensions
installer/              Inno Setup script and icons
scripts/                local build and packaging scripts
.github/workflows/      GitHub Actions automated release workflow
docs/screenshots/       demo GIF and UI screenshots
```

> Agent events arrive over the local named pipe `\\.\pipe\open-island-bridge`, and the UI renders from a single `SessionStateReducer`.

---

## Roadmap

- Customizable display widgets for the island (media controls, date, weather, etc.).

---

## Credits & License

- Upstream inspiration & design: [Octane0411/open-vibe-island](https://github.com/Octane0411/open-vibe-island) (macOS, SwiftUI)
- Licensed under **GNU GPL-3.0** — see [LICENSE](LICENSE).
- Thanks to all AI coding agent maintainers and the open-source community.

---

<p align="center"><sub>If this project helps you, a ⭐ Star is always appreciated.</sub></p>
