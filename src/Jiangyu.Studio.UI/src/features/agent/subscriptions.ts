// Host-notification wiring for the agent store. Importing this module for
// side effects registers every `subscribe()` handler exactly once. The store
// imports it so any consumer that pulls in the store also pulls in the
// subscriptions.

import { subscribe, type AgentSessionCreatedNotification } from "@shared/rpc";
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
} from "./types";

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

// Adapt the host's typed notification envelope onto the existing store
// contract. Keeping AgentSessionResult internal-only lets the store logic
// stay unchanged while the host moves to the uniform {ok, error, ...} shape
// every notification now uses.
subscribe("agentSessionCreated", (params) => {
  const note = params as AgentSessionCreatedNotification;
  const result: AgentSessionResult = {
    ...(note.sessionId !== undefined && note.sessionId !== null
      ? { sessionId: note.sessionId }
      : {}),
    modes: (note.modes as SessionModes | undefined) ?? null,
    configOptions: (note.configOptions as ConfigOption[] | undefined) ?? null,
    error: note.error ?? null,
    authRequired: note.authRequired,
    authMethods: (note.authMethods as AuthMethod[] | undefined) ?? null,
  };
  useAgentStore.getState().handleSessionCreated(result);
});

subscribe("agentSessionResumed", (params) => {
  useAgentStore.getState().handleResumeResult(params as AgentResumeResult);
});
