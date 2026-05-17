import { create } from "zustand";
import { subscribe } from "@shared/rpc";
import type {
  AgentStartResult,
  AgentAuthResult,
  AgentSessionResult,
  AgentStopReason,
  AuthMethod,
  SessionNotification,
  SessionUpdate,
  PermissionRequest,
  AgentStatusEvent,
  AvailableCommand,
  ConfigOption,
  SessionMode,
  SessionModes,
  ContentBlock,
  PermissionOption,
  PlanEntry,
  ToolCallContent,
  ToolKind,
  PermissionToolCall,
} from "./types";
import { agentPermissionResponse } from "./rpc";

/// Notification emitted by the host when a prompt turn ends. Carries the
/// spec stop reason or, for host-side failures, an opaque error string.
export interface AgentPromptResultEvent {
  readonly stopReason: AgentStopReason | "error";
  readonly error: string | null;
}

/// Notification emitted by the host when an `agentSessionResume` RPC
/// finishes. Per ACP spec, the agent has already streamed all historical
/// session updates by the time this lands.
export interface AgentResumeResult {
  readonly sessionId?: string;
  readonly modes?: SessionModes | null;
  readonly configOptions?: ConfigOption[] | null;
  /** Mode id Studio persisted when the user last picked one for this
   *  session. Overrides the agent's reported currentModeId because some
   *  agents (Claude) don't persist set_mode across session/load. */
  readonly persistedModeId?: string | null;
  /** configId → value pairs Studio persisted from prior
   *  set_config_option calls. Same rationale as persistedModeId. */
  readonly persistedConfigValues?: Readonly<Record<string, unknown>> | null;
  readonly error?: string;
}

/// Discriminated intent for what should happen automatically once the
/// agent finishes connecting. "create" → call `session/new`. "resume" →
/// call `agentSessionResume` with the captured session id. The auto-effect
/// in `AgentPanel` consumes this and clears it, so a resume failure
/// doesn't loop into a `create`.
export type PendingSessionAction =
  | { readonly kind: "create" }
  | { readonly kind: "resume"; readonly sessionId: string };

// --- Chat message model ---

export type ChatRole = "user" | "agent" | "thought" | "tool" | "plan" | "permission";

interface MessageBase {
  /** Stable identity for React keys; assigned at creation, never reused. */
  readonly id: string;
}

export interface UserMessage extends MessageBase {
  readonly role: "user";
  readonly text: string;
}

export interface AgentMessage extends MessageBase {
  readonly role: "agent";
  readonly text: string;
}

export interface ThoughtMessage extends MessageBase {
  readonly role: "thought";
  readonly text: string;
}

export interface ToolMessage extends MessageBase {
  readonly role: "tool";
  readonly toolCallId: string;
  readonly toolName: string;
  readonly content: readonly ToolCallContent[];
  readonly status: string | null;
}

export interface PlanMessage extends MessageBase {
  readonly role: "plan";
  readonly entries: readonly PlanEntry[];
}

export interface PermissionMessage extends MessageBase {
  readonly role: "permission";
  readonly permissionId: string;
  readonly toolCall: PermissionToolCall;
  readonly options: PermissionRequest["options"];
  readonly resolved: boolean;
}

export type ChatMessage =
  | UserMessage
  | AgentMessage
  | ThoughtMessage
  | ToolMessage
  | PlanMessage
  | PermissionMessage;

let messageIdCounter = 0;
function nextMessageId(): string {
  messageIdCounter += 1;
  return `m${messageIdCounter.toString(36)}`;
}

/** Best-available identifier for a config option (agents emit either
 *  `key` or `id`; Claude Agent emits neither and we drop those). */
export function configOptionIdentifier(opt: ConfigOption): string | null {
  return opt.key ?? opt.id ?? null;
}

/** Merge a config_option_update payload into the existing list by key.
 *  New keys append; existing keys overwrite. Entries without any
 *  identifier are dropped (we have no way to address them). */
