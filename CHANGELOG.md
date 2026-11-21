# Changelog

本文件记录 Open Island Windows 11 版的所有重要变更。每个版本的内容会自动同步到对应的 GitHub Release 页面以及应用内的更新窗口。

All notable changes to Open Island for Windows 11 are documented here. Each version section is published automatically as the GitHub Release notes and shown in the in-app update window.

## [0.1.5] - 2026-11-22

### ✨ 新功能 / New Features

- **应用内自动更新**：启动时自动检查 GitHub Releases 最新正式版，强制校验 `SHA256SUMS.txt`；自动识别安装版 / 便携版 / 开发构建，支持安装版静默覆盖升级与便携版原地更新。可在「设置 → 关于」手动检查更新，「设置 → 通用」开关自动检查，托盘菜单也提供「检查更新」。
  **In-app auto-update**: checks GitHub Releases on startup with mandatory `SHA256SUMS.txt` verification; detects Installed / Portable / Dev builds and upgrades them in place. Manual check in Settings → About, toggle in Settings → General, and a tray menu entry.
- **支持 Google Antigravity（桌面版 / IDE / CLI）**：通过本地对话日志与 `~/.gemini/config/hooks.json` 观察型 Hook 双通道识别 Antigravity 会话，并可一键聚焦对应窗口。
  **Google Antigravity support (Desktop / IDE / CLI)**: sessions are discovered from local transcripts plus an observational hook in `~/.gemini/config/hooks.json`.
- **本机 Agent 自动识别与确认接入**：启动时自动探测本机已安装但尚未接入的 AI 编程 Agent，弹出确认窗口，经你勾选同意后才写入配置（自动保留并备份原有配置）；「Agent 接入」页新增「扫描并接入本机 Agent」按钮。
  **Auto-detect local agents**: installed-but-unconnected agents are detected on startup and connected only after you confirm (existing configs are preserved and backed up).
- **统一用量监控**：用量页统一覆盖 Claude Code、Codex、Google Antigravity 与 **DeepSeek Harness**。
  - 未接入的 Agent 显示「未接入」状态，并提供「去接入」快捷按钮；
  - 已接入的 Agent 统一展示：**剩余额度**（5 小时 / 7 天，DeepSeek Harness 不显示）、**已使用模型 Token 数**（5 小时 / 7 天，含输入 / 缓存 / 输出明细）、**近 7 天模型使用分布**（各模型 Token 与占比）。
  **Unified usage monitoring** for Claude Code, Codex, Google Antigravity and **DeepSeek Harness**: `Not Connected` state with a quick Connect button; connected agents show remaining quota (5h / 7d, omitted for DSH), used model tokens (5h / 7d) and a 7-day model distribution.
- **全新高保真提示音**：用内置 48 kHz / 16-bit 立体声合成引擎替换原先沉闷的 Windows 系统音效，提供 9 套更清晰、更悦耳的提示音方案（晶莹灵动、极光和弦、清脆木琴、空灵风铃等），审批 / 提问与完成使用不同节奏，可在「设置 → 通用与音效」试听。
  **New hi-fi notification sounds**: a built-in 48 kHz / 16-bit stereo chime synthesizer replaces the old Windows system sounds, with 9 clearer schemes and distinct urgent vs. completion cadences.
- **开机自启真正生效**：「开机自动启动」开关现在会同步写入 / 移除 Windows 启动项。
  **Launch at startup** now actually registers / removes the Windows startup entry.

### 🐛 修复 / Fixes

- 修复设置界面开关（ToggleSwitch）与分段按钮动画不随选中状态变化的问题。
  Fixed Settings toggle switches and segmented controls not animating with their checked state.
- 修复自动更新重启前未恢复 TMP / TEMP 环境变量、更新检查标志竞态以及错误提示图标显示问题。
  Fixed TMP / TEMP not being restored before relaunching after an update, an update-check flag race, and the error toast icon.

### 🔧 其他 / Other

- 版本号统一由 `Directory.Build.props` 管理，安装器、SmartScreen 助手与发布产物自动同步版本。
  Single-source versioning via `Directory.Build.props`; installer, SmartScreen helper and release assets stay in sync.
- 新增 GitHub Actions 自动发布流程：推送 `v*` 标签即自动构建安装包、便携版并生成 `SHA256SUMS.txt`。
  Added a GitHub Actions release workflow that builds the installer, portable ZIP and `SHA256SUMS.txt` on every `v*` tag.
- 优化中英文 README 介绍页。
  Polished the Chinese and English README pages.

## [0.1.4] - 2026-11-20

- Windows 11 原生移植首个公开版本：灵动岛实时多 Agent 会话、审批 / 提问 / 完成交互、一键跳回终端、空闲自动隐藏、本地额度监控、中英双语与双主题。
  First public Windows 11 port: live multi-agent island, approvals / questions / completions, jump back to terminal, idle auto-hide, local quota monitoring, bilingual UI and two themes.

[0.1.5]: https://github.com/zhengbaiyang/open-vibe-island-win11/compare/v0.1.4...v0.1.5
[0.1.4]: https://github.com/zhengbaiyang/open-vibe-island-win11/releases/tag/v0.1.4
