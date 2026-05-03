import { useCallback, useEffect, useRef, useState } from "react";
import {
  useAgentStore,
  type ChatMessage,
  type ToolMessage,
  type PlanMessage,
  type PermissionMessage,
} from "@lib/agent/store";
import {
  agentStart,
  agentStop,
  agentSessionCreate,
  agentSessionPrompt,
  agentSessionCancel,
  agentPermissionResponse,
} from "@lib/agent/rpc";
import type { AgentStopReason } from "@lib/agent/types";
import styles from "./AgentPanel.module.css";

const STOP_REASON_LABEL: Record<AgentStopReason | "error", string | null> = {
  end_turn: null,
  cancelled: "Cancelled.",
  refusal: "The agent declined to continue.",
  max_tokens: "Stopped: token limit reached.",
  max_turn_requests: "Stopped: maximum tool requests in a turn reached.",
  error: null,
};

export function AgentPanel() {
  const connected = useAgentStore((s) => s.connected);
  const connecting = useAgentStore((s) => s.connecting);
  const connectError = useAgentStore((s) => s.connectError);
  const agentName = useAgentStore((s) => s.agentName);
  const sessionId = useAgentStore((s) => s.sessionId);
  const sessionTitle = useAgentStore((s) => s.sessionTitle);
  const currentModeId = useAgentStore((s) => s.currentModeId);
  const prompting = useAgentStore((s) => s.prompting);
  const messages = useAgentStore((s) => s.messages);
  const availableCommands = useAgentStore((s) => s.availableCommands);
  const lastStopReason = useAgentStore((s) => s.lastStopReason);

  const [input, setInput] = useState("");
  const listRef = useRef<HTMLDivElement>(null);
  const inputRef = useRef<HTMLTextAreaElement>(null);

  // Translate the last stop reason to a user-facing string. "end_turn" and
  // host errors render via different surfaces (just clears banner / error
  // banner respectively) so we suppress them here.
  const stopMessage = (() => {
    if (lastStopReason === null) return null;
    if (lastStopReason.error !== null) return null;
    return STOP_REASON_LABEL[lastStopReason.stopReason] ?? null;
  })();
  const promptError = lastStopReason?.error ?? null;

  // Slash-command suggestions: when the input starts with "/", filter the
  // agent's published command list by the prefix typed so far. Sending a
  // chosen command is just sending it as a normal prompt — ACP doesn't have
  // a separate slash-command RPC; the agent parses the leading slash itself.
  const slashSuggestions = (() => {
    if (!input.startsWith("/")) return [];
    if (availableCommands.length === 0) return [];
    const prefix = input.slice(1).toLowerCase();
    return availableCommands.filter((c) => c.name.toLowerCase().startsWith(prefix)).slice(0, 8);
  })();

  // Auto-scroll to bottom on new messages.
  useEffect(() => {
    const el = listRef.current;
    if (el) el.scrollTop = el.scrollHeight;
  }, [messages]);

  // Focus input on mount and after prompting ends.
  useEffect(() => {
    if (!prompting) inputRef.current?.focus();
  }, [prompting]);

  const handleConnect = useCallback(async () => {
    useAgentStore.getState().setConnecting();
    try {
      // TODO: replace with the agent installation/registry once it lands.
      await agentStart("bunx", ["@agentclientprotocol/claude-agent-acp"]);
      // Result arrives asynchronously as agentStartResult notification.
      // The store's handleStartResult will set connected=true, then we
      // auto-create a session via the effect below.
    } catch (err: unknown) {
      useAgentStore.getState().handleStartResult({
        error: err instanceof Error ? err.message : String(err),
      });
    }
  }, []);

  // When the agent connects (via notification), auto-create a session.
  useEffect(() => {
    if (connected && !sessionId) {
      void agentSessionCreate().catch((err: unknown) => {
        useAgentStore.getState().handleSessionCreated({
          error: err instanceof Error ? err.message : String(err),
        });
      });
    }
  }, [connected, sessionId]);

  const handleSend = useCallback(async () => {
    const text = input.trim();
    if (!text || !sessionId || prompting) return;

    setInput("");
    useAgentStore.getState().clearLastStopReason();
    useAgentStore.getState().addUserMessage(text);
    useAgentStore.getState().setPrompting(true);

    try {
      await agentSessionPrompt(text);
    } catch {
      useAgentStore.getState().setPrompting(false);
    }
  }, [input, sessionId, prompting]);

  const handleCancel = useCallback(() => {
    void agentSessionCancel();
  }, []);

  const handleDisconnect = useCallback(() => {
    // Multiple AgentPanel instances can share one agent process per window, so
    // we don't auto-teardown on unmount. The user explicitly disconnects
    // when they're done; project switches handle teardown automatically (see
    // lib/project/store.ts).
    void agentStop().catch(() => {
      /* host will surface the error via agentStatus */
    });
    useAgentStore.getState().setDisconnected();
  }, []);

  const handleKeyDown = useCallback(
    (e: React.KeyboardEvent) => {
      if (e.key === "Enter" && !e.shiftKey) {
        e.preventDefault();
        void handleSend();
      }
    },
    [handleSend],
  );

  return (
    <div className={styles.panel}>
      <div className={styles.header}>
        <span
          className={`${styles.statusDot} ${connected ? styles.statusDot_connected : styles.statusDot_disconnected}`}
        />
        <span className={styles.agentLabel}>{connected ? (agentName ?? "Agent") : "No agent"}</span>
        {sessionTitle !== null && <span className={styles.sessionTitle}>{sessionTitle}</span>}
        {currentModeId !== null && <span className={styles.modeBadge}>{currentModeId}</span>}
        {connected && (
          <button
            type="button"
            className={styles.disconnectBtn}
            onClick={handleDisconnect}
            title="Disconnect agent"
          >
            Disconnect
          </button>
        )}
      </div>

      {!connected ? (
        <div className={styles.emptyState}>
          <div style={{ textAlign: "center" }}>
            {connecting ? (
              <span>Connecting…</span>
            ) : (
              <button type="button" className={styles.sendBtn} onClick={() => void handleConnect()}>
                Connect Agent
              </button>
            )}
            {connectError !== null && <div className={styles.connectError}>{connectError}</div>}
          </div>
        </div>
      ) : !sessionId ? (
        <div className={styles.emptyState}>Creating session…</div>
      ) : (
        <>
          <div ref={listRef} className={styles.messageList}>
            {messages.length === 0 && (
              <div className={styles.emptyState}>Send a message to begin.</div>
            )}
            {messages.map((msg) => (
              <MessageBubble key={msg.id} message={msg} />
            ))}
          </div>

          {slashSuggestions.length > 0 && (
            <div className={styles.slashSuggestions}>
              {slashSuggestions.map((cmd) => (
                <button
                  key={cmd.name}
                  type="button"
                  className={styles.slashItem}
                  onClick={() => setInput(`/${cmd.name} `)}
                >
                  <span className={styles.slashName}>/{cmd.name}</span>
                  {cmd.description !== null && cmd.description !== undefined && (
                    <span className={styles.slashDesc}>{cmd.description}</span>
                  )}
                </button>
              ))}
            </div>
          )}

          <div className={styles.inputArea}>
            <textarea
              ref={inputRef}
              className={styles.input}
              value={input}
              onChange={(e) => setInput(e.target.value)}
              onKeyDown={handleKeyDown}
              placeholder={prompting ? "Agent is working…" : "Message the agent…"}
              disabled={prompting}
              rows={1}
            />
            {prompting ? (
              <button type="button" className={styles.cancelBtn} onClick={handleCancel}>
                Cancel
              </button>
            ) : (
              <button
                type="button"
                className={styles.sendBtn}
                onClick={() => void handleSend()}
                disabled={!input.trim()}
              >
                Send
              </button>
            )}
          </div>

          {connectError !== null && <div className={styles.inlineError}>{connectError}</div>}
          {connectError === null && promptError !== null && (
            <div className={styles.inlineError}>{promptError}</div>
          )}
          {connectError === null && promptError === null && stopMessage !== null && (
            <div className={styles.stopReason}>{stopMessage}</div>
          )}
        </>
      )}
    </div>
  );
}

