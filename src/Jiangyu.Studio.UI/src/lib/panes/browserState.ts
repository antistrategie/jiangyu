import type { AssetKindGroup } from "@lib/assets";

// Controlled state emitted by AssetBrowser / TemplateBrowser so the parent
// can persist, restore, or transfer it (between panes, windows, or sessions).
// Shapes are intentionally flat + JSON-serialisable so they round-trip through
// localStorage and URL params.

export interface AssetBrowserState {
  readonly query: string;
  readonly kindFilter: "all" | AssetKindGroup;
  readonly selection: readonly string[];
  readonly focusedKey: string | null;
  readonly listFraction: number;
  readonly scrollTop: number;
}

export const DEFAULT_ASSET_BROWSER_STATE: AssetBrowserState = {
  query: "",
  kindFilter: "all",
  selection: [],
  focusedKey: null,
  listFraction: 0.35,
  scrollTop: 0,
};

export interface TemplateBrowserState {
  readonly query: string;
  readonly typeFilter: string;
  readonly focusedKey: string | null;
  readonly navHistory: readonly string[];
  readonly navIndex: number;
  readonly listFraction: number;
  readonly scrollTop: number;
}

export const DEFAULT_TEMPLATE_BROWSER_STATE: TemplateBrowserState = {
  query: "",
  typeFilter: "all",
  focusedKey: null,
  navHistory: [],
  navIndex: -1,
  listFraction: 0.35,
  scrollTop: 0,
};
