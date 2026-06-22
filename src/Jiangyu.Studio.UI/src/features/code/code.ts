import { rpcCall } from "@shared/rpc";
import type { CodeSyncResult } from "@shared/rpc";

export type { CodeSyncResult };

/** Scaffold or refresh the open project's code/ C# project. */
export async function codeSync(): Promise<CodeSyncResult> {
  return rpcCall<CodeSyncResult>("codeSync", undefined, { timeoutMs: 0 });
}
