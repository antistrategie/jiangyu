import { beforeEach, describe, expect, it, vi } from "vitest";

// Mock the permission-response RPC so the bypass path can fire without a
// real host. Defined before the store import so the store's
// `agentPermissionResponse` resolves to the mock.
const permissionResponseMock = vi.fn(() => Promise.resolve());
vi.mock("./rpc", () => ({
  agentPermissionResponse: (...args: unknown[]) => permissionResponseMock(...args),
}));

import { useAgentStore, type ChatMessage } from "./store";
import type {
  AgentMessageChunk,
  AgentThoughtChunk,
  ToolCallStartUpdate,
  ToolCallProgressUpdate,
  PlanUpdate,
  AvailableCommandsUpdate,
  SessionInfoUpdate,
  CurrentModeUpdate,
} from "./types";

function reset() {
  useAgentStore.setState({
    connected: false,
    agentName: null,
    agentVersion: null,
    sessionId: null,
    sessionTitle: null,
    currentModeId: null,
    prompting: false,
    replaying: false,
    pendingSession: null,
    connectError: null,
    lastStopReason: null,
    messages: [],
    availableCommands: [],
    autoApproveKinds: new Set(),
    autoRejectKinds: new Set(),
    trustAll: false,
  });
}

beforeEach(reset);

describe("connection lifecycle", () => {
  it("sets connected state from agent info", () => {
    useAgentStore.getState().setConnected({
      agentName: "claude",
      agentVersion: "1.0",
      protocolVersion: 1,
    });
    const s = useAgentStore.getState();
    expect(s.connected).toBe(true);
    expect(s.agentName).toBe("claude");
    expect(s.agentVersion).toBe("1.0");
  });

  it("clears state on disconnect", () => {
    useAgentStore.getState().setConnected({ agentName: "claude" });
    useAgentStore.getState().setSession("s-1");
    useAgentStore.getState().setDisconnected();
    const s = useAgentStore.getState();
    expect(s.connected).toBe(false);
    expect(s.agentName).toBeNull();
    expect(s.sessionId).toBeNull();
    expect(s.prompting).toBe(false);
  });
});

describe("session", () => {
  it("sets session id and clears messages", () => {
    useAgentStore.getState().addUserMessage("hello");
    expect(useAgentStore.getState().messages).toHaveLength(1);

    useAgentStore.getState().setSession("s-1");
    expect(useAgentStore.getState().sessionId).toBe("s-1");
    expect(useAgentStore.getState().messages).toHaveLength(0);
  });
});

describe("user messages", () => {
  it("appends a user message", () => {
    useAgentStore.getState().addUserMessage("hello agent");
    const msgs = useAgentStore.getState().messages;
    expect(msgs).toHaveLength(1);
    expect(msgs[0]?.role).toBe("user");
    expect((msgs[0] as { text: string }).text).toBe("hello agent");
  });
});

