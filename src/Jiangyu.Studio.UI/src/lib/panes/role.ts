import type { PaneKind } from "@lib/layout";
import type { AssetBrowserState, TemplateBrowserState } from "@lib/panes/browserState";

export type WindowRole = "main" | "pane";

export interface WindowParams {
  readonly role: WindowRole;
  readonly paneKind: PaneKind | null;
  readonly projectPath: string | null;
  readonly filePaths: readonly string[];
  readonly activeFilePath: string | null;
  readonly browserState: AssetBrowserState | TemplateBrowserState | null;
}

// Recognised values for the `kind` URL param. Anything else is ignored so a
// stale/hand-crafted link can't make the secondary render the wrong shell.
const PANE_KINDS: readonly PaneKind[] = ["code", "assetBrowser", "templateBrowser"];

export function getWindowParams(): WindowParams {
  const params = new URLSearchParams(window.location.search);
  const role = params.get("window") === "pane" ? "pane" : "main";
  const rawKind = params.get("kind");
  const paneKind = (PANE_KINDS as readonly string[]).includes(rawKind ?? "")
    ? (rawKind as PaneKind)
    : null;
  return {
    role,
    paneKind,
    projectPath: params.get("projectPath"),
    filePaths: params.getAll("filePath"),
    activeFilePath: params.get("activeFilePath"),
    browserState: parseBrowserStateParam(params.get("browserState")),
  };
}

function parseBrowserStateParam(
  raw: string | null,
): AssetBrowserState | TemplateBrowserState | null {
  if (raw === null || raw.length === 0) return null;
  try {
    return JSON.parse(raw) as AssetBrowserState | TemplateBrowserState;
  } catch {
    return null;
  }
}