function mergeConfigOptions(
  existing: readonly ConfigOption[],
  incoming: readonly ConfigOption[],
): ConfigOption[] {
  const byKey = new Map<string, ConfigOption>();
  for (const opt of existing) {
    const id = configOptionIdentifier(opt);
    if (id !== null) byKey.set(id, opt);
  }
  for (const opt of incoming) {
    const id = configOptionIdentifier(opt);
    if (id !== null) byKey.set(id, opt);
  }
  return [...byKey.values()];
}

/** Extract a flat string from a content block. Non-text blocks render to a
 *  short placeholder so we don't drop them silently. */
function contentBlockText(block: ContentBlock): string {
  switch (block.type) {
    case "text":
      return block.text;
    case "image":
      return "[image]";
    case "audio":
      return "[audio]";
    case "resource_link":
      return `[resource ${block.uri}]`;
  }
}

// --- Store ---

interface AgentStore {
  // Connection
  readonly connected: boolean;
  readonly connecting: boolean;
  readonly connectError: string | null;
  readonly agentName: string | null;
  readonly agentVersion: string | null;
  /**
   * The InstalledAgent.id we asked the host to start. Set optimistically
   * when agentStart is called so the UI can detect "new session in the
   * same agent" picks without restarting the process. Distinct from
   * agentName which is the agent's self-reported initialize-handshake name.
   */
  readonly currentAgentId: string | null;

  // Session
  readonly sessionId: string | null;
  readonly sessionTitle: string | null;
  readonly currentModeId: string | null;
  readonly prompting: boolean;
  /**
   * True between calling `agentSessionResume` and receiving the
   * `agentSessionResumed` notification. While true, the live conversation
   * is locked (no new prompts) and `user_message_chunk` updates are
   * appended to `messages` instead of being treated as redundant echoes —
   * resume is the only path where these blocks aren't already in the local
   * state.
   */
  readonly replaying: boolean;
  /**
   * Intent flag for the auto-create / auto-resume effect that runs when
   * the agent finishes connecting. Set by code paths that actually want
   * a session change ("user clicked agent X", "user clicked session Y in
   * the history list"); cleared on failure or after the corresponding
   * RPC fires. The effect ONLY runs when this is non-null — otherwise
   * nulling sessionId (e.g. on a resume failure) would silently spawn
   * an unwanted new session and bury the resume error.
   */
  readonly pendingSession: PendingSessionAction | null;

  /** Last turn's stop reason, surfaced when not "end_turn". */
  readonly lastStopReason: AgentPromptResultEvent | null;

  // Chat
  readonly messages: ChatMessage[];

  /**
   * ACP authentication methods the agent advertised in initialize. When
   * non-empty, the panel renders a sign-in card instead of auto-creating a
   * session — most notably for Copilot, which gates session/new behind
   * `authenticate`. Cleared on a successful authenticate or when the
   * agent's session/new succeeds (some agents advertise methods opportun-
   * istically even when external auth is already in place).
   */
  readonly authMethods: readonly AuthMethod[];
  /** id of the auth method currently being driven; null when idle. */
  readonly authenticatingMethodId: string | null;
  readonly authError: string | null;

  // Slash commands
  readonly availableCommands: AvailableCommand[];

  // Agent-declared session knobs (model, thinking budget, mode catalogue).
  // Populated from session/new and session/load responses; mutated by
  // current_mode_update / config_option_update notifications.
  readonly availableModes: readonly SessionMode[];
  readonly configOptions: readonly ConfigOption[];

  // Auto-approve. ACP doesn't natively scope `_always` outcomes; clients
  // remember and bypass future prompts. All three fields are session-
  // scoped: they reset on connect, disconnect, and session change so a
  // long-lived "trust all" can't leak across sessions.
  /** Tool kinds the user has chosen `allow_always` for in this session. */
  readonly autoApproveKinds: ReadonlySet<ToolKind>;
  /** Tool kinds the user has chosen `reject_always` for in this session. */
  readonly autoRejectKinds: ReadonlySet<ToolKind>;