describe("handleUpdate", () => {
  it("appends a new agent message on first chunk", () => {
    const chunk: AgentMessageChunk = {
      sessionUpdate: "agent_message_chunk",
      content: { type: "text", text: "Hello" },
    };
    useAgentStore.getState().handleUpdate(chunk);
    const msgs = useAgentStore.getState().messages;
    expect(msgs).toHaveLength(1);
    expect(msgs[0]?.role).toBe("agent");
    expect((msgs[0] as { text: string }).text).toBe("Hello");
  });

  it("concatenates consecutive agent message chunks", () => {
    const chunk1: AgentMessageChunk = {
      sessionUpdate: "agent_message_chunk",
      content: { type: "text", text: "Hello " },
    };
    const chunk2: AgentMessageChunk = {
      sessionUpdate: "agent_message_chunk",
      content: { type: "text", text: "world" },
    };
    useAgentStore.getState().handleUpdate(chunk1);
    useAgentStore.getState().handleUpdate(chunk2);
    const msgs = useAgentStore.getState().messages;
    expect(msgs).toHaveLength(1);
    expect((msgs[0] as { text: string }).text).toBe("Hello world");
  });

  it("creates a separate message when a thought follows an agent message", () => {
    const agentChunk: AgentMessageChunk = {
      sessionUpdate: "agent_message_chunk",
      content: { type: "text", text: "response" },
    };
    const thoughtChunk: AgentThoughtChunk = {
      sessionUpdate: "agent_thought_chunk",
      content: { type: "text", text: "thinking..." },
    };
    useAgentStore.getState().handleUpdate(agentChunk);
    useAgentStore.getState().handleUpdate(thoughtChunk);
    const msgs = useAgentStore.getState().messages;
    expect(msgs).toHaveLength(2);
    expect(msgs[0]?.role).toBe("agent");
    expect(msgs[1]?.role).toBe("thought");
  });

  it("creates a fresh agent message when a tool call interleaves chunks", () => {
    useAgentStore.getState().handleUpdate({
      sessionUpdate: "agent_message_chunk",
      content: { type: "text", text: "Hello " },
    });
    useAgentStore.getState().handleUpdate({
      sessionUpdate: "tool_call",
      toolCallId: "tc-1",
      title: "readFile",
    });
    useAgentStore.getState().handleUpdate({
      sessionUpdate: "agent_message_chunk",
      content: { type: "text", text: "world" },
    });
    const msgs = useAgentStore.getState().messages;
    expect(msgs).toHaveLength(3);
    expect(msgs[0]?.role).toBe("agent");
    expect(msgs[1]?.role).toBe("tool");
    expect(msgs[2]?.role).toBe("agent");
    expect((msgs[2] as { text: string }).text).toBe("world");
  });

  it("concatenates consecutive thought chunks", () => {
    const t1: AgentThoughtChunk = {
      sessionUpdate: "agent_thought_chunk",
      content: { type: "text", text: "hmm " },
    };
    const t2: AgentThoughtChunk = {
      sessionUpdate: "agent_thought_chunk",
      content: { type: "text", text: "interesting" },
    };
    useAgentStore.getState().handleUpdate(t1);
    useAgentStore.getState().handleUpdate(t2);
    const msgs = useAgentStore.getState().messages;
    expect(msgs).toHaveLength(1);
    expect((msgs[0] as { text: string }).text).toBe("hmm interesting");
  });

  it("tracks tool call start", () => {
    const start: ToolCallStartUpdate = {
      sessionUpdate: "tool_call",
      toolCallId: "tc-1",
      title: "readFile",
    };
    useAgentStore.getState().handleUpdate(start);
    const msgs = useAgentStore.getState().messages;
    expect(msgs).toHaveLength(1);
    expect(msgs[0]?.role).toBe("tool");
    const tool = msgs[0] as ChatMessage & { role: "tool" };
    expect(tool.toolCallId).toBe("tc-1");
    expect(tool.toolName).toBe("readFile");
    expect(tool.content).toHaveLength(0);
  });

  it("appends content to matching tool call", () => {
    const start: ToolCallStartUpdate = {
      sessionUpdate: "tool_call",
      toolCallId: "tc-1",
      title: "readFile",
    };
    const update: ToolCallProgressUpdate = {
      sessionUpdate: "tool_call_update",
      toolCallId: "tc-1",
      content: [{ type: "content", content: { type: "text", text: "file contents" } }],
    };
    useAgentStore.getState().handleUpdate(start);
    useAgentStore.getState().handleUpdate(update);
    const tool = useAgentStore.getState().messages[0] as ChatMessage & { role: "tool" };
    expect(tool.content).toHaveLength(1);
    expect(tool.content[0]?.type).toBe("content");
  });

  it("replaces plan in-place on subsequent plan updates", () => {
    const plan1: PlanUpdate = {
      sessionUpdate: "plan",
      entries: [{ content: "Step 1", status: "pending", priority: "medium" }],
    };
    const plan2: PlanUpdate = {
      sessionUpdate: "plan",
      entries: [
        { content: "Step 1", status: "completed", priority: "medium" },
        { content: "Step 2", status: "in_progress", priority: "high" },
      ],
    };
    useAgentStore.getState().handleUpdate(plan1);
    useAgentStore.getState().handleUpdate(plan2);
    const msgs = useAgentStore.getState().messages;
    expect(msgs).toHaveLength(1);
    const plan = msgs[0] as ChatMessage & { role: "plan" };
    expect(plan.entries).toHaveLength(2);
    expect(plan.entries[0]?.status).toBe("completed");
  });

  it("updates available commands", () => {
    const update: AvailableCommandsUpdate = {
      sessionUpdate: "available_commands_update",
      availableCommands: [{ name: "/web", description: "Search the web" }],
    };
    useAgentStore.getState().handleUpdate(update);
    expect(useAgentStore.getState().availableCommands).toHaveLength(1);
    expect(useAgentStore.getState().availableCommands[0]?.name).toBe("/web");
  });

  it("records session info title", () => {
    const update: SessionInfoUpdate = {
      sessionUpdate: "session_info_update",
      title: "My conversation",
    };
    useAgentStore.getState().handleUpdate(update);
    expect(useAgentStore.getState().sessionTitle).toBe("My conversation");
    expect(useAgentStore.getState().messages).toHaveLength(0);
  });

  it("records current mode", () => {
    const update: CurrentModeUpdate = {
      sessionUpdate: "current_mode_update",
      currentModeId: "plan",
    };
    useAgentStore.getState().handleUpdate(update);
    expect(useAgentStore.getState().currentModeId).toBe("plan");
  });
});

