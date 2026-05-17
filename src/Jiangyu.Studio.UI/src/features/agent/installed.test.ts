// @vitest-environment jsdom
import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";
import type { InstalledAgent } from "@shared/rpc";

// `installed.ts` calls `rpcCall("setStudioSetting", ...)` to mirror writes to
// the host. Mock to a no-op so tests stay in-process; resolves to a settings
// object whose `installedAgents` echoes the value the store just sent, which
// matches what the host would send back.
vi.mock("@shared/rpc", async () => {
  const actual = await vi.importActual<typeof import("@shared/rpc")>("@shared/rpc");
  return {
    ...actual,
    rpcCall: vi.fn((_method: string, params?: unknown) => {
      const value = (params as { value?: unknown } | undefined)?.value;
      return Promise.resolve({ installedAgents: value });
    }),
  };
});

import { useInstalledAgents } from "./installed";

function agent(id: string, version = "1.0.0"): InstalledAgent {
  return {
    id,
    name: id,
    version,
    distribution: "npx",
    command: "bunx",
    args: [`${id}@${version}`],
    packageName: `${id}@${version}`,
    iconUrl: null,
  };
}

beforeEach(() => {
  localStorage.clear();
  useInstalledAgents.setState({ agents: [] });
});

afterEach(() => {
  vi.clearAllMocks();
});

describe("install", () => {
  it("appends a new agent to the end of the list", async () => {
    await useInstalledAgents.getState().install(agent("a"));
    await useInstalledAgents.getState().install(agent("b"));
    expect(useInstalledAgents.getState().agents.map((a) => a.id)).toEqual(["a", "b"]);
  });

  it("upserts an existing agent in place (preserves position)", async () => {
    await useInstalledAgents.getState().install(agent("a"));
    await useInstalledAgents.getState().install(agent("b"));
    await useInstalledAgents.getState().install(agent("c"));

    // Re-install b with a new version. Position should stay 1, version should update.
    await useInstalledAgents.getState().install(agent("b", "2.0.0"));

    const list = useInstalledAgents.getState().agents;
    expect(list.map((a) => a.id)).toEqual(["a", "b", "c"]);
    expect(list[1]?.version).toBe("2.0.0");
  });

  it("mirrors the list to localStorage so the next launch sees it", async () => {
    await useInstalledAgents.getState().install(agent("a"));
    const raw = localStorage.getItem("jiangyu:setting:installedAgents");
    expect(raw).not.toBeNull();
    const parsed = JSON.parse(raw ?? "[]") as InstalledAgent[];
    expect(parsed.map((a) => a.id)).toEqual(["a"]);
  });
});

describe("uninstall", () => {
  it("removes the agent with the matching id", async () => {
    await useInstalledAgents.getState().install(agent("a"));
    await useInstalledAgents.getState().install(agent("b"));
    await useInstalledAgents.getState().install(agent("c"));

    await useInstalledAgents.getState().uninstall("b");

    expect(useInstalledAgents.getState().agents.map((a) => a.id)).toEqual(["a", "c"]);
  });

  it("is a no-op when the id is not present", async () => {
    await useInstalledAgents.getState().install(agent("a"));
    await useInstalledAgents.getState().uninstall("missing");
    expect(useInstalledAgents.getState().agents.map((a) => a.id)).toEqual(["a"]);
  });
});
