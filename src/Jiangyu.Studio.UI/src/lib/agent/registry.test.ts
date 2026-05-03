import { describe, it, expect } from "vitest";
import {
  toInstalledAgent,
  distributionKindsFor,
  preferredDistribution,
  type RegistryAgent,
} from "./registry";

function npxAgent(overrides: Partial<RegistryAgent> = {}): RegistryAgent {
  return {
    id: "claude-acp",
    name: "Claude Agent",
    version: "0.31.4",
    description: "ACP wrapper for Anthropic's Claude",
    distribution: {
      npx: { package: "@agentclientprotocol/claude-agent-acp@0.31.4" },
    },
    ...overrides,
  };
}

function binaryAgent(): RegistryAgent {
  return {
    id: "amp-acp",
    name: "Amp",
    version: "0.7.0",
    description: "ACP wrapper for Amp",
    distribution: {
      binary: {
        "linux-x86_64": {
          archive: "https://example/amp-linux.tar.gz",
          cmd: "./amp-acp",
        },
      },
    },
  };
}

function uvxAgent(): RegistryAgent {
  return {
    id: "py-agent",
    name: "Py Agent",
    version: "1.0.0",
    description: "Python-distributed agent",
    distribution: {
      uvx: { package: "py-agent==1.0.0" },
    },
  };
}

describe("toInstalledAgent", () => {
  it("maps an npx agent to a bunx-runnable InstalledAgent", () => {
    const agent = npxAgent();
    const installed = toInstalledAgent(agent);
    expect(installed).not.toBeNull();
    expect(installed).toEqual({
      id: "claude-acp",
      name: "Claude Agent",
      version: "0.31.4",
      distribution: "npx",
      command: "bunx",
      args: ["@agentclientprotocol/claude-agent-acp@0.31.4"],
      packageName: "@agentclientprotocol/claude-agent-acp@0.31.4",
      iconUrl: null,
    });
  });

  it("appends the registry's extra args after the package", () => {
    const agent = npxAgent({
      distribution: {
        npx: {
          package: "cline@2.17.0",
          args: ["--acp"],
        },
      },
    });
    const installed = toInstalledAgent(agent);
    expect(installed?.args).toEqual(["cline@2.17.0", "--acp"]);
  });

  it("propagates the icon URL when the registry provides one", () => {
    const agent = npxAgent({ icon: "https://cdn.example/icon.svg" });
    const installed = toInstalledAgent(agent);
    expect(installed?.iconUrl).toBe("https://cdn.example/icon.svg");
  });

  it("returns null for binary-only agents (binary install not yet supported)", () => {
    expect(toInstalledAgent(binaryAgent())).toBeNull();
  });

  it("returns null for uvx-only agents (uvx install not yet supported)", () => {
    expect(toInstalledAgent(uvxAgent())).toBeNull();
  });

  it("prefers npx when both npx and binary are offered", () => {
    const agent: RegistryAgent = {
      id: "dual",
      name: "Dual Distribution",
      version: "1.0.0",
      description: "Has both",
      distribution: {
        npx: { package: "dual@1.0.0" },
        binary: {
          "linux-x86_64": { archive: "https://x/dual.tar.gz", cmd: "./dual" },
        },
      },
    };
    const installed = toInstalledAgent(agent);
    expect(installed?.distribution).toBe("npx");
    expect(installed?.command).toBe("bunx");
  });
});

describe("distributionKindsFor", () => {
  it("returns the kinds in canonical order: npx, uvx, binary", () => {
    const agent: RegistryAgent = {
      id: "all",
      name: "All",
      version: "1.0.0",
      description: "All three",
      distribution: {
        binary: { "linux-x86_64": { archive: "x", cmd: "./x" } },
        npx: { package: "x" },
        uvx: { package: "x" },
      },
    };
    expect(distributionKindsFor(agent)).toEqual(["npx", "uvx", "binary"]);
  });

  it("returns only the kinds present", () => {
    expect(distributionKindsFor(uvxAgent())).toEqual(["uvx"]);
    expect(distributionKindsFor(binaryAgent())).toEqual(["binary"]);
    expect(distributionKindsFor(npxAgent())).toEqual(["npx"]);
  });
});

describe("preferredDistribution", () => {
  it("picks npx when available", () => {
    expect(preferredDistribution(npxAgent())).toBe("npx");
  });

  it("returns null when only an unsupported kind is offered", () => {
    expect(preferredDistribution(binaryAgent())).toBeNull();
    expect(preferredDistribution(uvxAgent())).toBeNull();
  });
});
