// Agent protocol types. These mirror the ACP schema types defined in C#
// (Jiangyu.Acp.Schema) and the notification shapes from RpcDispatcher.Agent.cs.
// Hand-authored because the ACP update stream is polymorphic and the generated
// RPC types only cover simple request/response shapes.

/** Result of agentStart RPC (now arrives as `agentStartResult` notification). */
export interface AgentStartResult {
  readonly agentName?: string | null;
  readonly agentVersion?: string | null;
  readonly protocolVersion?: number | null;
  readonly authMethods?: AuthMethod[] | null;
  readonly error?: string | null;
}

export interface AuthMethod {
  readonly id: string;
  readonly name: string;
  readonly description?: string | null;
}

/** Result of agentSessionCreate (now arrives as `agentSessionCreated` notification). */
export interface AgentSessionResult {
  readonly sessionId?: string;
  readonly modes?: SessionModes | null;
  readonly configOptions?: ConfigOption[] | null;
  readonly error?: string | null;
  /** Set when the agent rejected session/new with ACP's auth_required
   *  error (-32000). The host attaches the latest authMethods snapshot
   *  from initialize so the panel can render a sign-in card without an
   *  extra round-trip. */
  readonly authRequired?: boolean;
  readonly authMethods?: AuthMethod[] | null;
}

/** Result of agentAuthenticate (arrives as `agentAuthenticated` notification). */
export interface AgentAuthResult {
  readonly methodId: string;
  readonly error?: string | null;
}

/** Per ACP, agent-declared operating modes for the session
 *  (e.g. Claude's "default" / "plan" / "explain"). */
export interface SessionModes {
  readonly currentModeId?: string | null;
  readonly availableModes: readonly SessionMode[];
}

export interface SessionMode {
  readonly id?: string | null;
  readonly name?: string | null;
  readonly description?: string | null;
}

/** One agent-tunable knob (model selection, thinking budget, etc.). All
 *  fields are optional because real-world agents emit varying shapes —
 *  Claude Agent uses `type` + `currentValue` without a stable `key`;
 *  others use `id` + `value`. The UI reads `key ?? id` and
 *  `value ?? currentValue`; entries without any identifier are dropped. */
export interface ConfigOption {
  readonly key?: string | null;
  readonly id?: string | null;
  readonly name?: string | null;
  readonly description?: string | null;
  readonly type?: string | null;
  readonly value?: unknown;
  readonly currentValue?: unknown;
  /** Copilot's spelling. Use whichever is set; UI reads `options ?? choices`. */
  readonly options?: readonly ConfigOptionChoice[] | null;
  readonly choices?: readonly ConfigOptionChoice[] | null;
  readonly min?: number | null;
  readonly max?: number | null;
  readonly category?: string | null;
}

export interface ConfigOptionChoice {
  readonly value?: unknown;
  /** Copilot uses `name`; some specs use `label`. UI reads `name ?? label`. */
  readonly name?: string | null;
  readonly label?: string | null;
  readonly description?: string | null;
}

/** Result of agentSessionPrompt RPC. Spec stop reasons:
 *  end_turn | max_tokens | max_turn_requests | refusal | cancelled. */
export interface AgentPromptResult {
  readonly stopReason: AgentStopReason;
}

export type AgentStopReason =
  | "end_turn"
  | "max_tokens"
  | "max_turn_requests"
  | "refusal"
  | "cancelled";

// --- Session update notifications (pushed via "agentUpdate") ---
//
// Wire shape: { sessionId, update: SessionUpdate }
// The store unwraps the outer envelope and dispatches on update.sessionUpdate.

export interface SessionNotification {
  readonly sessionId: string;
  readonly update: SessionUpdate;
}

export type SessionUpdate =
  | AgentMessageChunk
  | UserMessageChunk
  | AgentThoughtChunk
  | ToolCallStartUpdate
  | ToolCallProgressUpdate
  | PlanUpdate
  | AvailableCommandsUpdate
  | CurrentModeUpdate
  | ConfigOptionUpdate
  | SessionInfoUpdate;

export interface AgentMessageChunk {
  readonly sessionUpdate: "agent_message_chunk";
  readonly content: ContentBlock;
}

export interface UserMessageChunk {
  readonly sessionUpdate: "user_message_chunk";
  readonly content: ContentBlock;
}

export interface AgentThoughtChunk {
  readonly sessionUpdate: "agent_thought_chunk";
  readonly content: ContentBlock;
}

