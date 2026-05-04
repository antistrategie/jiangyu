// @vitest-environment jsdom
import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";
import { createElement } from "react";
import { render, screen, fireEvent, cleanup } from "@testing-library/react";

// Mock the RPC layer so button clicks don't try to dispatch through a
// non-existent host. agentPermissionResponse is the only one PermissionBlock
// touches.
const permissionResponseMock = vi.fn((..._args: unknown[]) => Promise.resolve());
vi.mock("@lib/agent/rpc", () => ({
  agentPermissionResponse: (...args: unknown[]) => permissionResponseMock(...args),
}));

vi.mock("./AgentPanel.module.css", () => ({
  default: new Proxy({}, { get: (_, key) => key }),
}));

// Lucide ships ESM that vitest can resolve, but rendering its SVGs in jsdom
// is wasted work for a className/aria-label assertion test. Stub them.
vi.mock("lucide-react", () => ({
  Clock: () => null,
  Plus: () => null,
}));

import { AuthCard, PermissionBlock } from "./AgentPanel";
import { useAgentStore } from "@lib/agent/store";
import type { PermissionMessage } from "@lib/agent/store";
import type { AuthMethod } from "@lib/agent/types";

function fourWayMessage(overrides?: Partial<PermissionMessage>): PermissionMessage {
  return {
    id: "m1",
    role: "permission",
    permissionId: "perm-1",
    toolCall: {
      toolCallId: "tc-1",
      title: "Edit templates/foo.kdl",
      kind: "edit",
    },
    options: [
      { optionId: "ao", name: "Allow once", kind: "allow_once" },
      { optionId: "aa", name: "Always allow", kind: "allow_always" },
      { optionId: "ro", name: "Reject", kind: "reject_once" },
      { optionId: "ra", name: "Always reject", kind: "reject_always" },
    ],
    resolved: false,
    ...overrides,
  };
}

function resetStore() {
  useAgentStore.setState({
    autoApproveKinds: new Set(),
    autoRejectKinds: new Set(),
    messages: [],
  });
}

beforeEach(() => {
  permissionResponseMock.mockClear();
  resetStore();
});
afterEach(() => cleanup());

describe("PermissionBlock", () => {
  it("renders one button per agent-supplied option", () => {
    render(createElement(PermissionBlock, { message: fourWayMessage() }));
    expect(screen.getByRole("button", { name: "Allow once" })).not.toBeNull();
    expect(screen.getByRole("button", { name: "Always allow" })).not.toBeNull();
    expect(screen.getByRole("button", { name: "Reject" })).not.toBeNull();
    expect(screen.getByRole("button", { name: "Always reject" })).not.toBeNull();
  });

  it("allow_once fires the RPC and does NOT add to autoApproveKinds", () => {
    render(createElement(PermissionBlock, { message: fourWayMessage() }));
    fireEvent.click(screen.getByRole("button", { name: "Allow once" }));
    expect(permissionResponseMock).toHaveBeenCalledWith("perm-1", "selected", "ao");
    expect(useAgentStore.getState().autoApproveKinds.size).toBe(0);
  });

  it("allow_always fires the RPC AND adds the kind to autoApproveKinds", () => {
    render(createElement(PermissionBlock, { message: fourWayMessage() }));
    fireEvent.click(screen.getByRole("button", { name: "Always allow" }));
    expect(permissionResponseMock).toHaveBeenCalledWith("perm-1", "selected", "aa");
    expect(useAgentStore.getState().autoApproveKinds.has("edit")).toBe(true);
  });

  it("reject_always fires the RPC AND adds the kind to autoRejectKinds", () => {
    render(createElement(PermissionBlock, { message: fourWayMessage() }));
    fireEvent.click(screen.getByRole("button", { name: "Always reject" }));
    expect(permissionResponseMock).toHaveBeenCalledWith("perm-1", "selected", "ra");
    expect(useAgentStore.getState().autoRejectKinds.has("edit")).toBe(true);
  });

  it("reject_once fires the RPC and does NOT add to autoRejectKinds", () => {
    render(createElement(PermissionBlock, { message: fourWayMessage() }));
    fireEvent.click(screen.getByRole("button", { name: "Reject" }));
    expect(permissionResponseMock).toHaveBeenCalledWith("perm-1", "selected", "ro");
    expect(useAgentStore.getState().autoRejectKinds.size).toBe(0);
  });

  it("an _always click on a tool call without a kind still fires the RPC", () => {
    // No kind = nothing to remember by; the response still goes through so
    // the agent isn't left waiting.
    const message: PermissionMessage = fourWayMessage({
      toolCall: { toolCallId: "tc-no-kind", title: "Anonymous tool" },
    });
    render(createElement(PermissionBlock, { message }));
    fireEvent.click(screen.getByRole("button", { name: "Always allow" }));
    expect(permissionResponseMock).toHaveBeenCalledWith("perm-1", "selected", "aa");
    expect(useAgentStore.getState().autoApproveKinds.size).toBe(0);
  });

  it("hides the action row once resolved", () => {
    const message: PermissionMessage = fourWayMessage({ resolved: true });
    render(createElement(PermissionBlock, { message }));
    expect(screen.queryByRole("button", { name: "Allow once" })).toBeNull();
  });

  it("renders the agent-supplied heading", () => {
    render(createElement(PermissionBlock, { message: fourWayMessage() }));
    expect(screen.getByText("Edit templates/foo.kdl")).not.toBeNull();
  });
});

