import { create } from "zustand";
import { subscribe } from "@lib/rpc";
import type {
  AgentStartResult,
  AgentSessionResult,
  AgentStopReason,
  SessionNotification,
  SessionUpdate,
  PermissionRequest,
  AgentStatusEvent,
  AvailableCommand,
  ContentBlock,
  PlanEntry,
  ToolCallContent,
  PermissionToolCall,
} from "./types";

/// Notification emitted by the host when a prompt turn ends. Carries the
/// spec stop reason or, for host-side failures, an opaque error string.
export interface AgentPromptResultEvent {
  readonly stopReason: AgentStopReason | "error";
  readonly error: string | null;
}

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

  /** Last turn's stop reason, surfaced when not "end_turn". */
  readonly lastStopReason: AgentPromptResultEvent | null;

  // Chat
  readonly messages: ChatMessage[];

  // Slash commands
  readonly availableCommands: AvailableCommand[];

  // Actions
  readonly setConnecting: (agentId: string) => void;
  readonly handleStartResult: (result: AgentStartResult) => void;
  readonly handleSessionCreated: (result: AgentSessionResult) => void;
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
}

export const useAgentStore = create<AgentStore>((set) => ({
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
  lastStopReason: null,
  messages: [],
  availableCommands: [],

  setConnecting: (agentId) =>
    // Switching agents (or first-time connect) puts the panel into a clean
    // "starting up" state. We MUST clear `connected`, `sessionId`, and the
    // session-scoped chat state — otherwise, when the new agent's
    // agentStartResult lands, the auto-create-session effect's deps
    // ([connected, sessionId]) haven't actually changed (connected was
    // already true from the previous agent, sessionId still points at the
    // dead session) and never re-fires. Result: the panel sits on
    // "Creating session…" forever, requiring a second click to reset it
    // manually.
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
      lastStopReason: null,
      messages: [],
      availableCommands: [],
    }),

  handleStartResult: (result) => {
    if (result.error) {
      set({ connecting: false, connectError: result.error, currentAgentId: null });
    } else {
      set({
        connecting: false,
        connectError: null,
        connected: true,
        agentName: result.agentName ?? null,
        agentVersion: result.agentVersion ?? null,
      });
    }
  },

  handleSessionCreated: (result) => {
    if (result.error) {
      set({ connectError: result.error });
    } else if (result.sessionId) {
      set({
        sessionId: result.sessionId,
        sessionTitle: null,
        currentModeId: null,
        lastStopReason: null,
        messages: [],
        availableCommands: [],
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
      lastStopReason: null,
      availableCommands: [],
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

        case "user_message_chunk":
          // Echo of the user's prompt; we already pushed a user message via
          // addUserMessage when the user sent it.
          return s;

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
          // Future: surface in settings UI.
          return s;

        default:
          return s;
      }
    }),

  handlePermissionRequest: (request) =>
    set((s) => ({
      messages: [
        ...s.messages,
        {
          id: nextMessageId(),
          role: "permission" as const,
          permissionId: request.permissionId,
          toolCall: request.toolCall,
          options: request.options,
          resolved: false,
        },
      ],
    })),

  resolvePermission: (permissionId) =>
    set((s) => ({
      messages: s.messages.map((m) =>
        m.role === "permission" && m.permissionId === permissionId ? { ...m, resolved: true } : m,
      ),
    })),

  clearMessages: () => set({ messages: [] }),
}));

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

subscribe("agentSessionCreated", (params) => {
  const result = params as AgentSessionResult;
  useAgentStore.getState().handleSessionCreated(result);
});