export interface ToolCallStartUpdate {
  readonly sessionUpdate: "tool_call";
  readonly toolCallId: string;
  readonly title?: string | null;
  readonly content?: ToolCallContent[] | null;
  readonly kind?: ToolKind | null;
  readonly status?: ToolCallStatus | null;
  readonly locations?: ToolCallLocation[] | null;
  readonly rawInput?: unknown;
  readonly rawOutput?: unknown;
}

export interface ToolCallProgressUpdate {
  readonly sessionUpdate: "tool_call_update";
  readonly toolCallId: string;
  readonly title?: string | null;
  readonly content?: ToolCallContent[] | null;
  readonly kind?: ToolKind | null;
  readonly status?: ToolCallStatus | null;
  readonly locations?: ToolCallLocation[] | null;
  readonly rawInput?: unknown;
  readonly rawOutput?: unknown;
}

export interface PlanUpdate {
  readonly sessionUpdate: "plan";
  readonly entries: PlanEntry[];
}

export interface AvailableCommandsUpdate {
  readonly sessionUpdate: "available_commands_update";
  readonly availableCommands: AvailableCommand[];
}

export interface CurrentModeUpdate {
  readonly sessionUpdate: "current_mode_update";
  readonly currentModeId: string;
}

export interface ConfigOptionUpdate {
  readonly sessionUpdate: "config_option_update";
  readonly configOptions: readonly ConfigOption[];
}

export interface SessionInfoUpdate {
  readonly sessionUpdate: "session_info_update";
  readonly title?: string | null;
}

// --- Content blocks (used in prompts and message chunks) ---

export type ContentBlock =
  | TextContentBlock
  | ImageContentBlock
  | AudioContentBlock
  | ResourceLinkContentBlock;

export interface TextContentBlock {
  readonly type: "text";
  readonly text: string;
}

export interface ImageContentBlock {
  readonly type: "image";
  readonly data: string;
  readonly mimeType: string;
  readonly uri?: string | null;
}

export interface AudioContentBlock {
  readonly type: "audio";
  readonly data: string;
  readonly mimeType: string;
}

export interface ResourceLinkContentBlock {
  readonly type: "resource_link";
  readonly name: string;
  readonly uri: string;
  readonly title?: string | null;
  readonly description?: string | null;
  readonly mimeType?: string | null;
  readonly size?: number | null;
}

// --- Tool call content ---

export type ToolCallContent = ContentToolCallContent | DiffContent | TerminalContent;

export interface ContentToolCallContent {
  readonly type: "content";
  readonly content: ContentBlock;
}

export interface DiffContent {
  readonly type: "diff";
  readonly path: string;
  readonly newText: string;
  readonly oldText?: string | null;
}

export interface TerminalContent {
  readonly type: "terminal";
  readonly terminalId: string;
}

export type ToolKind =
  | "read"
  | "edit"
  | "delete"
  | "move"
  | "search"
  | "execute"
  | "think"
  | "fetch"
  | "switch_mode"
  | "other";

export type ToolCallStatus = "pending" | "in_progress" | "completed" | "failed";

export interface ToolCallLocation {
  readonly path: string;
  readonly line?: number | null;
}

export interface PlanEntry {
  readonly content: string;
  readonly status: "pending" | "in_progress" | "completed";
  readonly priority: "high" | "medium" | "low";
}

export interface AvailableCommand {
  readonly name: string;
  readonly description: string;
  readonly input?: unknown;
}

// --- Permission request notification (pushed via "agentPermissionRequest") ---
//
// Wire shape from the host: { permissionId, sessionId, toolCall, options }
// The host generates a permissionId (the toolCall's own id) so the frontend
// can correlate the response.

export interface PermissionRequest {
  readonly permissionId: string;
  readonly sessionId: string;
  readonly toolCall: PermissionToolCall;
  readonly options: PermissionOption[];
}

export interface PermissionToolCall {
  readonly toolCallId: string;
  readonly title?: string | null;
  readonly content?: ToolCallContent[] | null;
  readonly kind?: ToolKind | null;
  readonly status?: ToolCallStatus | null;
  readonly locations?: ToolCallLocation[] | null;
  readonly rawInput?: unknown;
  readonly rawOutput?: unknown;
}

export interface PermissionOption {
  readonly optionId: string;
  readonly name: string;
  readonly kind: "allow_once" | "allow_always" | "reject_once" | "reject_always";
}

// --- Agent status notification (pushed via "agentStatus") ---

export interface AgentStatusEvent {
  readonly running: boolean;
}
