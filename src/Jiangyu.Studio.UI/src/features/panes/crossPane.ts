import { rpcCall, subscribe } from "@shared/rpc";
import type { PaneKind } from "@features/panes/layout";
import type { AssetBrowserState, TemplateBrowserState } from "@features/panes/browserState";

// Cross-window pane drag: used when a secondary window's whole pane (its tabs
// or its browser state) is dragged back into the primary. Rides on text/plain
// like the tab-drag because custom mimetypes don't bridge webview processes.

export const CROSS_PANE_MIME = "application/json";
const MARKER = "jiangyu-pane-drag/1";

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
    if (
      parsed.kind !== "code" &&
      parsed.kind !== "assetBrowser" &&
      parsed.kind !== "templateBrowser"
    )
      return null;
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