// --- Message rendering ---

function MessageBubble({ message }: { message: ChatMessage }) {
  switch (message.role) {
    case "user":
      return <div className={`${styles.message} ${styles.message_user}`}>{message.text}</div>;
    case "agent":
      return <div className={`${styles.message} ${styles.message_agent}`}>{message.text}</div>;
    case "thought":
      return <div className={`${styles.message} ${styles.message_thought}`}>{message.text}</div>;
    case "tool":
      return <ToolBlock message={message} />;
    case "plan":
      return <PlanBlock message={message} />;
    case "permission":
      return <PermissionBlock message={message} />;
  }
}

function ToolBlock({ message }: { message: ToolMessage }) {
  return (
    <div className={styles.toolBlock}>
      <div className={styles.toolHeader}>
        <span className={styles.toolName}>{message.toolName}</span>
      </div>
      {message.content.map((c, i) => (
        // Tool content is append-only; index is stable for the lifetime of the call.
        // eslint-disable-next-line @eslint-react/no-array-index-key
        <div key={`${message.id}-${i}`} className={styles.toolContent}>
          {c.type === "content" && c.content.type === "text" && c.content.text}
          {c.type === "diff" && (
            <>
              <div className={styles.diffPath}>{c.path}</div>
              {c.newText}
            </>
          )}
          {c.type === "terminal" && <div className={styles.diffPath}>terminal {c.terminalId}</div>}
        </div>
      ))}
    </div>
  );
}