  // Actions
  readonly setConnecting: (agentId: string, pendingAction?: PendingSessionAction) => void;
  readonly handleStartResult: (result: AgentStartResult) => void;
  readonly beginAuthenticate: (methodId: string) => void;
  readonly handleAuthResult: (result: AgentAuthResult) => void;
  readonly handleSessionCreated: (result: AgentSessionResult) => void;
  readonly beginReplay: (sessionId: string) => void;
  readonly handleResumeResult: (result: AgentResumeResult) => void;
  readonly setConnected: (info: AgentStartResult) => void;
  readonly setDisconnected: () => void;
  readonly setSession: (sessionId: string) => void;
  readonly addUserMessage: (text: string) => void;
  readonly setPrompting: (prompting: boolean) => void;
  readonly clearLastStopReason: () => void;
  readonly handleUpdate: (update: SessionUpdate) => void;
  readonly handlePromptResult: (event: AgentPromptResultEvent) => void;
  readonly handlePermissionRequest: (request: PermissionRequest) => void;
  readonly resolvePermission: (permissionId: string) => void;
  readonly clearMessages: () => void;
  readonly addAutoApproveKind: (kind: ToolKind) => void;
  readonly addAutoRejectKind: (kind: ToolKind) => void;
}

export const useAgentStore = create<AgentStore>((set, get) => ({
  connected: false,
  connecting: false,
  connectError: null,
  agentName: null,
  agentVersion: null,
  currentAgentId: null,
  sessionId: null,
  sessionTitle: null,
  currentModeId: null,
  prompting: false,
  replaying: false,
  pendingSession: null,
  lastStopReason: null,
  messages: [],
  availableCommands: [],
  availableModes: [],
  configOptions: [],
  authMethods: [],
  authenticatingMethodId: null,
  authError: null,
  autoApproveKinds: new Set<ToolKind>(),
  autoRejectKinds: new Set<ToolKind>(),

  setConnecting: (agentId, pendingAction = { kind: "create" }) =>
    // Switching agents (or first-time connect) puts the panel into a clean
    // "starting up" state. We MUST clear `connected`, `sessionId`, and the
    // session-scoped chat state — otherwise, when the new agent's
    // agentStartResult lands, the auto-effect's deps wouldn't actually
    // change (connected was already true from the previous agent,
    // sessionId still points at the dead session) and never re-fires.
    // pendingAction defaults to "create" but can be {kind:"resume",
    // sessionId} for resuming a stored thread that requires booting an
    // agent first.
    set({
      connecting: true,
      connected: false,
      connectError: null,
      currentAgentId: agentId,
      agentName: null,
      agentVersion: null,
      sessionId: null,
      sessionTitle: null,
      currentModeId: null,
      replaying: false,
      pendingSession: pendingAction,
      lastStopReason: null,
      messages: [],
      availableCommands: [],
      availableModes: [],
      configOptions: [],
      authMethods: [],
      authenticatingMethodId: null,
      authError: null,
      autoApproveKinds: new Set<ToolKind>(),
      autoRejectKinds: new Set<ToolKind>(),
    }),

  handleStartResult: (result) => {
    if (result.error) {
      set({
        connecting: false,
        connectError: result.error,
        currentAgentId: null,
        // The intent doesn't survive a failed connect — otherwise a
        // queued resume would re-fire if a future agent connect happens.
        pendingSession: null,
      });
    } else {
      // If the agent advertised auth methods, gate the auto-create-session
      // effect: pendingSession stays as the user asked for, but the panel
      // will render the sign-in card while authMethods is non-empty. After
      // a successful authenticate the methods are cleared and the effect
      // proceeds. Agents that don't gate session/new (Claude when configured
      // outside Studio) advertise no methods and skip this entirely.
      const methods = result.authMethods ?? [];
      set({
        connecting: false,
        connectError: null,
        connected: true,
        agentName: result.agentName ?? null,
        agentVersion: result.agentVersion ?? null,
        authMethods: methods,
        authError: null,
      });
    }
  },

  beginAuthenticate: (methodId) => set({ authenticatingMethodId: methodId, authError: null }),

  handleAuthResult: (result) => {
    if (result.error) {
      set({ authenticatingMethodId: null, authError: result.error });
    } else {
      // Auth succeeded — clear the methods so the auto-create-session
      // effect unblocks, and clear any previous error. pendingSession is
      // already set (from the original setConnecting), so the panel
      // resumes whatever the user originally asked for.
      set({
        authenticatingMethodId: null,
        authError: null,
        authMethods: [],
      });
    }
  },

  handleSessionCreated: (result) => {
    if (result.authRequired) {
      // Agent rejected session/new with ACP auth_required. Re-seed the
      // sign-in card from the snapshot the host attached (the initialize
      // response may have advertised methods we cleared after a stale
      // success). Keep pendingSession alive so the auto-create effect
      // re-fires once authenticate succeeds. If the agent didn't advertise
      // any methods we have nothing to drive — surface the agent's error
      // string so the user isn't stuck on a perpetual spinner.
      const methods = result.authMethods ?? [];
      if (methods.length === 0) {
        set({
          connectError: result.error ?? "Authentication required but no methods advertised.",
          pendingSession: null,
        });
      } else {
        set({
          connectError: null,
          authMethods: methods,
          authError: result.error ?? null,
        });
      }
    } else if (result.error) {
      set({ connectError: result.error, pendingSession: null });
    } else if (result.sessionId) {
      // New session id = new auto-approve scope. The user's "always allow"
      // decisions for the previous session don't carry over. Modes and
      // config options come from the agent's session/new response — seed
      // them here so the composer's options popover populates immediately.
      // Some agents (Copilot) advertise authMethods opportunistically even
      // when external auth is already in place. A successful session/new
      // proves auth wasn't actually required, so clear the methods so the
      // sign-in card doesn't linger after the session is live.
      set({
        sessionId: result.sessionId,
        sessionTitle: null,
        currentModeId: result.modes?.currentModeId ?? null,
        availableModes: result.modes?.availableModes ?? [],
        configOptions: result.configOptions ?? [],
        lastStopReason: null,
        messages: [],
        availableCommands: [],
        pendingSession: null,
        authMethods: [],
        authError: null,
        autoApproveKinds: new Set<ToolKind>(),
        autoRejectKinds: new Set<ToolKind>(),
      });
    }
  },

  setConnected: (info) =>
    set({
      connected: true,
      connecting: false,
      connectError: null,
      agentName: info.agentName ?? null,
      agentVersion: info.agentVersion ?? null,
    }),

  setDisconnected: () =>
    set({
      connected: false,
      connecting: false,
      connectError: null,
      agentName: null,
      agentVersion: null,
      currentAgentId: null,
      sessionId: null,
      sessionTitle: null,
      currentModeId: null,
      prompting: false,
      replaying: false,
      pendingSession: null,
      lastStopReason: null,
      availableCommands: [],
      availableModes: [],
      configOptions: [],
      authMethods: [],
      authenticatingMethodId: null,
      authError: null,
      autoApproveKinds: new Set<ToolKind>(),
      autoRejectKinds: new Set<ToolKind>(),
    }),

  setSession: (sessionId) =>
    set({
      sessionId,
      sessionTitle: null,
      currentModeId: null,
      lastStopReason: null,
      messages: [],
      availableCommands: [],
    }),

  beginReplay: (sessionId) =>
    // Optimistically set sessionId so the auto-create-session effect
    // doesn't fire while we're loading. Clear messages so the agent's
    // historical updates land into a clean list. `replaying: true`
    // unlocks the user_message_chunk path so the modder's prior prompts
    // come back into the visible thread. `pendingSession: null` is the
    // important guard — without it, a resume failure would null
    // sessionId and the auto-create effect would silently spawn a fresh
    // session, burying the resume error.
    set({
      sessionId,
      sessionTitle: null,
      currentModeId: null,
      availableModes: [],
      configOptions: [],
      lastStopReason: null,
      messages: [],
      availableCommands: [],
      replaying: true,
      pendingSession: null,
    }),

  handleResumeResult: (result) => {
    if (result.error !== undefined) {
      // Resume failed (likely the agent doesn't support session/load,
      // or the id is unknown). Null sessionId so the panel renders the
      // "no session" empty state with the error visible — but DON'T set
      // pendingSession, otherwise the auto-create effect would
      // immediately mask the error by spawning a new session.
      set({
        replaying: false,
        sessionId: null,
        connectError: result.error,
        pendingSession: null,
      });
    } else {
      // Historical updates have all landed by the time this notification
      // arrives, so we can flip back to live mode. Modes / config options
      // come fresh from session/load — repopulate so the composer's
      // options popover reflects the resumed session's agent state.
      // Persisted overrides (from Studio's session metadata) win over the
      // agent's currentValue: the host has already replayed them via
      // set_mode / set_config_option, so the agent's state matches; the
      // UI just needs to render with the same values.
      const persistedConfig = result.persistedConfigValues ?? null;
      const baseConfig = result.configOptions ?? [];
      const mergedConfig =
        persistedConfig !== null
          ? baseConfig.map((opt) => {
              const id = opt.key ?? opt.id;
              if (id !== null && id !== undefined && id in persistedConfig) {
                return { ...opt, value: persistedConfig[id], currentValue: persistedConfig[id] };
              }
              return opt;
            })
          : baseConfig;

      set({
        replaying: false,
        currentModeId: result.persistedModeId ?? result.modes?.currentModeId ?? null,
        availableModes: result.modes?.availableModes ?? [],
        configOptions: mergedConfig,
      });
    }
  },

  addUserMessage: (text) =>
    set((s) => ({
      messages: [...s.messages, { id: nextMessageId(), role: "user" as const, text }],
    })),

  setPrompting: (prompting) => set({ prompting }),

  clearLastStopReason: () => set({ lastStopReason: null }),

  handlePromptResult: (event) => set({ prompting: false, lastStopReason: event }),

  handleUpdate: (update) =>
    set((s) => {
      // Always produce new objects when modifying a message; the previous
      // render's references stay frozen so consumers comparing by identity
      // (React.memo, strict-mode double invokes) see real changes.
      switch (update.sessionUpdate) {
        case "agent_message_chunk": {
          const text = contentBlockText(update.content);
          const last = s.messages[s.messages.length - 1];
          if (last?.role === "agent") {
            const updatedLast: AgentMessage = { ...last, text: last.text + text };
            return { messages: [...s.messages.slice(0, -1), updatedLast] };
          }
          return {
            messages: [...s.messages, { id: nextMessageId(), role: "agent", text }],
          };
        }

        case "agent_thought_chunk": {
          const text = contentBlockText(update.content);
          const last = s.messages[s.messages.length - 1];
          if (last?.role === "thought") {
            const updatedLast: ThoughtMessage = { ...last, text: last.text + text };
            return { messages: [...s.messages.slice(0, -1), updatedLast] };
          }
          return {
            messages: [...s.messages, { id: nextMessageId(), role: "thought", text }],
          };
        }

        case "user_message_chunk": {
          // For live conversations this is just an echo of the prompt we
          // already pushed via addUserMessage. During a resume replay
          // there's no local addUserMessage call (the prompts predate this
          // session), so we have to materialise them from the chunks.
          if (!s.replaying) return s;
          const text = contentBlockText(update.content);
          const last = s.messages[s.messages.length - 1];
          if (last?.role === "user") {
            const updatedLast: UserMessage = { ...last, text: last.text + text };
            return { messages: [...s.messages.slice(0, -1), updatedLast] };
          }
          return {
            messages: [...s.messages, { id: nextMessageId(), role: "user", text }],
          };
        }

        case "tool_call":
          return {
            messages: [
              ...s.messages,
              {
                id: nextMessageId(),
                role: "tool",
                toolCallId: update.toolCallId,
                toolName: update.title ?? update.kind ?? "tool",
                content: update.content ? [...update.content] : [],
                status: update.status ?? null,
              },
            ],
          };

        case "tool_call_update": {
          // Find the matching tool message (last-wins) and produce a fresh copy.
          for (let i = s.messages.length - 1; i >= 0; i--) {
            const m = s.messages[i];
            if (m?.role === "tool" && m.toolCallId === update.toolCallId) {
              const updatedTool: ToolMessage = {
                ...m,
                content: update.content ? [...m.content, ...update.content] : m.content,
                status: update.status ?? m.status,
                toolName: update.title ?? m.toolName,
              };
              return {
                messages: [...s.messages.slice(0, i), updatedTool, ...s.messages.slice(i + 1)],
              };
            }
          }
          return s;
        }

        case "plan": {
          const planIdx = s.messages.findIndex((m) => m.role === "plan");
          if (planIdx >= 0) {
            const existing = s.messages[planIdx] as PlanMessage;
            const updatedPlan: PlanMessage = { ...existing, entries: update.entries };
            return {
              messages: [
                ...s.messages.slice(0, planIdx),
                updatedPlan,
                ...s.messages.slice(planIdx + 1),
              ],
            };
          }
          return {
            messages: [
              ...s.messages,
              { id: nextMessageId(), role: "plan", entries: update.entries },
            ],
          };
        }

        case "available_commands_update":
          return { availableCommands: update.availableCommands };

        case "current_mode_update":
          return { currentModeId: update.currentModeId };

        case "session_info_update":
          return { sessionTitle: update.title ?? null };

        case "config_option_update":
          // Merge by key so the update works whether the agent sends a
          // full snapshot or a delta with just the changed option(s).
          return { configOptions: mergeConfigOptions(s.configOptions, update.configOptions) };

        default:
          return s;
      }
    }),

  handlePermissionRequest: (request) => {
    // Bypass policy. ACP doesn't natively scope `_always` outcomes; this is
    // the client-side memory:
    //
    //   autoApproveKinds[k]   → bypass-allow requests with kind k
    //   autoRejectKinds[k]    → bypass-reject requests with kind k
    //
    // Reject takes precedence over allow when both are set for the same
    // kind (defensive — the user's last decision wins, and clicking
    // "reject_always" after "allow_always" should be the override).
    // Falls through to the UI prompt when we can't determine the kind or
    // the agent didn't offer the corresponding option. Session-wide
    // "allow all" is the agent's responsibility (Claude exposes a
    // `bypassPermissions` mode; Copilot exposes an `allow_all` config
    // option) — the host doesn't shadow that with its own toggle.
    const s = get();
    const kind = request.toolCall.kind ?? null;
    if (kind !== null && s.autoRejectKinds.has(kind)) {
      const reject = pickRejectOption(request.options);
      if (reject) {
        sendPermissionResponse(request.permissionId, "selected", reject.optionId);
        return;
      }
    }
    if (kind !== null && s.autoApproveKinds.has(kind)) {
      const allow = pickAllowOption(request.options);
      if (allow) {
        sendPermissionResponse(request.permissionId, "selected", allow.optionId);
        return;
      }
    }
    set((prev) => ({
      messages: [
        ...prev.messages,
        {
          id: nextMessageId(),
          role: "permission" as const,
          permissionId: request.permissionId,
          toolCall: request.toolCall,
          options: request.options,
          resolved: false,
        },
      ],
    }));
  },

  resolvePermission: (permissionId) =>
    set((s) => ({
      messages: s.messages.map((m) =>
        m.role === "permission" && m.permissionId === permissionId ? { ...m, resolved: true } : m,
      ),
    })),

  clearMessages: () => set({ messages: [] }),

  addAutoApproveKind: (kind) =>
    set((s) => {
      // Set is treated as immutable from the consumer's perspective; replace
      // wholesale so React/zustand's referential-equality checks notice.
      // Adding to "approve" clears the same kind from "reject" — the most
      // recent click wins.
      const inApprove = s.autoApproveKinds.has(kind);
      const inReject = s.autoRejectKinds.has(kind);
      if (inApprove && !inReject) return s;
      const nextApprove = inApprove ? s.autoApproveKinds : new Set(s.autoApproveKinds);
      if (!inApprove) (nextApprove as Set<ToolKind>).add(kind);
      const nextReject = inReject ? new Set(s.autoRejectKinds) : s.autoRejectKinds;
      if (inReject) (nextReject as Set<ToolKind>).delete(kind);
      return { autoApproveKinds: nextApprove, autoRejectKinds: nextReject };
    }),

  addAutoRejectKind: (kind) =>
    set((s) => {
      const inReject = s.autoRejectKinds.has(kind);
      const inApprove = s.autoApproveKinds.has(kind);
      if (inReject && !inApprove) return s;
      const nextReject = inReject ? s.autoRejectKinds : new Set(s.autoRejectKinds);
      if (!inReject) (nextReject as Set<ToolKind>).add(kind);
      const nextApprove = inApprove ? new Set(s.autoApproveKinds) : s.autoApproveKinds;
      if (inApprove) (nextApprove as Set<ToolKind>).delete(kind);
      return { autoApproveKinds: nextApprove, autoRejectKinds: nextReject };
    }),
}));

