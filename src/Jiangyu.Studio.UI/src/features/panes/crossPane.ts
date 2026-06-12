import { rpcCall, subscribe } from "@shared/rpc";
import { DATA_BROWSER_KINDS, type PaneKind } from "@features/panes/layout";
import type { AssetBrowserState, TemplateBrowserState } from "@features/panes/browserState";

// Cross-window pane drag: used when a secondary window's whole pane (its tabs
// or its browser state) is dragged back into the primary. Rides on
// application/json, a standard mimetype that survives webview-process
// boundaries (text/plain is already taken by the tab drag). The marker field
// distinguishes the payload from an arbitrary JSON drop.

export const CROSS_PANE_MIME = "application/json";
const MARKER = "jiangyu-pane-drag/1";

// Only panes with a drag affordance in a secondary window can encode this
// payload: code panes (tab bar fill) and the data browsers (drag bar).
// Simple panes have no drag bar, so their kinds never ride this path.
const DRAGGABLE_PANE_KINDS: readonly PaneKind[] = ["code", ...DATA_BROWSER_KINDS];

declare module "@shared/rpc/notifications" {
  interface HostNotificationMap {
    // Pure signal: the source window only needs to know its pane was consumed.
    paneMovedOut: unknown;
  }
}

export interface CrossPanePayload {
  readonly m: typeof MARKER;
  readonly kind: PaneKind;
  readonly filePaths?: readonly string[] | undefined;
  readonly activeFilePath?: string | null | undefined;
  readonly browserState?: AssetBrowserState | TemplateBrowserState | undefined;
}

export function encodeCrossPanePayload(payload: Omit<CrossPanePayload, "m">): string {
  return JSON.stringify({ m: MARKER, ...payload });
}

export function parseCrossPanePayload(raw: string): CrossPanePayload | null {
  if (raw.length === 0) return null;
  try {
    const parsed = JSON.parse(raw) as Partial<CrossPanePayload>;
    if (parsed.m !== MARKER) return null;
    if (parsed.kind === undefined || !DRAGGABLE_PANE_KINDS.includes(parsed.kind)) return null;
    return parsed as CrossPanePayload;
  } catch {
    return null;
  }
}

export async function beginPaneMove(): Promise<void> {
  await rpcCall<null>("beginPaneMove");
}

export async function completePaneMove(): Promise<void> {
  await rpcCall<null>("completePaneMove");
}

export function subscribePaneMovedOut(callback: () => void): () => void {
  return subscribe("paneMovedOut", () => {
    callback();
  });
}
