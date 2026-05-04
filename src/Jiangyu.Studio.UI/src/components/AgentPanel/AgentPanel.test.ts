// @vitest-environment jsdom
import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";
import { createElement } from "react";
import { render, screen, fireEvent, cleanup } from "@testing-library/react";

// Mock the RPC layer so button clicks don't try to dispatch through a
// non-existent host. agentPermissionResponse is the only one PermissionBlock
// touches.
const permissionResponseMock = vi.fn(() => Promise.resolve());
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
  Shield: () => createElement("span", { "data-testid": "shield-outline" }),
  ShieldCheck: () => createElement("span", { "data-testid": "shield-check" }),
}));

import { PermissionBlock, TrustToggle } from "./AgentPanel";
import { useAgentStore } from "@lib/agent/store";
import type { PermissionMessage } from "@lib/agent/store";

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
    trustAll: false,
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

describe("TrustToggle", () => {
  it("renders the outline shield when trustAll is off", () => {
    useAgentStore.setState({ trustAll: false });
    render(createElement(TrustToggle));
    expect(screen.queryByTestId("shield-outline")).not.toBeNull();
    expect(screen.queryByTestId("shield-check")).toBeNull();
    expect(screen.getByRole("button").getAttribute("aria-pressed")).toBe("false");
  });

  it("renders the check shield when trustAll is on", () => {
    useAgentStore.setState({ trustAll: true });
    render(createElement(TrustToggle));
    expect(screen.queryByTestId("shield-check")).not.toBeNull();
    expect(screen.queryByTestId("shield-outline")).toBeNull();
    expect(screen.getByRole("button").getAttribute("aria-pressed")).toBe("true");
  });

  it("click toggles trustAll", () => {
    useAgentStore.setState({ trustAll: false });
    render(createElement(TrustToggle));
    fireEvent.click(screen.getByRole("button"));
    expect(useAgentStore.getState().trustAll).toBe(true);
    fireEvent.click(screen.getByRole("button"));
    expect(useAgentStore.getState().trustAll).toBe(false);
  });

  it("aria-label reflects the per-kind count when trustAll is off", () => {
    useAgentStore.setState({
      trustAll: false,
      autoApproveKinds: new Set(["edit", "delete"]),
    });
    render(createElement(TrustToggle));
    const label = screen.getByRole("button").getAttribute("aria-label");
    expect(label).toBe("Auto-approve: 2 kinds");
  });

  it("aria-label says 'on' when trustAll is on regardless of per-kind state", () => {
    useAgentStore.setState({
      trustAll: true,
      autoApproveKinds: new Set(["edit"]),
    });
    render(createElement(TrustToggle));
    const label = screen.getByRole("button").getAttribute("aria-label");
    expect(label).toBe("Auto-approve on (this session)");
  });
});
