import { rpcCall, type BridgeStatusResult, type UiDump, type UiNode } from "@shared/rpc";

export type { BridgeStatusResult, UiDump, UiNode };

/** Current bridge state: enabled (the game's `bridge` flag is set) and connected (socket up). */
export function bridgeStatus(): Promise<BridgeStatusResult> {
  return rpcCall<BridgeStatusResult>("bridgeStatus");
}

/** Enable or disable the bridge. Writes the game's `bridge` flag and connects/disconnects. */
export function bridgeSetEnabled(enabled: boolean): Promise<BridgeStatusResult> {
  return rpcCall<BridgeStatusResult>("bridgeSetEnabled", { enabled });
}

/** Capture the game's live UI tree (the inspector feed). Rejects when not connected, null when no UI is up. */
export function bridgeUiCapture(): Promise<UiDump | null> {
  return rpcCall<UiDump | null>("bridgeUiCapture");
}
