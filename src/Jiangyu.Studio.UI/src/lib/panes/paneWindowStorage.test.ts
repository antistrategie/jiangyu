import { beforeEach, describe, expect, it, vi } from "vitest";
import {
  loadPaneWindows,
  savePaneWindows,
  type PaneWindowDescriptor,
} from "@lib/panes/paneWindowStorage";

function stubStorage() {
  const store = new Map<string, string>();
  vi.stubGlobal("localStorage", {
    getItem: (k: string) => store.get(k) ?? null,
    setItem: (k: string, v: string) => store.set(k, v),
    removeItem: (k: string) => store.delete(k),
    clear: () => store.clear(),
  });
  return store;
}

const CODE: PaneWindowDescriptor = {
  kind: "code",
  filePaths: ["/proj/a", "/proj/b"],
  activeFilePath: "/proj/b",
};

const ASSET_BROWSER: PaneWindowDescriptor = {
  kind: "assetBrowser",
  filePaths: [],
  activeFilePath: null,
  browserState: {
    query: "soldier",
    kindFilter: "model",
    selection: [],
    focusedKey: null,
    listFraction: 0.42,
    scrollTop: 120,
  },
};

describe("save/loadPaneWindows", () => {
  beforeEach(() => {
    stubStorage();
  });

  it("returns [] when nothing saved", () => {
    expect(loadPaneWindows("/proj")).toEqual([]);
  });

  it("round-trips a code descriptor", () => {
    savePaneWindows("/proj", [CODE]);
    expect(loadPaneWindows("/proj")).toEqual([CODE]);
  });

  it("round-trips a browser descriptor with state", () => {
    savePaneWindows("/proj", [ASSET_BROWSER]);
    expect(loadPaneWindows("/proj")).toEqual([ASSET_BROWSER]);
  });

  it("isolates descriptors per project path", () => {
    savePaneWindows("/proj-a", [CODE]);
    savePaneWindows("/proj-b", [ASSET_BROWSER]);
    expect(loadPaneWindows("/proj-a")).toEqual([CODE]);
    expect(loadPaneWindows("/proj-b")).toEqual([ASSET_BROWSER]);
  });

  it("clears the key when saving an empty list", () => {
    savePaneWindows("/proj", [CODE]);
    expect(loadPaneWindows("/proj")).toHaveLength(1);
    savePaneWindows("/proj", []);
    expect(loadPaneWindows("/proj")).toEqual([]);
  });

  it("returns [] when stored JSON is malformed", () => {
    localStorage.setItem("jiangyu.panewindows./proj", "{bad json");
    expect(loadPaneWindows("/proj")).toEqual([]);
  });

  it("returns [] when stored value is not an array", () => {
    localStorage.setItem("jiangyu.panewindows./proj", JSON.stringify({ kind: "code" }));
    expect(loadPaneWindows("/proj")).toEqual([]);
  });

  it("filters out entries with unknown kinds", () => {
    localStorage.setItem(
      "jiangyu.panewindows./proj",
      JSON.stringify([CODE, { kind: "zzz", filePaths: [], activeFilePath: null }]),
    );
    expect(loadPaneWindows("/proj")).toEqual([CODE]);
  });

  it("filters out entries with malformed filePaths", () => {
    localStorage.setItem(
      "jiangyu.panewindows./proj",
      JSON.stringify([
        { kind: "code", filePaths: ["ok"], activeFilePath: null },
        { kind: "code", filePaths: [42], activeFilePath: null },
      ]),
    );
    expect(loadPaneWindows("/proj")).toHaveLength(1);
  });

  it("filters out entries where activeFilePath is a non-string non-null", () => {
    localStorage.setItem(
      "jiangyu.panewindows./proj",
      JSON.stringify([{ kind: "code", filePaths: [], activeFilePath: 7 }]),
    );
    expect(loadPaneWindows("/proj")).toEqual([]);
  });
});
