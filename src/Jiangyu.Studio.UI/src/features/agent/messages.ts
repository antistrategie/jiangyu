// Chat message discriminated union plus the helpers that shape ACP payloads
// into messages for the store.

import type {
  ConfigOption,
  ContentBlock,
  PermissionRequest,
  PermissionToolCall,
  PlanEntry,
  ToolCallContent,
} from "./types";

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
export function nextMessageId(): string {
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
export function mergeConfigOptions(
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
export function contentBlockText(block: ContentBlock): string {
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
