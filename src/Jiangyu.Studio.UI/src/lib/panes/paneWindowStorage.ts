import type { PaneKind } from "@lib/layout";
import type { AssetBrowserState, TemplateBrowserState } from "@lib/panes/browserState";

// Persistent descriptor of a pane window open against a project. The runtime
// windowId that InfiniFrame assigns isn't stored because it's process-local;
// the primary maintains an in-memory map from windowId → descriptor slot so
// it can drop entries when a secondary closes mid-session.
export interface PaneWindowDescriptor {
  readonly kind: PaneKind;
  readonly filePaths: readonly string[];
  readonly activeFilePath: string | null;
  // Populated for browser windows. Shape depends on `kind`.
  readonly browserState?: AssetBrowserState | TemplateBrowserState | undefined;
}

const KEY_PREFIX = "jiangyu.panewindows.";

function keyFor(projectPath: string): string {
  return KEY_PREFIX + projectPath;
}

export function loadPaneWindows(projectPath: string): PaneWindowDescriptor[] {
  try {
    const raw = localStorage.getItem(keyFor(projectPath));
    if (raw === null) return [];
    const parsed = JSON.parse(raw) as unknown;
    if (!Array.isArray(parsed)) return [];
    return parsed.filter(isDescriptor);
  } catch {
    return [];
  }
}

export function savePaneWindows(
  projectPath: string,
  descriptors: readonly PaneWindowDescriptor[],
): void {
  try {
    if (descriptors.length === 0) localStorage.removeItem(keyFor(projectPath));
    else localStorage.setItem(keyFor(projectPath), JSON.stringify(descriptors));
  } catch {
    // Storage quota / disabled — non-fatal.
  }
}

function isDescriptor(value: unknown): value is PaneWindowDescriptor {
  if (typeof value !== "object" || value === null) return false;
  const d = value as Partial<PaneWindowDescriptor>;
  if (d.kind !== "code" && d.kind !== "assetBrowser" && d.kind !== "templateBrowser") return false;
  if (!Array.isArray(d.filePaths) || !d.filePaths.every((p) => typeof p === "string")) return false;
  if (d.activeFilePath !== null && typeof d.activeFilePath !== "string") return false;
  // browserState is opaque — trust the shape if present (the browsers fall
  // back to defaults for missing fields).
  return true;
}
