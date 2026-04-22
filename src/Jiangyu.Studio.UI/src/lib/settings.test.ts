import { beforeEach, describe, expect, it, vi } from "vitest";
import {
  EDITOR_FONT_SIZE_DEFAULT,
  EDITOR_FONT_SIZE_MAX,
  EDITOR_FONT_SIZE_MIN,
  EDITOR_KEYBIND_MODE_DEFAULT,
  EDITOR_WORD_WRAP_DEFAULT,
  loadEditorFontSize,
  loadEditorKeybindMode,
  loadEditorWordWrap,
  saveEditorFontSize,
  saveEditorKeybindMode,
  saveEditorWordWrap,
} from "./settings.ts";

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
