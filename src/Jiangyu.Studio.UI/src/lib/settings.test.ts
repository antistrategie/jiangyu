import { beforeEach, describe, expect, it, vi } from "vitest";
import {
  EDITOR_FONT_SIZE_DEFAULT,
  EDITOR_FONT_SIZE_MAX,
  EDITOR_FONT_SIZE_MIN,
  EDITOR_KEYBIND_MODE_DEFAULT,
  EDITOR_WORD_WRAP_DEFAULT,
  SESSION_RESTORE_PROJECT_DEFAULT,
  SESSION_RESTORE_TABS_DEFAULT,
  SIDEBAR_HIDDEN_DEFAULT,
  UI_FONT_SCALE_DEFAULT,
  UI_FONT_SCALE_MAX,
  UI_FONT_SCALE_MIN,
  loadEditorFontSize,
  loadEditorKeybindMode,
  loadEditorWordWrap,
  loadSessionRestoreProject,
  loadSessionRestoreTabs,
  loadSidebarHidden,
  loadUiFontScale,
  saveEditorFontSize,
  saveEditorKeybindMode,
  saveEditorWordWrap,
  saveSessionRestoreProject,
  saveSessionRestoreTabs,
  saveSidebarHidden,
  saveUiFontScale,
} from "./settings";

beforeEach(() => {
  const store = new Map<string, string>();
  vi.stubGlobal("localStorage", {
    getItem: (k: string) => store.get(k) ?? null,
    setItem: (k: string, v: string) => store.set(k, v),
    removeItem: (k: string) => store.delete(k),
    clear: () => store.clear(),
  });
});

describe("editor font size", () => {
  it("returns the default when nothing is stored", () => {
    expect(loadEditorFontSize()).toBe(EDITOR_FONT_SIZE_DEFAULT);
  });

  it("returns the default for malformed JSON", () => {
    localStorage.setItem("jiangyu:setting:editorFontSize", "{not json");
    expect(loadEditorFontSize()).toBe(EDITOR_FONT_SIZE_DEFAULT);
  });

  it("round-trips a valid value", () => {
    saveEditorFontSize(18);
    expect(loadEditorFontSize()).toBe(18);
  });

  it("clamps below the minimum on save", () => {
    saveEditorFontSize(2);
    expect(loadEditorFontSize()).toBe(EDITOR_FONT_SIZE_MIN);
  });

  it("clamps above the maximum on save", () => {
    saveEditorFontSize(9999);
    expect(loadEditorFontSize()).toBe(EDITOR_FONT_SIZE_MAX);
  });

  it("clamps a stored out-of-range value on load", () => {
    localStorage.setItem("jiangyu:setting:editorFontSize", "999");
    expect(loadEditorFontSize()).toBe(EDITOR_FONT_SIZE_MAX);
  });

  it("rounds non-integer values", () => {
    saveEditorFontSize(14.7);
    expect(loadEditorFontSize()).toBe(15);
  });

  it("falls back to default when stored value is not a number", () => {
    localStorage.setItem("jiangyu:setting:editorFontSize", JSON.stringify("big"));
    expect(loadEditorFontSize()).toBe(EDITOR_FONT_SIZE_DEFAULT);
  });
});

describe("UI font scale", () => {
  it("returns the default when nothing is stored", () => {
    expect(loadUiFontScale()).toBe(UI_FONT_SCALE_DEFAULT);
  });

  it("round-trips a valid value", () => {
    saveUiFontScale(115);
    expect(loadUiFontScale()).toBe(115);
  });

  it("clamps below the minimum on save", () => {
    saveUiFontScale(10);
    expect(loadUiFontScale()).toBe(UI_FONT_SCALE_MIN);
  });

  it("clamps above the maximum on save", () => {
    saveUiFontScale(9999);
    expect(loadUiFontScale()).toBe(UI_FONT_SCALE_MAX);
  });

  it("falls back to default when stored value is not a number", () => {
    localStorage.setItem("jiangyu:setting:uiFontScale", JSON.stringify("huge"));
    expect(loadUiFontScale()).toBe(UI_FONT_SCALE_DEFAULT);
  });
});

describe("editor word wrap", () => {
  it("returns the default when nothing is stored", () => {
    expect(loadEditorWordWrap()).toBe(EDITOR_WORD_WRAP_DEFAULT);
  });

  it("round-trips 'off'", () => {
    saveEditorWordWrap("off");
    expect(loadEditorWordWrap()).toBe("off");
  });

  it("falls back to default for unrecognised values", () => {
    localStorage.setItem("jiangyu:setting:editorWordWrap", JSON.stringify("sideways"));
    expect(loadEditorWordWrap()).toBe(EDITOR_WORD_WRAP_DEFAULT);
  });
});

describe("editor keybind mode", () => {
  it("returns the default when nothing is stored", () => {
    expect(loadEditorKeybindMode()).toBe(EDITOR_KEYBIND_MODE_DEFAULT);
  });

  it("round-trips 'vim'", () => {
    saveEditorKeybindMode("vim");
    expect(loadEditorKeybindMode()).toBe("vim");
  });

  it("falls back to default for unrecognised values", () => {
    localStorage.setItem("jiangyu:setting:editorKeybindMode", JSON.stringify("helix"));
    expect(loadEditorKeybindMode()).toBe(EDITOR_KEYBIND_MODE_DEFAULT);
  });
});

describe("session restore project", () => {
  it("returns the default when nothing is stored", () => {
    expect(loadSessionRestoreProject()).toBe(SESSION_RESTORE_PROJECT_DEFAULT);
  });

  it("round-trips true", () => {
    saveSessionRestoreProject(true);
    expect(loadSessionRestoreProject()).toBe(true);
  });

  it("round-trips false", () => {
    saveSessionRestoreProject(false);
    expect(loadSessionRestoreProject()).toBe(false);
  });

  it("falls back to default for non-boolean values", () => {
    localStorage.setItem("jiangyu:setting:sessionRestoreProject", JSON.stringify("yes"));
    expect(loadSessionRestoreProject()).toBe(SESSION_RESTORE_PROJECT_DEFAULT);
  });
});

describe("sidebar hidden", () => {
  it("returns the default when nothing is stored", () => {
    expect(loadSidebarHidden()).toBe(SIDEBAR_HIDDEN_DEFAULT);
  });

  it("round-trips true", () => {
    saveSidebarHidden(true);
    expect(loadSidebarHidden()).toBe(true);
  });

  it("falls back to default for non-boolean values", () => {
    localStorage.setItem("jiangyu:setting:sidebarHidden", JSON.stringify("yes"));
    expect(loadSidebarHidden()).toBe(SIDEBAR_HIDDEN_DEFAULT);
  });
});

describe("session restore tabs", () => {
  it("returns the default when nothing is stored", () => {
    expect(loadSessionRestoreTabs()).toBe(SESSION_RESTORE_TABS_DEFAULT);
  });

  it("round-trips false", () => {
    saveSessionRestoreTabs(false);
    expect(loadSessionRestoreTabs()).toBe(false);
  });

  it("falls back to default for non-boolean values", () => {
    localStorage.setItem("jiangyu:setting:sessionRestoreTabs", JSON.stringify(1));
    expect(loadSessionRestoreTabs()).toBe(SESSION_RESTORE_TABS_DEFAULT);
  });
});
