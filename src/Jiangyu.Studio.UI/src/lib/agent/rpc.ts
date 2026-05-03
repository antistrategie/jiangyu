import { rpcCall, type AgentSessionsFile } from "@lib/rpc";

/** Fire-and-forget; result arrives as `agentStartResult` notification.
 *  `agentId` is the InstalledAgent.id so the host can record which agent
 *  the session belongs to, enabling the history popover to route resumes
 *  to the right subprocess. */
export function agentStart(
  command: string,
  args?: string[],
  agentId?: string,
): Promise<{ accepted: boolean }> {
  return rpcCall<{ accepted: boolean }>("agentStart", { command, args, agentId });
}

export function agentStop(): Promise<unknown> {
  return rpcCall("agentStop");
}

/** Fire-and-forget; result arrives as `agentSessionCreated` notification. */
export function agentSessionCreate(): Promise<{ accepted: boolean }> {
  return rpcCall<{ accepted: boolean }>("agentSessionCreate");
}

/**
 * Submit a prompt. Resolves immediately with an ack; the final stopReason
 * arrives later as an `agentPromptResult` notification handled by the
 * agent store. This decoupling lets `agentSessionCancel` reach the host
 * even while a long prompt is in flight.
 */
export function agentSessionPrompt(text: string): Promise<{ accepted: boolean }> {
  return rpcCall<{ accepted: boolean }>("agentSessionPrompt", { text });
}

export function agentSessionCancel(): Promise<unknown> {
  return rpcCall("agentSessionCancel");
}

export function agentSessionClose(): Promise<unknown> {
  return rpcCall("agentSessionClose");
}

export function agentPermissionResponse(
  permissionId: string,
  outcome: "selected" | "cancelled",
  optionId?: string,
): Promise<unknown> {
  return rpcCall("agentPermissionResponse", { permissionId, outcome, optionId });
}

/** Returns the project's session metadata index (history popover source). */
export function agentSessionsList(): Promise<AgentSessionsFile> {
  return rpcCall<AgentSessionsFile>("agentSessionsList");
}

/** Removes a session metadata entry. The agent's own session storage is
 *  unaffected; only the local index forgets it. */
export function agentSessionDelete(sessionId: string): Promise<AgentSessionsFile> {
  return rpcCall<AgentSessionsFile>("agentSessionDelete", { sessionId });
}

/** Fire-and-forget; the resumed session id (or an error) arrives via the
 *  `agentSessionResumed` notification once the agent has streamed all
 *  historical updates. */
export function agentSessionResume(sessionId: string): Promise<{ accepted: boolean }> {
  return rpcCall<{ accepted: boolean }>("agentSessionResume", { sessionId });
}