describe("permissions", () => {
  beforeEach(() => permissionResponseMock.mockClear());

  it("adds a permission message", () => {
    useAgentStore.getState().handlePermissionRequest({
      permissionId: "tc-1",
      sessionId: "s-1",
      toolCall: { toolCallId: "tc-1", title: "Write to file.ts" },
      options: [{ optionId: "allow_once", name: "Allow", kind: "allow_once" as const }],
    });
    const msgs = useAgentStore.getState().messages;
    expect(msgs).toHaveLength(1);
    expect(msgs[0]?.role).toBe("permission");
    const perm = msgs[0] as ChatMessage & { role: "permission" };
    expect(perm.resolved).toBe(false);
    expect(perm.toolCall.toolCallId).toBe("tc-1");
  });

  it("resolves a permission by id", () => {
    useAgentStore.getState().handlePermissionRequest({
      permissionId: "tc-1",
      sessionId: "s-1",
      toolCall: { toolCallId: "tc-1", title: "Write to file.ts" },
      options: [{ optionId: "allow_once", name: "Allow", kind: "allow_once" as const }],
    });
    useAgentStore.getState().resolvePermission("tc-1");
    const perm = useAgentStore.getState().messages[0] as ChatMessage & { role: "permission" };
    expect(perm.resolved).toBe(true);
  });
});