function PlanBlock({ message }: { message: PlanMessage }) {
  const statusIcon = (status?: string | null) => {
    switch (status) {
      case "completed":
        return "✓";
      case "in_progress":
        return "▸";
      default:
        return "○";
    }
  };

  return (
    <div className={styles.planBlock}>
      <div className={styles.planTitle}>Plan</div>
      {message.entries.map((entry, i) => (
        <div key={i} className={styles.planEntry}>
          <span className={styles.planStatus}>{statusIcon(entry.status)}</span>
          <span>{entry.content}</span>
        </div>
      ))}
    </div>
  );
}

function PermissionBlock({ message }: { message: PermissionMessage }) {
  const firstAllow = message.options?.find((o) => o.kind.startsWith("allow"));

  const handleAllow = () => {
    void agentPermissionResponse(message.permissionId, "selected", firstAllow?.optionId);
    useAgentStore.getState().resolvePermission(message.permissionId);
  };

  const handleDeny = () => {
    void agentPermissionResponse(message.permissionId, "cancelled");
    useAgentStore.getState().resolvePermission(message.permissionId);
  };

  const tc = message.toolCall;
  const heading = tc.title ?? tc.kind ?? "Tool call";

  return (
    <div
      className={`${styles.permissionBlock} ${message.resolved ? styles.permissionResolved : ""}`}
    >
      <div className={styles.permissionDesc}>{heading}</div>
      {tc.content?.map((c, i) => (
        // Permission content is fixed at request time.
        // eslint-disable-next-line @eslint-react/no-array-index-key
        <div key={`${message.permissionId}-${i}`} className={styles.toolContent}>
          {c.type === "content" && c.content.type === "text" && c.content.text}
          {c.type === "diff" && (
            <>
              <div className={styles.diffPath}>{c.path}</div>
              {c.newText}
            </>
          )}
          {c.type === "terminal" && <div className={styles.diffPath}>terminal {c.terminalId}</div>}
        </div>
      ))}
      {!message.resolved && (
        <div className={styles.permissionActions}>
          <button
            type="button"
            className={`${styles.permissionBtn} ${styles.permissionBtn_allow}`}
            onClick={handleAllow}
          >
            Allow
          </button>
          <button
            type="button"
            className={`${styles.permissionBtn} ${styles.permissionBtn_deny}`}
            onClick={handleDeny}
          >
            Deny
          </button>
        </div>
      )}
    </div>
  );
}
