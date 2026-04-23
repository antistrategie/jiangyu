import { rpcCall, subscribe } from "./rpc.ts";

// Shared "mime" for cross-window tab drag. Custom mimetypes don't bridge
// reliably across webview processes (WebKitGTK's X11 DnD drops them); text/plain
// is the lowest-common-denominator channel that survives. The payload marker
// lets us distinguish a Jiangyu tab drop from an arbitrary text-drop.
export const CROSS_TAB_MIME = "text/plain";
const MARKER = "jiangyu-tab-drag/1";

interface CrossTabPayload {
  readonly m: string;
  readonly path: string;
}

export function encodeCrossTabPayload(path: string): string {
  return JSON.stringify({ m: MARKER, path } satisfies CrossTabPayload);
}

/** Returns the path if `raw` is a valid Jiangyu cross-window tab payload. */
export function parseCrossTabPayload(raw: string): string | null {
  if (raw.length === 0) return null;
  try {
    const parsed = JSON.parse(raw) as Partial<CrossTabPayload>;
    if (parsed.m !== MARKER || typeof parsed.path !== "string") return null;
    return parsed.path;
  } catch {
    return null;
  }
}

/** Source window declaring it's starting a cross-window tab drag. */
export function beginTabMove(path: string): Promise<void> {
  return rpcCall<void>("beginTabMove", { path });
}

/** Target window declaring it consumed a cross-window tab drop. */
export function completeTabMove(path: string): Promise<void> {
  return rpcCall<void>("completeTabMove", { path });
}

/** Subscribe to tab-moved-out notifications — the source window is told the tab was consumed. */
export function subscribeTabMovedOut(callback: (path: string) => void): () => void {
  return subscribe<{ path: string }>("tabMovedOut", ({ path }) => callback(path));
}
