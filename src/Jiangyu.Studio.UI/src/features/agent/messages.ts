// Chat message discriminated union plus the helpers that shape ACP payloads
// into messages for the store.

import type {
  ConfigOption,
  ConfigOptionChoice,
  ContentBlock,
  PermissionRequest,
  PermissionToolCall,
  PlanEntry,
  SessionConfigChoice,
  SessionConfigOption,
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
  UserMessage | AgentMessage | ThoughtMessage | ToolMessage | PlanMessage | PermissionMessage;

let messageIdCounter = 0;
export function nextMessageId(): string {
  messageIdCounter += 1;
  return `m${messageIdCounter.toString(36)}`;
}

/** Render a config value for display. Returns null when the value has no
 *  obvious flat form (objects, arrays, null). */
export function stringifyValue(value: unknown): string | null {
  if (typeof value === "string") return value;
  if (typeof value === "boolean") return value ? "on" : "off";
  if (typeof value === "number") return String(value);
  return null;
}

/** Collapse the wire's dual spellings into the canonical shape. Agents emit
 *  either `key` or `id`; entries with neither are dropped (we have no way
 *  to address them in set_config_option). */
export function normaliseConfigOption(raw: ConfigOption): SessionConfigOption | null {
  const id = raw.key ?? raw.id ?? null;
  if (id === null) return null;
  return {
    id,
    name: raw.name ?? id,
    description: raw.description ?? null,
    type: raw.type ?? null,
    value: raw.value ?? raw.currentValue,
    choices: (raw.options ?? raw.choices ?? []).map(normaliseConfigChoice),
    min: raw.min ?? null,
    max: raw.max ?? null,
  };
}

export function normaliseConfigOptions(raw: readonly ConfigOption[]): SessionConfigOption[] {
  return raw.flatMap((opt) => {
    const normalised = normaliseConfigOption(opt);
    return normalised === null ? [] : [normalised];
  });
}

function normaliseConfigChoice(raw: ConfigOptionChoice): SessionConfigChoice {
  return {
    value: raw.value,
    label: raw.name ?? raw.label ?? stringifyValue(raw.value) ?? "—",
    description: raw.description ?? null,
  };
}

/** Merge a config_option_update payload into the existing list by id.
 *  New ids append; existing ids overwrite. */
export function mergeConfigOptions(
  existing: readonly SessionConfigOption[],
  incoming: readonly SessionConfigOption[],
): SessionConfigOption[] {
  const byId = new Map<string, SessionConfigOption>();
  for (const opt of existing) byId.set(opt.id, opt);
  for (const opt of incoming) byId.set(opt.id, opt);
  return [...byId.values()];
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