describe("auto-approve", () => {
  beforeEach(() => permissionResponseMock.mockClear());

  function fireRequest(toolCallId: string, kind?: "edit" | "execute") {
    useAgentStore.getState().handlePermissionRequest({
      permissionId: toolCallId,
      sessionId: "s-1",
      toolCall: kind ? { toolCallId, kind, title: "x" } : { toolCallId, title: "x" },
      options: [
        { optionId: "ao", name: "Allow once", kind: "allow_once" as const },
        { optionId: "aa", name: "Always allow", kind: "allow_always" as const },
        { optionId: "ro", name: "Reject", kind: "reject_once" as const },
      ],
    });
  }

  it("trustAll bypasses the prompt and fires the allow option", () => {
    useAgentStore.getState().setTrustAll(true);
    fireRequest("tc-1", "edit");
    // No UI message added; RPC called with the agent's allow_once option.
    expect(useAgentStore.getState().messages).toHaveLength(0);
    expect(permissionResponseMock).toHaveBeenCalledWith("tc-1", "selected", "ao");
  });

  it("autoApproveKinds bypasses for the matching kind only", () => {
    useAgentStore.getState().addAutoApproveKind("edit");
    fireRequest("tc-edit", "edit");
    fireRequest("tc-execute", "execute");
    const msgs = useAgentStore.getState().messages;
    // edit was bypassed; execute prompted.
    expect(msgs).toHaveLength(1);
    expect((msgs[0] as ChatMessage & { role: "permission" }).permissionId).toBe("tc-execute");
    expect(permissionResponseMock).toHaveBeenCalledOnce();
    expect(permissionResponseMock).toHaveBeenCalledWith("tc-edit", "selected", "ao");
  });

  it("does not bypass when neither flag is set", () => {
    fireRequest("tc-1", "edit");
    expect(useAgentStore.getState().messages).toHaveLength(1);
    expect(permissionResponseMock).not.toHaveBeenCalled();
  });

  it("does not bypass when the tool call has no kind", () => {
    useAgentStore.getState().addAutoApproveKind("edit");
    fireRequest("tc-1"); // kind omitted
    expect(useAgentStore.getState().messages).toHaveLength(1);
    expect(permissionResponseMock).not.toHaveBeenCalled();
  });

  it("falls back to the UI prompt when the agent offers no allow option", () => {
    useAgentStore.getState().setTrustAll(true);
    useAgentStore.getState().handlePermissionRequest({
      permissionId: "tc-1",
      sessionId: "s-1",
      toolCall: { toolCallId: "tc-1", title: "x", kind: "edit" },
      options: [{ optionId: "ro", name: "Reject", kind: "reject_once" as const }],
    });
    // Bypass tried but no allow option present, so the prompt still surfaces.
    expect(useAgentStore.getState().messages).toHaveLength(1);
    expect(permissionResponseMock).not.toHaveBeenCalled();
  });

  it("addAutoApproveKind is idempotent and produces a new Set reference", () => {
    const before = useAgentStore.getState().autoApproveKinds;
    useAgentStore.getState().addAutoApproveKind("edit");
    const after1 = useAgentStore.getState().autoApproveKinds;
    expect(after1).not.toBe(before);
    expect(after1.has("edit")).toBe(true);

    useAgentStore.getState().addAutoApproveKind("edit");
    const after2 = useAgentStore.getState().autoApproveKinds;
    // Idempotent: same kind a second time is a no-op so the reference is
    // preserved (no spurious re-renders).
    expect(after2).toBe(after1);
  });

  it("setConnecting clears both autoApproveKinds and trustAll", () => {
    useAgentStore.getState().setTrustAll(true);
    useAgentStore.getState().addAutoApproveKind("edit");
    useAgentStore.getState().setConnecting("agent-1");
    const s = useAgentStore.getState();
    expect(s.trustAll).toBe(false);
    expect(s.autoApproveKinds.size).toBe(0);
  });

  it("setDisconnected clears both autoApproveKinds and trustAll", () => {
    useAgentStore.getState().setTrustAll(true);
    useAgentStore.getState().addAutoApproveKind("edit");
    useAgentStore.getState().setDisconnected();
    const s = useAgentStore.getState();
    expect(s.trustAll).toBe(false);
    expect(s.autoApproveKinds.size).toBe(0);
  });

  it("handleSessionCreated clears both autoApproveKinds and trustAll", () => {
    useAgentStore.getState().setTrustAll(true);
    useAgentStore.getState().addAutoApproveKind("edit");
    useAgentStore.getState().handleSessionCreated({ sessionId: "fresh" });
    const s = useAgentStore.getState();
    expect(s.trustAll).toBe(false);
    expect(s.autoApproveKinds.size).toBe(0);
  });

  it("autoRejectKinds bypasses with the reject option", () => {
    useAgentStore.getState().addAutoRejectKind("delete");
    useAgentStore.getState().handlePermissionRequest({
      permissionId: "tc-1",
      sessionId: "s-1",
      toolCall: { toolCallId: "tc-1", kind: "delete", title: "Delete file" },
      options: [
        { optionId: "ao", name: "Allow", kind: "allow_once" as const },
        { optionId: "ro", name: "Reject", kind: "reject_once" as const },
      ],
    });
    expect(useAgentStore.getState().messages).toHaveLength(0);
    expect(permissionResponseMock).toHaveBeenCalledWith("tc-1", "selected", "ro");
  });

  it("auto-reject takes precedence over auto-allow when both are set", () => {
    // Last-decision-wins is enforced by addAutoApproveKind / addAutoRejectKind
    // toggling each other off, but the bypass logic also protects against
    // a stale state where both happen to be set: reject wins.
    useAgentStore.setState({
      autoApproveKinds: new Set(["edit"]),
      autoRejectKinds: new Set(["edit"]),
    });
    useAgentStore.getState().handlePermissionRequest({
      permissionId: "tc-1",
      sessionId: "s-1",
      toolCall: { toolCallId: "tc-1", kind: "edit", title: "x" },
      options: [
        { optionId: "ao", name: "Allow", kind: "allow_once" as const },
        { optionId: "ro", name: "Reject", kind: "reject_once" as const },
      ],
    });
    expect(permissionResponseMock).toHaveBeenCalledWith("tc-1", "selected", "ro");
  });

  it("addAutoRejectKind clears the same kind from autoApproveKinds", () => {
    useAgentStore.getState().addAutoApproveKind("edit");
    expect(useAgentStore.getState().autoApproveKinds.has("edit")).toBe(true);
    useAgentStore.getState().addAutoRejectKind("edit");
    const s = useAgentStore.getState();
    expect(s.autoApproveKinds.has("edit")).toBe(false);
    expect(s.autoRejectKinds.has("edit")).toBe(true);
  });

  it("addAutoApproveKind clears the same kind from autoRejectKinds", () => {
    useAgentStore.getState().addAutoRejectKind("edit");
    expect(useAgentStore.getState().autoRejectKinds.has("edit")).toBe(true);
    useAgentStore.getState().addAutoApproveKind("edit");
    const s = useAgentStore.getState();
    expect(s.autoRejectKinds.has("edit")).toBe(false);
    expect(s.autoApproveKinds.has("edit")).toBe(true);
  });

  it("trustAll does not bypass-reject; per-kind reject still does", () => {
    // trustAll is the "auto-allow everything" master switch; it shouldn't
    // override a per-kind reject the user explicitly chose. Reject runs
    // first in the bypass chain.
    useAgentStore.getState().setTrustAll(true);
    useAgentStore.getState().addAutoRejectKind("delete");
    useAgentStore.getState().handlePermissionRequest({
      permissionId: "tc-1",
      sessionId: "s-1",
      toolCall: { toolCallId: "tc-1", kind: "delete", title: "x" },
      options: [
        { optionId: "ao", name: "Allow", kind: "allow_once" as const },
        { optionId: "ro", name: "Reject", kind: "reject_once" as const },
      ],
    });
    expect(permissionResponseMock).toHaveBeenCalledWith("tc-1", "selected", "ro");
  });
});