/**
 * Fires `agentPermissionResponse` and silently swallows rejections so a
 * disconnected host (test env, or a race against agentStop) doesn't
 * surface as an unhandled promise rejection. Used only by the bypass
 * paths in `handlePermissionRequest`; the user-facing button handler in
 * `PermissionBlock` runs through `agentPermissionResponse` directly so
 * that genuine RPC failures still bubble.
 */
function sendPermissionResponse(
  permissionId: string,
  outcome: "selected" | "cancelled",
  optionId?: string,
): void {
  agentPermissionResponse(permissionId, outcome, optionId).catch(() => undefined);
}

/**
 * Picks the option to send when bypassing a permission prompt. Prefers
 * the `_once` variant (single-shot, the safest default for an automated
 * answer) over `_always` (which would mean "remember this again",
 * redundant when we're already remembering on our side). Returns null
 * when the agent didn't offer either kind.
 */
function pickAllowOption(options: readonly PermissionOption[]): PermissionOption | null {
  return (
    options.find((o) => o.kind === "allow_once") ??
    options.find((o) => o.kind === "allow_always") ??
    null
  );
}

function pickRejectOption(options: readonly PermissionOption[]): PermissionOption | null {
  return (
    options.find((o) => o.kind === "reject_once") ??
    options.find((o) => o.kind === "reject_always") ??
    null
  );
}

