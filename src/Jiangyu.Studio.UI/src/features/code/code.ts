import { rpcCall } from "@shared/rpc";
import type { CodeSyncResult, DeployResult } from "@shared/rpc";

export type { CodeSyncResult, DeployResult };

/** Scaffold or refresh the open project's code/ C# project. */
export async function codeSync(): Promise<CodeSyncResult> {
  return rpcCall<CodeSyncResult>("codeSync");
}

/** Deploy the open project's compiled/ output into the game's Mods folder. */
export async function deploy(): Promise<DeployResult> {
  return rpcCall<DeployResult>("deploy");
}
