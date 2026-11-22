# Changelog

本文件记录 Open Island Windows 11 版的所有重要变更。每个版本的内容会自动同步到对应的 GitHub Release 页面以及应用内的更新窗口。

All notable changes to Open Island for Windows 11 are documented here. Each version section is published automatically as the GitHub Release notes and shown in the in-app update window.

## [0.1.8] - 2026-11-22

### ✨ 全平台交互选项与审批完整接入 / All-Platform Options & Approvals

- **Google Antigravity 全部待选 / 待审批交互完整支持**：
  - 除 `/grill-me`（`ask_question`）外，新增识别 **沙箱外命令审批**（`run_command` `BypassSandbox: true` / `SafeToAutoRun: false`，展示「允许一次 / 始终允许 / 拒绝」）、**计划 / 产物审阅确认**（`write_file` / `edit_file` `ArtifactMetadata.RequestFeedback: true`，展示「继续执行 (Proceed) / 提出修改意见」）、**`notify_user`（`BlockedOnUser: true`）** 以及任何携带 `options` / `choices` / `questions` 参数或处于 `WAITING / PENDING / APPROVAL` 状态的工具调用；在客户端完成操作后自动消隐。
  - **Complete Google Antigravity interactive coverage**: in addition to `/grill-me` (`ask_question`), now detects sandbox-bypass command approvals (`BypassSandbox: true` / `SafeToAutoRun: false`), plan/artifact feedback prompts (`RequestFeedback: true` / Proceed), `notify_user` (`BlockedOnUser: true`), and any tool exposing `options`/`choices`/`questions` or waiting states, auto-clearing once resolved.
- **全平台（17 个 Agent）选项接入与响应链路修复**：
  - **Claude Code & 全部 Claude 分支（Qoder / Qwen / Factory / CodeBuddy / Kimi）**：补齐阻塞型 `QuestionRequest` 标准输出指令编码（`DirectiveEncoder.EncodeQuestionAnswer` + `BridgeServer.ResolveQuestion`），同时支持本地 `.jsonl` 日志离线发现未回答的 `AskUserQuestion` 与 `ExitPlanMode` 计划确认卡片。
  - **Codex CLI & Desktop**：`CodexAppServerClient` 接入 `item/commandExecution/requestApproval`、`item/fileChange/requestApproval`、`item/tool/requestUserInput` 实时 JSON-RPC 审批与提问选项；`hooks.json` 补全 `PreToolUse` / `PostToolUse`；本地 `rollout-*.jsonl` 支持发现未回答的 `request_user_input` 选项。
  - **DeepSeek Harness（DSH）**：解析本地 `approvalState` 中的待审批命令与待选问题及自定义选项列表，直接呈现于灵动岛。
  - **OpenCode / Hermes / Gemini CLI / Cursor / Pi / OMP**：OpenCode 插件新增 `permission.asked` / `permission.replied` / `question.asked` / `question.replied` 事件转发；Gemini CLI 补全 `BeforeTool` / `AfterTool` 钩子；统一解析 `choices` / `availableDecisions` / `permission_suggestions` / `actions` / `buttons` 等多平台选项字段。
  - **Unified cross-platform option ingestion & responses**: fixed blocking question `StdoutDirective` encoding for Claude/Codex/forks, added Codex app-server & rollout interactive approval/user-input support, Claude local transcript `AskUserQuestion`/`ExitPlanMode` discovery, DeepSeek Harness `approvalState` options, OpenCode permission/question plugin events, Gemini `BeforeTool`/`AfterTool` hooks, and Hermes `choices` parsing.
- **灵动岛选项与审批卡片升级**：
  - 审批卡片支持完整展示自定义可选列表，并自动区分「阻塞型原地审批」与「观察型一键切回处理 + 忽略」；
  - 阻塞型问题卡片新增 **多选（Multi-select）与多题合并勾选**（选中项显示 `✓` 高亮，点击「提交所选」一次性回传）。
  - **Upgraded Island cards**: approval cards now display platform-provided decision options with blocking vs. observational handling, and blocking question cards support multi-select and multi-question batch submission.

## [0.1.7] - 2026-11-22

### 🎨 界面优化 / UI Improvements

- **优化设置窗口与弹窗阴影在白色/浅色背景下的视觉表现**：
  - 将设置界面、Agent 接入引导窗口、更新窗口及下拉菜单的阴影由原先偏右下且过重的黑边（默认 `315°` 方向、被窗口边界裁切）调整为正下方居中的柔和阴影，在浅色背景下左右对称且无硬切边。
  - 将阴影拆分为独立底层渲染，避免整窗套用模糊滤镜，提升文字 ClearType 清晰度与交互流畅度。
  - **Polished window and dropdown shadows on light/white backgrounds**: replaced the heavy bottom-right clipped shadows across Settings, Agent Connect, Update windows, and ComboBox popups with centered, subtle, clip-free shadows rendered on a separate backdrop layer for crisper ClearType text and smoother animations.

## [0.1.6] - 2026-11-22

### 🐛 修复 / Fixes

- **修复 Google Antigravity `/grill-me`（`ask_question`）选项无法在灵动岛显示的问题**：
  - 兼容解析 Antigravity 对话日志与 Hook 中 JSON 字符串编码的 `ask_question` 问题数组及 `is_multi_select` 单选 / 多选标识，并在紧凑日志截断时自动回退读取完整日志。
  - 修复会话被 Antigravity 观察型 Hook 接管后屏蔽日志中待选问题状态的问题，触发提问时灵动岛会自动滑出展开并播放提示音；在 Antigravity 中完成选择后自动消隐。
  - 升级灵动岛问题卡片：收起态提示「待选择」数量，展开态完整展示所有问题与自动换行的编号选项（高亮「推荐」项），点击任意选项或「前往 Antigravity 选择」按钮可一键切回 Antigravity 完成勾选，并支持「忽略」暂藏提醒。
  - **Fixed Google Antigravity `/grill-me` (`ask_question`) selectable options not showing on the Dynamic Island**: parses string-encoded `ask_question` payloads (`is_multi_select`, single/multi-question lists), syncs transcript-discovered pending questions even when observational hooks are active, upgrades the island question card with wrapped numbered options, Recommended badges, and one-click jump back to Antigravity, and clears automatically once answered.
- 修复等待回复状态下会话列表行尾部出现多余分隔符 `·` 的显示细节。
  Fixed a stray trailing separator dot in the session row subtitle while waiting for an answer.

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

[0.1.8]: https://github.com/zhengbaiyang/open-vibe-island-win11/compare/v0.1.7...v0.1.8
[0.1.7]: https://github.com/zhengbaiyang/open-vibe-island-win11/compare/v0.1.6...v0.1.7
[0.1.6]: https://github.com/zhengbaiyang/open-vibe-island-win11/compare/v0.1.5...v0.1.6
[0.1.5]: https://github.com/zhengbaiyang/open-vibe-island-win11/compare/v0.1.4...v0.1.5
[0.1.4]: https://github.com/zhengbaiyang/open-vibe-island-win11/releases/tag/v0.1.4
