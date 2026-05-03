import { rpcCall } from "@lib/rpc";

/** Fire-and-forget; result arrives as `agentStartResult` notification. */
export function agentStart(command: string, args?: string[]): Promise<{ accepted: boolean }> {
  return rpcCall<{ accepted: boolean }>("agentStart", { command, args });
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