// Wire up host notifications.
//
// Host pushes { sessionId, update: { sessionUpdate, ... } } as `agentUpdate`.
// We unwrap the envelope before dispatch.
subscribe("agentUpdate", (params) => {
  const notification = params as SessionNotification;
  useAgentStore.getState().handleUpdate(notification.update);
});

subscribe("agentPermissionRequest", (params) => {
  const request = params as PermissionRequest;
  useAgentStore.getState().handlePermissionRequest(request);
});

subscribe("agentStatus", (params) => {
  const event = params as AgentStatusEvent;
  if (!event.running) {
    useAgentStore.getState().setDisconnected();
  }
});

subscribe("agentPromptResult", (params) => {
  useAgentStore.getState().handlePromptResult(params as AgentPromptResultEvent);
});

subscribe("agentStartResult", (params) => {
  const result = params as AgentStartResult;
  useAgentStore.getState().handleStartResult(result);
});

subscribe("agentAuthenticated", (params) => {
  useAgentStore.getState().handleAuthResult(params as AgentAuthResult);
});

subscribe("agentSessionCreated", (params) => {
  const result = params as AgentSessionResult;
  useAgentStore.getState().handleSessionCreated(result);
});

subscribe("agentSessionResumed", (params) => {
  useAgentStore.getState().handleResumeResult(params as AgentResumeResult);
});
