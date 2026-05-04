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
  agentSessionResume,
  agentPermissionResponse,
  agentAuthenticate,
} from "@lib/agent/rpc";
import { useInstalledAgents } from "@lib/agent/installed";
import { useRegistryModalStore } from "@lib/agent/registryModal";
import { AgentDropdown, AgentMenu } from "@components/AgentDropdown/AgentDropdown";
import { AgentHistoryPopover } from "@components/AgentHistoryPopover/AgentHistoryPopover";
import { Spinner } from "@components/Spinner/Spinner";
import { Clock, Plus } from "lucide-react";
import type { AgentStopReason, AuthMethod, PermissionOption } from "@lib/agent/types";
import type { InstalledAgent } from "@lib/rpc";
import { DiffStatsForContent, DiffView } from "./DiffView";
import { Markdown } from "./Markdown";
import { SessionOptionsBar } from "./SessionOptions";
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
  const availableModes = useAgentStore((s) => s.availableModes);
  // ACP advertises mode ids as URIs (e.g.
  // "https://agentclientprotocol.com/protocol/session-modes#agent"); the
  // human label is on the matching availableModes entry. Falls back to a
  // shortened id for unknown modes so the badge never renders a full URL.
  const currentMode = availableModes.find((m) => m.id === currentModeId);
  const modeBadgeLabel =
    currentMode?.name ?? (currentModeId !== null ? shortenModeId(currentModeId) : null);
  const prompting = useAgentStore((s) => s.prompting);
  const messages = useAgentStore((s) => s.messages);
  const availableCommands = useAgentStore((s) => s.availableCommands);
  const lastStopReason = useAgentStore((s) => s.lastStopReason);

  const installedAgents = useInstalledAgents((s) => s.agents);
  const currentAgent = installedAgents.find((a) => a.id === currentAgentId) ?? null;
  const setRegistryOpen = useRegistryModalStore((s) => s.setOpen);
  const replaying = useAgentStore((s) => s.replaying);
  const authMethods = useAgentStore((s) => s.authMethods);
  const authenticatingMethodId = useAgentStore((s) => s.authenticatingMethodId);
  const authError = useAgentStore((s) => s.authError);
  const needsAuth = authMethods.length > 0;

  const [input, setInput] = useState("");
  const [historyOpen, setHistoryOpen] = useState(false);
  const historyWrapRef = useRef<HTMLDivElement>(null);
  const listRef = useRef<HTMLDivElement>(null);
  const inputRef = useRef<HTMLTextAreaElement>(null);

  // Outside-click + Escape to dismiss the history popover.
  useEffect(() => {
    if (!historyOpen) return;
    const onMouseDown = (e: MouseEvent) => {
      if (historyWrapRef.current?.contains(e.target as Node) !== true) setHistoryOpen(false);
    };
    const onKey = (e: KeyboardEvent) => {
      if (e.key === "Escape") setHistoryOpen(false);
    };
    document.addEventListener("mousedown", onMouseDown);
    document.addEventListener("keydown", onKey);
    return () => {
      document.removeEventListener("mousedown", onMouseDown);
      document.removeEventListener("keydown", onKey);
    };
  }, [historyOpen]);

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

  // Stick to the bottom only when the user already is. Scrolling up to read
  // history shouldn't yank the view back down on every streamed token; once
  // they scroll back near the bottom we resume the follow.
  const stickToBottomRef = useRef(true);
  const handleListScroll = useCallback(() => {
    const el = listRef.current;
    if (!el) return;
    // 80 px tolerance covers the case where the last message is mid-render
    // and the layout settles a frame after the scroll event.
    stickToBottomRef.current = el.scrollHeight - el.scrollTop - el.clientHeight < 80;
  }, []);
  // Reset to "stuck" on session switch so a freshly-loaded session opens at
  // its most recent messages, regardless of where the previous session was.
  useEffect(() => {
    stickToBottomRef.current = true;
  }, [sessionId]);
  useEffect(() => {
    const el = listRef.current;
    if (el && stickToBottomRef.current) el.scrollTop = el.scrollHeight;
  }, [messages]);

  // Focus input on mount and after prompting ends.
  useEffect(() => {
    if (connected && !prompting) inputRef.current?.focus();
  }, [connected, prompting]);

  const startAgent = useCallback(async (agent: InstalledAgent) => {
    useAgentStore.getState().setConnecting(agent.id);
    try {
      await agentStart(agent.command, agent.args ?? [], agent.id);
      // Result arrives asynchronously as agentStartResult notification.
      // The store's handleStartResult will set connected=true, then we
      // auto-create a session via the effect below.
    } catch (err: unknown) {
      useAgentStore.getState().handleStartResult({
        error: err instanceof Error ? err.message : String(err),
      });
    }
  }, []);

  // Auto-create or auto-resume once the agent is connected, depending on
  // what the user asked for. The pendingSession discriminator decouples
  // intent ("I want a new session" vs "I want to resume X") from state,
  // so a resume failure can't loop back into a silent new-session create.
  const pendingSession = useAgentStore((s) => s.pendingSession);
  useEffect(() => {
    if (!connected || pendingSession === null) return;
    // Block until the user has cleared any auth gate the agent put up.
    // The sign-in card drives `agentAuthenticate`, which clears
    // `authMethods` on success and re-fires this effect via the deps.
    if (needsAuth) return;

    if (pendingSession.kind === "create" && !sessionId) {
      void agentSessionCreate().catch((err: unknown) => {
        useAgentStore.getState().handleSessionCreated({
          error: err instanceof Error ? err.message : String(err),
        });
      });
    } else if (pendingSession.kind === "resume") {
      const targetSessionId = pendingSession.sessionId;
      // beginReplay clears pendingSession itself, so the effect won't
      // re-fire after this branch runs.
      useAgentStore.getState().beginReplay(targetSessionId);
      void agentSessionResume(targetSessionId).catch((err: unknown) => {
        useAgentStore.getState().handleResumeResult({
          error: err instanceof Error ? err.message : String(err),
        });
      });
    }
  }, [connected, sessionId, pendingSession, needsAuth]);

  const handleAuthenticate = useCallback((methodId: string) => {
    useAgentStore.getState().beginAuthenticate(methodId);
    void agentAuthenticate(methodId).catch((err: unknown) => {
      useAgentStore.getState().handleAuthResult({
        methodId,
        error: err instanceof Error ? err.message : String(err),
      });
    });
  }, []);

  const handleNewSession = useCallback(
    (agent: InstalledAgent) => {
      // Same agent: just open a fresh session in the running process. The
      // pendingSession flag tells the auto-create effect that this
      // sessionId-clear is an explicit user intent (vs a resume failure).
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
          pendingSession: { kind: "create" },
        });
      } else {
        void startAgent(agent);
      }
    },
    [currentAgentId, connected, startAgent],
  );

  const handleSend = useCallback(async () => {
    const text = input.trim();
    if (!text || !sessionId || replaying) return;

    setInput("");
    useAgentStore.getState().clearLastStopReason();
    useAgentStore.getState().addUserMessage(text);
    useAgentStore.getState().setPrompting(true);

    // ACP queues concurrent prompts on the agent side; sending mid-turn
    // is fine and avoids losing the modder's typed message.
    try {
      await agentSessionPrompt(text);
    } catch {
      useAgentStore.getState().setPrompting(false);
    }
  }, [input, sessionId, replaying]);

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
              {/* Recent sessions inline — short list with a hard cap so the
                  picker card stays compact. The full list is still
                  reachable via the header history popover after connect.
                  The component hides itself (and the header) when no
                  sessions exist, so a fresh project shows just the
                  agent menu. */}
              <AgentHistoryPopover inline limit={5} inlineHeader="Or resume" />
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
        <span className={styles.sessionTitle}>{deriveSessionLabel(sessionTitle, messages)}</span>
        {modeBadgeLabel !== null && <span className={styles.modeBadge}>{modeBadgeLabel}</span>}
        <div className={styles.headerActions}>
          <div className={styles.historyWrap} ref={historyWrapRef}>
            <button
              type="button"
              className={styles.headerBtn}
              title="Previous sessions"
              aria-label="Previous sessions"
              aria-haspopup="menu"
              aria-expanded={historyOpen}
              onClick={() => setHistoryOpen((o) => !o)}
            >
              <Clock size={16} aria-hidden="true" />
            </button>
            {historyOpen && <AgentHistoryPopover onClose={() => setHistoryOpen(false)} />}
          </div>
          <AgentDropdown
            agents={installedAgents}
            onSelect={handleNewSession}
            onOpenRegistry={() => setRegistryOpen(true)}
            align="right"
            triggerClassName={styles.headerBtn}
            triggerAriaLabel="Start new session"
            triggerContent={<Plus size={16} aria-hidden="true" />}
          />
        </div>
      </div>

      {!sessionId ? (
        <div className={styles.emptyStateWrap}>
          {needsAuth ? (
            <AuthCard
              agentName={currentAgent?.name ?? null}
              methods={authMethods}
              authenticatingMethodId={authenticatingMethodId}
              error={authError}
              onPick={handleAuthenticate}
            />
          ) : connectError !== null ? (
            <div className={styles.resumeErrorCard}>
              <div className={styles.connectError}>{connectError}</div>
              <button
                type="button"
                className={styles.welcomeAction}
                onClick={() => {
                  useAgentStore.setState({
                    connectError: null,
                    pendingSession: { kind: "create" },
                  });
                }}
              >
                Start new session
              </button>
            </div>
          ) : (
            <div className={styles.connecting}>
              <Spinner size={16} />
              <span>Creating session…</span>
            </div>
          )}
        </div>
      ) : (
        <>
          <div ref={listRef} className={styles.messageList} onScroll={handleListScroll}>
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
              placeholder={
                replaying
                  ? "Loading conversation history…"
                  : prompting
                    ? "Type next message (sends after current turn)…"
                    : "Message the agent…"
              }
              disabled={replaying}
              rows={1}
            />
            <div className={styles.composerActions}>
              <div className={styles.composerActionsLeft}>
                <SessionOptionsBar />
              </div>
              <div className={styles.composerActionsRight}>
                {/* During a turn, show Cancel when there's nothing typed —
                    that's the user's only sensible action mid-prompt.
                    As soon as they start typing, swap to Send so the new
                    message can fire (ACP queues it agent-side). */}
                {prompting && !input.trim() ? (
                  <button type="button" className={styles.cancelBtn} onClick={handleCancel}>
                    Cancel
                  </button>
                ) : (
                  <button
                    type="button"
                    className={styles.sendBtn}
                    onClick={() => void handleSend()}
                    disabled={!input.trim() || replaying}
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

/**
 * Choose what to display in the header. `session_info_update` is the
 * agent's authoritative title once it has decided one (Claude Agent ACP
 * generally emits it after a few turns; some agents never do). Until
 * then, fall back to the first user message — same heuristic the history
 * popover uses for `firstMessage`. "New session" is the empty-canvas
 * fallback when nothing has been typed yet.
 */
/**
 * ACP mode ids are typically URIs with a `#fragment` carrying the
 * short name. Trim to that fragment for the badge fallback when we
 * don't have a matching availableModes entry to read `name` from.
 */
function shortenModeId(id: string): string {
  const hashIdx = id.lastIndexOf("#");
  if (hashIdx >= 0 && hashIdx < id.length - 1) return id.slice(hashIdx + 1);
  const slashIdx = id.lastIndexOf("/");
  if (slashIdx >= 0 && slashIdx < id.length - 1) return id.slice(slashIdx + 1);
  return id;
}

export function AuthCard({
  agentName,
  methods,
  authenticatingMethodId,
  error,
  onPick,
}: {
  agentName: string | null;
  methods: readonly AuthMethod[];
  authenticatingMethodId: string | null;
  error: string | null;
  onPick: (methodId: string) => void;
}) {
  const busy = authenticatingMethodId !== null;
  return (
    <div className={styles.welcomeCard}>
      <h3 className={styles.welcomeEyebrow}>Sign in</h3>
      <p className={styles.welcomeBlurb}>
        {agentName ?? "This agent"} needs to authenticate before it can start a session.
      </p>
      {methods.map((m) => {
        const inFlight = authenticatingMethodId === m.id;
        return (
          <button
            key={m.id}
            type="button"
            className={styles.welcomeAction}
            disabled={busy}
            onClick={() => onPick(m.id)}
          >
            {inFlight ? `Signing in (${m.name})…` : m.name}
          </button>
        );
      })}
      {error !== null && <div className={styles.connectError}>{error}</div>}
    </div>
  );
}

function deriveSessionLabel(sessionTitle: string | null, messages: readonly ChatMessage[]): string {
  if (sessionTitle !== null && sessionTitle.length > 0) return sessionTitle;
  const firstUser = messages.find((m) => m.role === "user");
  if (firstUser !== undefined) {
    const trimmed = firstUser.text.trim();
    return trimmed.length > 60 ? `${trimmed.slice(0, 60)}…` : trimmed;
  }
  return "New session";
}

// --- Message rendering ---

function MessageBubble({ message }: { message: ChatMessage }) {
  switch (message.role) {
    case "user":
      return <div className={`${styles.message} ${styles.message_user}`}>{message.text}</div>;
    case "agent":
      return (
        <div className={`${styles.message} ${styles.message_agent}`}>
          <Markdown text={message.text} />
        </div>
      );
    case "thought":
      return (
        <div className={`${styles.message} ${styles.message_thought}`}>
          <Markdown text={message.text} />
        </div>
      );
    case "tool":
      return <ToolBlock message={message} />;
    case "plan":
      return <PlanBlock message={message} />;
    case "permission":
      return <PermissionBlock message={message} />;
  }
}

function ToolBlock({ message }: { message: ToolMessage }) {
  // Default collapsed so a chat full of tool calls reads like a conversation,
  // not a debugger trace. PermissionBlock (which gates writes) deliberately
  // does NOT collapse — there the content IS what's being approved.
  const [expanded, setExpanded] = useState(false);
  const firstDiff = message.content.find((c) => c.type === "diff");
  // Agent-supplied title usually already encodes what + where (e.g. "Edit
  // templates/foo.kdl"), so only show our own path summary when it adds
  // information the title doesn't.
  const rawSummary = summariseToolContent(message.content);
  const summary = rawSummary && !message.toolName.includes(rawSummary) ? rawSummary : null;
  const hasContent = message.content.length > 0;

  return (
    <div className={styles.toolBlock}>
      <button
        type="button"
        className={styles.toolHeader}
        onClick={() => setExpanded((v) => !v)}
        disabled={!hasContent}
        aria-expanded={expanded}
      >
        <span className={styles.toolChevron} aria-hidden="true">
          {hasContent ? (expanded ? "▾" : "▸") : "·"}
        </span>
        <span className={styles.toolName}>{message.toolName}</span>
        {summary && <span className={styles.toolSummary}>{summary}</span>}
        {firstDiff && <DiffStatsForContent diff={firstDiff} />}
      </button>
      {expanded &&
        message.content.map((c, i) => (
          // Tool content is append-only; index is stable for the lifetime of the call.
          // eslint-disable-next-line @eslint-react/no-array-index-key
          <div key={`${message.id}-${i}`} className={styles.toolContent}>
            {c.type === "content" && c.content.type === "text" && c.content.text}
            {c.type === "diff" && <DiffView diff={c} />}
            {c.type === "terminal" && (
              <div className={styles.diffPath}>terminal {c.terminalId}</div>
            )}
          </div>
        ))}
    </div>
  );
}

/** Map an ACP permission-option kind to its button visual variant. */
function permissionBtnClass(kind: PermissionOption["kind"]): string {
  switch (kind) {
    case "allow_once":
      return styles.permissionBtn_allow;
    case "allow_always":
      // Lighter "allow" variant signals that this choice persists for the
      // rest of the session (the UI doesn't ask again until reconnect).
      return `${styles.permissionBtn_allow} ${styles.permissionBtn_always}`;
    case "reject_once":
      return styles.permissionBtn_deny;
    case "reject_always":
      return `${styles.permissionBtn_deny} ${styles.permissionBtn_always}`;
  }
}

/** Pick a single concise blurb for a collapsed tool block's header. */
function summariseToolContent(content: readonly ToolMessage["content"][number][]): string | null {
  for (const c of content) {
    if (c.type === "diff") return c.path;
    if (c.type === "terminal") return `terminal ${c.terminalId}`;
  }
  return null;
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

export function PermissionBlock({ message }: { message: PermissionMessage }) {
  // Each agent-supplied option gets its own button. ACP exposes a four-way
  // choice (allow_once / allow_always / reject_once / reject_always); we
  // don't collapse to "Allow / Deny" because the distinction between
  // "_once" and "_always" is the user's lever for auto-approve. Picking
  // allow_once silently for them throws that lever away.
  const handleSelect = (option: PermissionOption) => {
    // _always selections are remembered for the rest of the session so
    // future requests of the same kind bypass the prompt. Bounded by
    // setConnecting / handleSessionCreated / setDisconnected.
    if (message.toolCall.kind) {
      const store = useAgentStore.getState();
      if (option.kind === "allow_always") store.addAutoApproveKind(message.toolCall.kind);
      else if (option.kind === "reject_always") store.addAutoRejectKind(message.toolCall.kind);
    }
    void agentPermissionResponse(message.permissionId, "selected", option.optionId);
    useAgentStore.getState().resolvePermission(message.permissionId);
  };

  const tc = message.toolCall;
  const heading = tc.title ?? tc.kind ?? "Tool call";
  // Surface the +N −N for the first diff content item inline with the
  // heading; the diff body still renders below so the modder can review
  // before approving.
  const firstDiff = tc.content?.find((c) => c.type === "diff");

  return (
    <div
      className={`${styles.permissionBlock} ${message.resolved ? styles.permissionResolved : ""}`}
    >
      <div className={styles.permissionDesc}>
        <span className={styles.permissionDescText}>{heading}</span>
        {firstDiff && <DiffStatsForContent diff={firstDiff} />}
      </div>
      {tc.content?.map((c, i) => (
        // Permission content is fixed at request time.
        // eslint-disable-next-line @eslint-react/no-array-index-key
        <div key={`${message.permissionId}-${i}`} className={styles.toolContent}>
          {c.type === "content" && c.content.type === "text" && c.content.text}
          {c.type === "diff" && <DiffView diff={c} />}
          {c.type === "terminal" && <div className={styles.diffPath}>terminal {c.terminalId}</div>}
        </div>
      ))}
      {!message.resolved && (
        <div className={styles.permissionActions}>
          {message.options.map((option) => (
            <button
              key={option.optionId}
              type="button"
              className={`${styles.permissionBtn} ${permissionBtnClass(option.kind)}`}
              onClick={() => handleSelect(option)}
            >
              {option.name}
            </button>
          ))}
        </div>
      )}
    </div>
  );
}
