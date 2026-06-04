import { rpcCall, type BridgeStatusResult } from "@shared/rpc";

export type { BridgeStatusResult };

/** Current bridge state: enabled (the game's `bridge` flag is set) and connected (socket up). */
export function bridgeStatus(): Promise<BridgeStatusResult> {
  return rpcCall<BridgeStatusResult>("bridgeStatus");
}

/** Enable or disable the bridge. Writes the game's `bridge` flag and connects/disconnects. */
export function bridgeSetEnabled(enabled: boolean): Promise<BridgeStatusResult> {
  return rpcCall<BridgeStatusResult>("bridgeSetEnabled", { enabled });
}

/** Capture the game's live UI tree (the inspector feed). Rejects when not connected. */
export function bridgeUiCapture<T = unknown>(): Promise<T> {
  return rpcCall<T>("bridgeUiCapture");
}