describe("prompt result", () => {
  it("clears prompting and records the stop reason", () => {
    useAgentStore.setState({ prompting: true });
    useAgentStore.getState().handlePromptResult({ stopReason: "end_turn", error: null });
    const s = useAgentStore.getState();
    expect(s.prompting).toBe(false);
    expect(s.lastStopReason?.stopReason).toBe("end_turn");
  });

  it("records errors", () => {
    useAgentStore.setState({ prompting: true });
    useAgentStore.getState().handlePromptResult({ stopReason: "error", error: "agent died" });
    expect(useAgentStore.getState().lastStopReason?.error).toBe("agent died");
  });

  it("clearLastStopReason wipes the banner", () => {
    useAgentStore.getState().handlePromptResult({ stopReason: "refusal", error: null });
    useAgentStore.getState().clearLastStopReason();
    expect(useAgentStore.getState().lastStopReason).toBeNull();
  });
});

describe("clearMessages", () => {
  it("empties the message list", () => {
    useAgentStore.getState().addUserMessage("hello");
    useAgentStore.getState().clearMessages();
    expect(useAgentStore.getState().messages).toHaveLength(0);
  });
});

describe("session resume", () => {
  it("beginReplay resets the message list, sets sessionId, and flips replaying on", () => {
    useAgentStore.getState().addUserMessage("stale");
    useAgentStore.setState({ sessionId: "old", sessionTitle: "Old", replaying: false });

    useAgentStore.getState().beginReplay("resume-target");

    const s = useAgentStore.getState();
    expect(s.sessionId).toBe("resume-target");
    expect(s.sessionTitle).toBeNull();
    expect(s.messages).toHaveLength(0);
    expect(s.replaying).toBe(true);
  });

  it("user_message_chunk appends during replay (resume re-stream)", () => {
    useAgentStore.getState().beginReplay("resumed");

    useAgentStore.getState().handleUpdate({
      sessionUpdate: "user_message_chunk",
      content: { type: "text", text: "what does this template do?" },
    });

    const messages = useAgentStore.getState().messages;
    expect(messages).toHaveLength(1);
    expect(messages[0]?.role).toBe("user");
    expect(messages[0]?.role === "user" && messages[0].text).toBe("what does this template do?");
  });

  it("user_message_chunk concatenates onto an in-progress user message during replay", () => {
    useAgentStore.getState().beginReplay("resumed");

    useAgentStore.getState().handleUpdate({
      sessionUpdate: "user_message_chunk",
      content: { type: "text", text: "make darby " },
    });
    useAgentStore.getState().handleUpdate({
      sessionUpdate: "user_message_chunk",
      content: { type: "text", text: "more accurate" },
    });

    const messages = useAgentStore.getState().messages;
    expect(messages).toHaveLength(1);
    expect(messages[0]?.role === "user" && messages[0].text).toBe("make darby more accurate");
  });

  it("user_message_chunk is ignored in live mode (already pushed via addUserMessage)", () => {
    // replaying defaults to false in the reset() above.
    useAgentStore.getState().handleUpdate({
      sessionUpdate: "user_message_chunk",
      content: { type: "text", text: "echo" },
    });

    expect(useAgentStore.getState().messages).toHaveLength(0);
  });

  it("handleResumeResult success flips replaying off", () => {
    useAgentStore.getState().beginReplay("resumed");
    useAgentStore.getState().handleResumeResult({ sessionId: "resumed" });

    const s = useAgentStore.getState();
    expect(s.replaying).toBe(false);
    expect(s.sessionId).toBe("resumed");
  });

  it("handleResumeResult error rolls back optimistic sessionId without queuing a new session", () => {
    useAgentStore.getState().beginReplay("resumed");
    useAgentStore.getState().handleResumeResult({ error: "agent doesn't support session/load" });

    const s = useAgentStore.getState();
    expect(s.replaying).toBe(false);
    expect(s.sessionId).toBeNull();
    expect(s.connectError).toBe("agent doesn't support session/load");
    // Critical: pendingSession stays null so the auto-create-session
    // effect doesn't silently spawn a fresh session and bury the error.
    expect(s.pendingSession).toBeNull();
  });

  it("beginReplay clears pendingSession so the resume isn't shadowed by an auto-create", () => {
    useAgentStore.setState({ pendingSession: { kind: "create" } });
    useAgentStore.getState().beginReplay("resumed");
    expect(useAgentStore.getState().pendingSession).toBeNull();
  });

  it("setConnecting defaults pendingSession to create", () => {
    useAgentStore.getState().setConnecting("claude-acp");
    expect(useAgentStore.getState().pendingSession).toEqual({ kind: "create" });
  });

  it("setConnecting accepts a resume intent for start-then-resume flows", () => {
    useAgentStore.getState().setConnecting("claude-acp", { kind: "resume", sessionId: "s-old" });
    expect(useAgentStore.getState().pendingSession).toEqual({
      kind: "resume",
      sessionId: "s-old",
    });
  });

  it("handleStartResult error clears pendingSession so a queued resume doesn't fire next connect", () => {
    useAgentStore.getState().setConnecting("claude-acp", { kind: "resume", sessionId: "s-old" });
    useAgentStore.getState().handleStartResult({ error: "bunx not found" });
    expect(useAgentStore.getState().pendingSession).toBeNull();
  });
});