describe("AuthCard", () => {
  const githubMethod: AuthMethod = { id: "github", name: "Sign in with GitHub" };
  const appleMethod: AuthMethod = { id: "apple", name: "Sign in with Apple" };

  it("renders one button per agent-advertised method", () => {
    const onPick = vi.fn();
    render(
      createElement(AuthCard, {
        agentName: "copilot",
        methods: [githubMethod, appleMethod],
        authenticatingMethodId: null,
        error: null,
        onPick,
      }),
    );
    expect(screen.getByRole("button", { name: "Sign in with GitHub" })).not.toBeNull();
    expect(screen.getByRole("button", { name: "Sign in with Apple" })).not.toBeNull();
  });

  it("invokes onPick with the method id on click", () => {
    const onPick = vi.fn();
    render(
      createElement(AuthCard, {
        agentName: "copilot",
        methods: [githubMethod],
        authenticatingMethodId: null,
        error: null,
        onPick,
      }),
    );
    fireEvent.click(screen.getByRole("button", { name: "Sign in with GitHub" }));
    expect(onPick).toHaveBeenCalledWith("github");
  });

  it("disables every button while a method is in flight", () => {
    render(
      createElement(AuthCard, {
        agentName: "copilot",
        methods: [githubMethod, appleMethod],
        authenticatingMethodId: "github",
        error: null,
        onPick: vi.fn(),
      }),
    );
    // The in-flight button advertises progress in its label so the user
    // sees that the click was registered.
    const inFlight = screen.getByRole("button", { name: /Signing in.*GitHub/ });
    expect((inFlight as HTMLButtonElement).disabled).toBe(true);
    const other = screen.getByRole("button", { name: "Sign in with Apple" });
    expect((other as HTMLButtonElement).disabled).toBe(true);
  });

  it("shows the agent's auth error inline", () => {
    render(
      createElement(AuthCard, {
        agentName: "copilot",
        methods: [githubMethod],
        authenticatingMethodId: null,
        error: "user cancelled",
        onPick: vi.fn(),
      }),
    );
    expect(screen.getByText("user cancelled")).not.toBeNull();
  });

  it("falls back to a generic agent name when none is supplied", () => {
    render(
      createElement(AuthCard, {
        agentName: null,
        methods: [githubMethod],
        authenticatingMethodId: null,
        error: null,
        onPick: vi.fn(),
      }),
    );
    expect(screen.getByText(/This agent needs to authenticate/)).not.toBeNull();
  });
});
