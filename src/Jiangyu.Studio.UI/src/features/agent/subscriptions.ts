// Host-notification wiring for the agent store. Importing this module for
// side effects registers every `subscribe()` handler exactly once. The store
// imports it so any consumer that pulls in the store also pulls in the
// subscriptions.

import { subscribe } from "@shared/rpc";
import { normaliseConfigOptions } from "./messages";
import { agentPermissionResponse } from "./rpc";
import { useAgentStore, type AgentPromptResultEvent, type AgentResumeResult } from "./store";
import type {
  AgentAuthResult,
  AgentSessionResult,
  AgentStartResult,
  AgentStatusEvent,
  AuthMethod,
  ConfigOption,
  PermissionOption,
  PermissionRequest,
  SessionModes,
  SessionNotification,
  SessionUpdate,
  WireSessionUpdate,
} from "./types";

/** Wire payload of `agentSessionResumed`. Config options arrive in the
 *  agent's raw dual-spelling form; the store-facing AgentResumeResult
 *  carries them normalised. */
interface AgentResumeNotification extends Omit<AgentResumeResult, "configOptions"> {
  readonly configOptions?: ConfigOption[] | null;
}

// The agent feature owns these payload shapes (hand-authored mirrors of the
// host's ACP envelopes in ./types); register them on the shared notification
// map so `subscribe` is typed end to end.
declare module "@shared/rpc/notifications" {
  interface HostNotificationMap {
    agentUpdate: SessionNotification;
    agentPermissionRequest: PermissionRequest;
    agentStatus: AgentStatusEvent;
    agentPromptResult: AgentPromptResultEvent;
    agentStartResult: AgentStartResult;
    agentAuthenticated: AgentAuthResult;
    agentSessionResumed: AgentResumeNotification;
  }
}

/**
 * Fires `agentPermissionResponse` and silently swallows rejections so a
 * disconnected host (test env, or a race against agentStop) doesn't surface
 * as an unhandled promise rejection. Used by the bypass paths in
 * `handlePermissionRequest`; the user-facing button handler in
 * `PermissionBlock` runs through `agentPermissionResponse` directly so
 * genuine RPC failures still bubble.
 */
export function sendPermissionResponse(
  permissionId: string,
  outcome: "selected" | "cancelled",
  optionId?: string,
): void {
  agentPermissionResponse(permissionId, outcome, optionId).catch(() => undefined);
}

/**
 * Picks the option to send when bypassing a permission prompt. Prefers the
 * `_once` variant (single-shot, the safest default for an automated answer)
 * over `_always` (which would mean "remember this again", redundant when we
 * are already remembering on our side). Returns null when the agent didn't
 * offer either kind.
 */
export function pickAllowOption(options: readonly PermissionOption[]): PermissionOption | null {
  return (
    options.find((o) => o.kind === "allow_once") ??
    options.find((o) => o.kind === "allow_always") ??
    null
  );
}

export function pickRejectOption(options: readonly PermissionOption[]): PermissionOption | null {
  return (
    options.find((o) => o.kind === "reject_once") ??
    options.find((o) => o.kind === "reject_always") ??
    null
  );
}

/** Collapse the wire's dual config-option spellings before the store sees
 *  the update; everything else passes through untouched. */
function normaliseSessionUpdate(update: WireSessionUpdate): SessionUpdate {
  if (update.sessionUpdate === "config_option_update") {
    return {
      sessionUpdate: "config_option_update",
      configOptions: normaliseConfigOptions(update.configOptions),
    };
  }
  return update;
}

// --- Streamed-update coalescing ---------------------------------------------
//
// A fast agent emits message chunks far quicker than 60Hz; dispatching each
// one straight into the store re-renders the transcript per chunk. Updates
// are buffered and flushed on a short timer instead, capping render
// frequency while preserving arrival order. Every other agent notification
// flushes the buffer first, so nothing is ever applied out of order
// relative to the update stream.

const FLUSH_INTERVAL_MS = 16;

let pendingUpdates: SessionUpdate[] = [];
let flushTimer: ReturnType<typeof setTimeout> | null = null;

function flushSessionUpdates(): void {
  if (flushTimer !== null) {
    clearTimeout(flushTimer);
    flushTimer = null;
  }
  if (pendingUpdates.length === 0) return;
  const batch = pendingUpdates;
  pendingUpdates = [];
  const store = useAgentStore.getState();
  for (const update of batch) store.handleUpdate(update);
}

// Host pushes { sessionId, update: { sessionUpdate, ... } } as `agentUpdate`.
// We unwrap the envelope before dispatch.
subscribe("agentUpdate", (notification) => {
  pendingUpdates.push(normaliseSessionUpdate(notification.update));
  flushTimer ??= setTimeout(() => {
    flushTimer = null;
    flushSessionUpdates();
  }, FLUSH_INTERVAL_MS);
});

subscribe("agentPermissionRequest", (request) => {
  flushSessionUpdates();
  useAgentStore.getState().handlePermissionRequest(request);
});

subscribe("agentStatus", (event) => {
  flushSessionUpdates();
  if (!event.running) {
    useAgentStore.getState().setDisconnected();
  }
});

subscribe("agentPromptResult", (event) => {
  flushSessionUpdates();
  useAgentStore.getState().handlePromptResult(event);
});

subscribe("agentStartResult", (result) => {
  flushSessionUpdates();
  useAgentStore.getState().handleStartResult(result);
});

subscribe("agentAuthenticated", (result) => {
  flushSessionUpdates();
  useAgentStore.getState().handleAuthResult(result);
});

// Adapt the host's typed notification envelope onto the existing store
// contract. Keeping AgentSessionResult internal-only lets the store logic
// stay unchanged while the host moves to the uniform {ok, error, ...} shape
// every notification now uses.
subscribe("agentSessionCreated", (note) => {
  flushSessionUpdates();
  const result: AgentSessionResult = {
    ...(note.sessionId !== undefined && note.sessionId !== null
      ? { sessionId: note.sessionId }
      : {}),
    modes: (note.modes as SessionModes | undefined) ?? null,
    configOptions: normaliseConfigOptions((note.configOptions as ConfigOption[] | undefined) ?? []),
    error: note.error ?? null,
    authRequired: note.authRequired,
    authMethods: (note.authMethods as AuthMethod[] | undefined) ?? null,
  };
  useAgentStore.getState().handleSessionCreated(result);
});

subscribe("agentSessionResumed", (result) => {
  flushSessionUpdates();
  const { configOptions, ...rest } = result;
  useAgentStore.getState().handleResumeResult({
    ...rest,
    configOptions: configOptions != null ? normaliseConfigOptions(configOptions) : null,
  });
});
