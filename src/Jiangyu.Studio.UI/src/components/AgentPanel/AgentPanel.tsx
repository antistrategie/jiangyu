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
  agentSessionCreate,
  agentSessionPrompt,
  agentSessionCancel,
  agentPermissionResponse,
} from "@lib/agent/rpc";
import { useInstalledAgents } from "@lib/agent/installed";
import { useRegistryModalStore } from "@lib/agent/registryModal";
import { AgentDropdown, AgentMenu } from "@components/AgentDropdown/AgentDropdown";
import { Spinner } from "@components/Spinner/Spinner";
import type { AgentStopReason } from "@lib/agent/types";
import type { InstalledAgent } from "@lib/rpc";
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
  const currentAgentId = useAgentStore((s) => s.currentAgentId);
  const sessionId = useAgentStore((s) => s.sessionId);
  const sessionTitle = useAgentStore((s) => s.sessionTitle);
  const currentModeId = useAgentStore((s) => s.currentModeId);
  const prompting = useAgentStore((s) => s.prompting);
  const messages = useAgentStore((s) => s.messages);
  const availableCommands = useAgentStore((s) => s.availableCommands);
  const lastStopReason = useAgentStore((s) => s.lastStopReason);

  const installedAgents = useInstalledAgents((s) => s.agents);
  const currentAgent = installedAgents.find((a) => a.id === currentAgentId) ?? null;
  const setRegistryOpen = useRegistryModalStore((s) => s.setOpen);

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
    if (connected && !prompting) inputRef.current?.focus();
  }, [connected, prompting]);

  const startAgent = useCallback(async (agent: InstalledAgent) => {
    useAgentStore.getState().setConnecting(agent.id);
    try {
      await agentStart(agent.command, agent.args ?? []);
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

  const handleNewSession = useCallback(
    (agent: InstalledAgent) => {
      // Same agent: just open a fresh session in the running process. The
      // connected/!sessionId effect above re-fires once we clear sessionId.
      // Different agent: full restart (host's agentStart tears down the
      // previous one before booting the new subprocess).
      if (agent.id === currentAgentId && connected) {
        useAgentStore.setState({
          sessionId: null,
          sessionTitle: null,
          currentModeId: null,
          lastStopReason: null,
          messages: [],
          availableCommands: [],
        });
      } else {
        void startAgent(agent);
      }
    },
    [currentAgentId, connected, startAgent],
  );

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

  const handleKeyDown = useCallback(
    (e: React.KeyboardEvent) => {
      if (e.key === "Enter" && !e.shiftKey) {
        e.preventDefault();
        void handleSend();
      }
    },
    [handleSend],
  );

  // --- Empty state ---------------------------------------------------------
  if (!connected) {
    return (
      <div className={styles.panel}>
        <div className={styles.emptyStateWrap}>
          {connecting ? (
            <div className={styles.connecting}>
              <Spinner size={16} />
              <span>Connecting to {currentAgent?.name ?? "agent"}…</span>
            </div>
          ) : installedAgents.length === 0 ? (
            <div className={styles.welcomeCard}>
              <h3 className={styles.welcomeEyebrow}>No agents installed</h3>
              <p className={styles.welcomeBlurb}>
                An agent connects Studio to a coding LLM that knows your mod project.
              </p>
              <button
                type="button"
                className={styles.welcomeAction}
                onClick={() => setRegistryOpen(true)}
              >
                Browse Registry
              </button>
            </div>
          ) : (
            <div className={styles.pickerCard}>
              <h3 className={styles.welcomeEyebrow}>Start a session</h3>
              <AgentMenu
                agents={installedAgents}
                onSelect={(a) => void startAgent(a)}
                onOpenRegistry={() => setRegistryOpen(true)}
                inline
              />
            </div>
          )}
          {connectError !== null && <div className={styles.connectError}>{connectError}</div>}
        </div>
      </div>
    );
  }

  // --- Connected state -----------------------------------------------------
  return (
    <div className={styles.panel}>
      <div className={styles.header}>
        <span className={styles.sessionTitle}>{sessionTitle ?? "New session"}</span>
        {currentModeId !== null && <span className={styles.modeBadge}>{currentModeId}</span>}
        <div className={styles.headerActions}>
          <button
            type="button"
            className={styles.headerBtn}
            title="Previous sessions"
            aria-label="Previous sessions"
            onClick={() => {
              /* TODO: Phase 5i — list prior sessions */
            }}
          >
            <ClockIcon />
          </button>
          <AgentDropdown
            agents={installedAgents}
            onSelect={handleNewSession}
            onOpenRegistry={() => setRegistryOpen(true)}
            align="right"
            triggerClassName={styles.headerBtn}
            triggerAriaLabel="Start new session"
            triggerContent={<PlusIcon />}
          />
        </div>
      </div>

      {!sessionId ? (
        <div className={styles.emptyStateWrap}>
          <div className={styles.connecting}>
            <Spinner size={16} />
            <span>Creating session…</span>
          </div>
        </div>
      ) : (
        <>
          <div ref={listRef} className={styles.messageList}>
            {messages.length === 0 && (
              <div className={styles.emptyHint}>Send a message to begin.</div>
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
                  <span className={styles.slashDesc}>{cmd.description}</span>
                </button>
              ))}
            </div>
          )}

          <div className={styles.composer}>
            <textarea
              ref={inputRef}
              className={styles.composerInput}
              value={input}
              onChange={(e) => setInput(e.target.value)}
              onKeyDown={handleKeyDown}
              placeholder={prompting ? "Agent is working…" : "Message the agent…"}
              disabled={prompting}
              rows={1}
            />
            <div className={styles.composerActions}>
              <div className={styles.composerActionsLeft}>
                {/* Future home for model picker, plan/write toggle, context info. */}
              </div>
              <div className={styles.composerActionsRight}>
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
            </div>
          </div>

          {promptError !== null && <div className={styles.inlineError}>{promptError}</div>}
          {promptError === null && stopMessage !== null && (
            <div className={styles.stopReason}>{stopMessage}</div>
          )}
        </>
      )}
    </div>
  );
}

// --- Icons (hairline SVG, 24px grid, 1.25 stroke per design system) -------

function ClockIcon() {
  return (
    <svg
      width="16"
      height="16"
      viewBox="0 0 24 24"
      fill="none"
      stroke="currentColor"
      strokeWidth="1.25"
      aria-hidden="true"
    >
      <circle cx="12" cy="12" r="9" />
      <path d="M12 7v5l3 2" />
    </svg>
  );
}

function PlusIcon() {
  return (
    <svg
      width="16"
      height="16"
      viewBox="0 0 24 24"
      fill="none"
      stroke="currentColor"
      strokeWidth="1.25"
      aria-hidden="true"
    >
      <path d="M12 5v14M5 12h14" />
    </svg>
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
        // Plan entries are replaced wholesale on each plan update; index is stable
        // within a single plan and React only re-keys when the plan itself changes.
        // eslint-disable-next-line @eslint-react/no-array-index-key
        <div key={i} className={styles.planEntry}>
          <span className={styles.planStatus}>{statusIcon(entry.status)}</span>
          <span>{entry.content}</span>
        </div>
      ))}
    </div>
  );
}

function PermissionBlock({ message }: { message: PermissionMessage }) {
  const firstAllow = message.options.find((o) => o.kind.startsWith("allow"));

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
