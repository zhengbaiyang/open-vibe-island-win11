// Open Island (Windows 11) — OpenCode plugin
//
// Streams OpenCode session/tool activity to the Open Island Windows app over a
// local Windows Named Pipe (\\.\pipe\open-island-bridge). Fail-open: if Open
// Island is not running, every call resolves immediately and OpenCode is never
// blocked or slowed down.
//
// Install: copied to ~/.config/opencode/plugins/open-island.js AND registered in
// ~/.config/opencode/config.json under "plugin": ["file:///…/open-island.js"].
import { connect } from "node:net";
import process from "node:process";

// The desktop app supports an optional instance suffix (used by tests / multiple
// portable installs). Keep in sync with BridgeConstants.PipeName in the C# app.
function pipePath() {
  const suffix = (process.env.OPEN_ISLAND_INSTANCE || "").replace(/[^a-zA-Z0-9]/g, "").toLowerCase();
  return "\\\\.\\pipe\\open-island-bridge" + (suffix ? "-" + suffix : "");
}

function skipped() {
  return process.env.OPEN_ISLAND_SKIP_HOOKS === "1" || process.env.VIBE_ISLAND_SKIP === "1";
}

function baseEnvelope(source, rawPayload, expectsDirective = false, timeoutSeconds = 45) {
  return {
    source,
    raw_payload: JSON.stringify(rawPayload),
    cwd: process.cwd(),
    pid: process.pid,
    terminal_app: process.env.WT_SESSION
      ? "Windows Terminal"
      : process.env.TERM_PROGRAM || process.env.TERM || null,
    terminal_session_id: process.env.WT_SESSION || process.env.WEZTERM_PANE || null,
    expects_directive: expectsDirective,
    timeout_seconds: timeoutSeconds,
  };
}

/** Fire-and-forget send with a tight timeout so OpenCode never waits on us. */
function send(rawPayload) {
  if (skipped()) return;
  try {
    const socket = connect(pipePath());
    socket.setTimeout(1200);
    socket.on("connect", () => {
      try {
        socket.end(JSON.stringify(baseEnvelope("opencode", rawPayload)) + "\n");
      } catch {
        /* fail-open */
      }
    });
    socket.on("error", () => {});
    socket.on("timeout", () => {
      try { socket.destroy(); } catch {}
    });
    socket.on("data", () => {
      try { socket.destroy(); } catch {}
    });
  } catch {
    /* fail-open */
  }
}

function clip(text, max = 220) {
  if (!text) return "";
  const clean = String(text).replace(/\s+/g, " ").trim();
  return clean.length <= max ? clean : clean.slice(0, max - 1) + "…";
}

function toolPreview(input) {
  const args = input || {};
  return clip(args.command || args.description || args.filePath || args.path || args.pattern || "");
}

// OpenCode 1.x plugin API: called with the plugin context, returns hook handlers.
async function openIslandServer() {
  const sessions = new Map(); // sessionID -> { cwd, lastAssistantText, lastUserPrompt }

  const session = (id) => {
    if (!sessions.has(id)) sessions.set(id, { cwd: process.cwd(), lastAssistantText: "", lastUserPrompt: "" });
    return sessions.get(id);
  };

  return {
    // Lifecycle + message events.
    async event({ event }) {
      if (!event || !event.type) return;
      const props = event.properties || {};
      const info = props.info || {};
      const sessionId = props.sessionID || info.id || info.sessionID;
      if (!sessionId) return;
      const s = session(sessionId);

      switch (event.type) {
        case "session.created":
          if (info.directory) s.cwd = info.directory;
          send({ hook_event_name: "SessionStart", session_id: sessionId, cwd: s.cwd });
          break;

        case "session.deleted":
          sessions.delete(sessionId);
          send({ hook_event_name: "SessionEnd", session_id: sessionId, cwd: s.cwd });
          break;

        case "session.updated":
          if (info.directory) s.cwd = info.directory;
          if (info.time && info.time.archived) {
            sessions.delete(sessionId);
            send({ hook_event_name: "SessionEnd", session_id: sessionId, cwd: s.cwd });
          }
          break;

        case "session.status":
          if (props.status && props.status.type === "idle") {
            send({
              hook_event_name: "Stop",
              session_id: sessionId,
              cwd: s.cwd,
              last_assistant_message: s.lastAssistantText || "Turn completed",
            });
          }
          break;

        case "session.idle":
          send({
            hook_event_name: "Stop",
            session_id: sessionId,
            cwd: s.cwd,
            last_assistant_message: s.lastAssistantText || "Turn completed",
          });
          break;

        case "message.updated": {
          if (info.role === "assistant" && info.id) {
            // Text is reported through message.part.updated; remember session for it.
            s.lastAssistantMessageId = info.id;
          }
          break;
        }

        case "message.part.updated": {
          const part = props.part;
          if (!part || part.type !== "text" || !part.text) break;
          if (part.messageID && part.messageID === s.lastAssistantMessageId) {
            s.lastAssistantText = clip(part.text, 400);
          }
          break;
        }
      }
    },

    "tool.execute.before": async (input) => {
      const sessionId = input && (input.sessionID || input.sessionId);
      if (!sessionId) return;
      const s = session(sessionId);
      if (input.cwd) s.cwd = input.cwd;
      send({
        hook_event_name: "PreToolUse",
        session_id: sessionId,
        cwd: s.cwd,
        tool_name: (input && input.tool) || "tool",
        tool_input: input && input.args ? input.args : {},
      });
    },

    "tool.execute.after": async (input) => {
      const sessionId = input && (input.sessionID || input.sessionId);
      if (!sessionId) return;
      send({
        hook_event_name: "PostToolUse",
        session_id: sessionId,
        tool_name: (input && input.tool) || "tool",
      });
    },
  };
}

// OpenCode 1.x loads plugin modules and calls the default export's `server()`
// factory; older builds also look for a named `server` export.
export const server = openIslandServer;

export default {
  id: "open-island-win11",
  server: openIslandServer,
};
