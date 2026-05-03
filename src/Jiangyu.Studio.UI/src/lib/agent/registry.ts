/**
 * ACP registry client. The host fetches the canonical
 * https://cdn.agentclientprotocol.com/registry/v1/latest/registry.json
 * (proxied via the `agentsRegistryFetch` RPC + `agentsRegistryFetched`
 * notification) so the WebView never touches the network directly. There
 * is no result cache — concurrent calls share a single in-flight promise,
 * but each modal open re-fetches from the CDN.
 *
 * Distribution shape mirrors the published schema. npx-only is the v1
 * happy path; binary and uvx install are left for a follow-up.
 */
import { rpcCall, subscribe, type InstalledAgent } from "@lib/rpc";

export interface RegistryNpx {
  readonly package: string;
  readonly args?: readonly string[];
  readonly env?: Readonly<Record<string, string>>;
}

export interface RegistryUvx {
  readonly package: string;
  readonly args?: readonly string[];
  readonly env?: Readonly<Record<string, string>>;
}

export interface RegistryBinaryEntry {
  readonly archive: string;
  readonly cmd: string;
}

export type RegistryBinaryPlatform =
  | "darwin-aarch64"
  | "darwin-x86_64"
  | "linux-aarch64"
  | "linux-x86_64"
  | "windows-aarch64"
  | "windows-x86_64";

export type RegistryBinary = Partial<Record<RegistryBinaryPlatform, RegistryBinaryEntry>>;

export interface RegistryDistribution {
  readonly npx?: RegistryNpx;
  readonly uvx?: RegistryUvx;
  readonly binary?: RegistryBinary;
}

export interface RegistryAgent {
  readonly id: string;
  readonly name: string;
  readonly version: string;
  readonly description: string;
  readonly repository?: string;
  readonly website?: string;
  readonly authors?: readonly string[];
  readonly license?: string;
  readonly icon?: string;
  readonly distribution: RegistryDistribution;
}

export interface RegistryDocument {
  readonly version: string;
  readonly agents: readonly RegistryAgent[];
}

interface RegistryFetchedEvent {
  readonly registry?: RegistryDocument;
  readonly error?: string;
}

/**
 * Fire the host fetch and resolve when the matching notification arrives.
 * Concurrent calls share a single in-flight promise so opening the modal
 * twice in quick succession doesn't double-fetch.
 */
let inFlight: Promise<RegistryDocument> | null = null;

export function fetchRegistry(): Promise<RegistryDocument> {
  if (inFlight !== null) return inFlight;

  inFlight = new Promise<RegistryDocument>((resolve, reject) => {
    const unsubscribe = subscribe("agentsRegistryFetched", (params) => {
      unsubscribe();
      inFlight = null;
      const event = params as RegistryFetchedEvent;
      if (event.error !== undefined) {
        reject(new Error(event.error));
      } else if (event.registry !== undefined) {
        resolve(event.registry);
      } else {
        reject(new Error("Registry response missing payload"));
      }
    });

    rpcCall("agentsRegistryFetch").catch((err: unknown) => {
      unsubscribe();
      inFlight = null;
      reject(err instanceof Error ? err : new Error(String(err)));
    });
  });

  return inFlight;
}

export type DistributionKind = "npx" | "uvx" | "binary";

/** Distribution kinds we currently support installing. */
export const SUPPORTED_DISTRIBUTIONS: readonly DistributionKind[] = ["npx"];

export function distributionKindsFor(agent: RegistryAgent): readonly DistributionKind[] {
  const kinds: DistributionKind[] = [];
  if (agent.distribution.npx !== undefined) kinds.push("npx");
  if (agent.distribution.uvx !== undefined) kinds.push("uvx");
  if (agent.distribution.binary !== undefined) kinds.push("binary");
  return kinds;
}

export function preferredDistribution(agent: RegistryAgent): DistributionKind | null {
  for (const kind of SUPPORTED_DISTRIBUTIONS) {
    if (distributionKindsFor(agent).includes(kind)) return kind;
  }
  return null;
}

/**
 * Build an InstalledAgent record from a registry entry. Currently only
 * npx is supported; binary/uvx return null and the caller surfaces that
 * as an unsupported distribution badge.
 */
export function toInstalledAgent(agent: RegistryAgent): InstalledAgent | null {
  const npx = agent.distribution.npx;
  if (npx === undefined) return null;
  return {
    id: agent.id,
    name: agent.name,
    version: agent.version,
    distribution: "npx",
    command: "bunx",
    args: [npx.package, ...(npx.args ?? [])],
    packageName: npx.package,
    iconUrl: agent.icon ?? null,
  };
}
