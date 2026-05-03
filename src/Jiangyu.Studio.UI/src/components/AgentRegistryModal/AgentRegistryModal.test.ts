// @vitest-environment jsdom
import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";
import { createElement } from "react";
import { render, screen, fireEvent, cleanup, waitFor } from "@testing-library/react";

vi.mock("./AgentRegistryModal.module.css", () => ({
  default: new Proxy({}, { get: (_, key) => key }),
}));

vi.mock("@components/Modal/Modal.module.css", () => ({
  default: new Proxy({}, { get: (_, key) => key }),
}));

vi.mock("@components/MenuList/MenuList.module.css", () => ({
  default: new Proxy({}, { get: (_, key) => key }),
}));

vi.mock("@lib/agent/registry", async () => {
  const actual = await vi.importActual<typeof import("@lib/agent/registry")>("@lib/agent/registry");
  return {
    ...actual,
    fetchRegistry: vi.fn(() =>
      Promise.resolve({
        version: "1.0.0",
        agents: [
          {
            id: "claude-acp",
            name: "Claude Agent",
            version: "0.31.4",
            description: "ACP wrapper for Anthropic's Claude",
            distribution: { npx: { package: "@agentclientprotocol/claude-agent-acp@0.31.4" } },
          },
          {
            id: "binary-only",
            name: "Binary Only",
            version: "1.0.0",
            description: "Distributed as a native binary",
            distribution: {
              binary: { "linux-x86_64": { archive: "x", cmd: "./x" } },
            },
          },
        ],
      }),
    ),
  };
});

vi.mock("@lib/rpc", async () => {
  const actual = await vi.importActual<typeof import("@lib/rpc")>("@lib/rpc");
  return {
    ...actual,
    rpcCall: vi.fn((_method: string, params?: unknown) => {
      const value = (params as { value?: unknown } | undefined)?.value;
      return Promise.resolve({ installedAgents: value });
    }),
  };
});

import { AgentRegistryModal } from "./AgentRegistryModal";
import { useInstalledAgents } from "@lib/agent/installed";

beforeEach(() => {
  localStorage.clear();
  useInstalledAgents.setState({ agents: [] });
});

afterEach(() => {
  cleanup();
  vi.clearAllMocks();
});

describe("AgentRegistryModal", () => {
  it("renders an Install button for npx-distributed agents and a disabled badge for binary-only", async () => {
    render(createElement(AgentRegistryModal, { onClose: vi.fn() }));

    // Wait for the registry fetch to resolve and the rows to render.
    await waitFor(() => {
      expect(screen.getByText("Claude Agent")).toBeTruthy();
    });

    // Supported distribution → Install affordance.
    expect(screen.getByRole("button", { name: "Install" })).toBeTruthy();

    // Unsupported distribution → "Unsupported" note instead of an Install button.
    expect(screen.getByText("Unsupported")).toBeTruthy();
  });

  it("Install adds the agent to the installed-agents store and the row toggles to Remove", async () => {
    render(createElement(AgentRegistryModal, { onClose: vi.fn() }));

    await waitFor(() => {
      expect(screen.getByText("Claude Agent")).toBeTruthy();
    });

    fireEvent.click(screen.getByRole("button", { name: "Install" }));

    // install() is async; wait for the store to settle and the button to flip.
    await waitFor(() => {
      const ids = useInstalledAgents.getState().agents.map((a) => a.id);
      expect(ids).toContain("claude-acp");
    });

    expect(screen.getByRole("button", { name: "Remove" })).toBeTruthy();
  });

  it("Remove takes an installed agent back out of the store", async () => {
    // Start with the agent already installed so we render the Remove state.
    useInstalledAgents.setState({
      agents: [
        {
          id: "claude-acp",
          name: "Claude Agent",
          version: "0.31.4",
          distribution: "npx",
          command: "bunx",
          args: ["@agentclientprotocol/claude-agent-acp@0.31.4"],
          packageName: "@agentclientprotocol/claude-agent-acp@0.31.4",
          iconUrl: null,
        },
      ],
    });

    render(createElement(AgentRegistryModal, { onClose: vi.fn() }));

    await waitFor(() => {
      expect(screen.getByRole("button", { name: "Remove" })).toBeTruthy();
    });

    fireEvent.click(screen.getByRole("button", { name: "Remove" }));

    await waitFor(() => {
      expect(useInstalledAgents.getState().agents).toHaveLength(0);
    });
  });

  it("filters the agent list as the user types", async () => {
    render(createElement(AgentRegistryModal, { onClose: vi.fn() }));

    await waitFor(() => {
      expect(screen.getByText("Claude Agent")).toBeTruthy();
    });

    const search = screen.getByPlaceholderText("Filter agents…");
    fireEvent.change(search, { target: { value: "binary" } });

    expect(screen.queryByText("Claude Agent")).toBeNull();
    expect(screen.getByText("Binary Only")).toBeTruthy();
  });
});
