// Open Island (Windows 11) — Pi & Oh My Pi (OMP) Extension
// Fire-and-forget over Windows Named Pipe (\\.\pipe\open-island-bridge) with 15-second liveness heartbeat.

import net from "node:net";
import process from "node:process";

const PIPE_PATH = "\\\\.\\pipe\\open-island-bridge";

function detectSource(): "pi" | "omp" {
  const scriptPath = (import.meta.url || "").toLowerCase();
  return scriptPath.includes(".omp") ? "omp" : "pi";
}

function sendFireAndForget(eventName: string, payload: Record<string, unknown>): void {
  if (process.env.OPEN_ISLAND_SKIP_HOOKS === "1" || process.env.VIBE_ISLAND_SKIP === "1") return;

  const envelope = {
    source: detectSource(),
    raw_payload: JSON.stringify({ hook_event_name: eventName, ...payload }),
    cwd: process.cwd(),
    pid: process.pid,
    terminal_app: process.env.WT_SESSION ? "Windows Terminal" : (process.env.TERM_PROGRAM || null),
    terminal_session_id: process.env.WT_SESSION || process.env.WEZTERM_PANE || null,
    expects_directive: false,
    timeout_seconds: 5
  };

  try {
    const socket = net.createConnection(PIPE_PATH);
    socket.setTimeout(1500);
    socket.on("connect", () => {
      socket.end(JSON.stringify(envelope) + "\n");
    });
    socket.on("error", () => {
      try { socket.destroy(); } catch {}
    });
    socket.on("timeout", () => {
      try { socket.destroy(); } catch {}
    });
  } catch {
    // Fail-open
  }
}

export default function openIslandExtension(pi: any) {
  let activeSessionId = `pi-${process.pid}`;
  let heartbeatTimer: ReturnType<typeof setInterval> | null = null;

  const startHeartbeat = () => {
    if (heartbeatTimer) clearInterval(heartbeatTimer);
    heartbeatTimer = setInterval(() => {
      sendFireAndForget("Heartbeat", { session_id: activeSessionId });
    }, 15_000);
  };

  const stopHeartbeat = () => {
    if (heartbeatTimer) {
      clearInterval(heartbeatTimer);
      heartbeatTimer = null;
    }
  };

  pi.on("session_start", (evt: any) => {
    activeSessionId = evt?.sessionId || evt?.session_id || activeSessionId;
    sendFireAndForget("SessionStart", {
      session_id: activeSessionId,
      model: evt?.model,
      transcript_path: evt?.transcriptPath || evt?.transcript_path
    });
    startHeartbeat();
  });

  pi.on("before_agent_start", (evt: any) => {
    process.env.OPEN_ISLAND_ACTIVE = "1";
    sendFireAndForget("UserPromptSubmit", {
      session_id: activeSessionId,
      prompt: evt?.prompt || evt?.message || ""
    });
  });

  pi.on("tool_execution_start", (evt: any) => {
    sendFireAndForget("PreToolUse", {
      session_id: activeSessionId,
      tool_name: evt?.toolName || evt?.tool || "tool",
      tool_input: evt?.input || evt?.args || {}
    });
  });

  pi.on("tool_execution_end", (evt: any) => {
    sendFireAndForget("PostToolUse", {
      session_id: activeSessionId,
      tool_name: evt?.toolName || evt?.tool || "tool"
    });
  });

  const handleStop = (evt: any) => {
    sendFireAndForget("Stop", {
      session_id: activeSessionId,
      last_assistant_message: evt?.text || evt?.assistantText || evt?.message || "Turn completed"
    });
  };

  pi.on("agent_settled", handleStop);
  pi.on("session_stop", handleStop);

  pi.on("session_shutdown", (evt: any) => {
    stopHeartbeat();
    if (!evt?.isReload) {
      sendFireAndForget("SessionEnd", { session_id: activeSessionId });
    }
  });
}
