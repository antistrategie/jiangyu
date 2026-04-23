import { describe, expect, it } from "vitest";
import { matchBinding } from "./shortcuts.ts";

// `matchBinding` only reads key / ctrlKey / metaKey / shiftKey / altKey, so a
// plain struct cast to KeyboardEvent is enough — avoids pulling in jsdom.
function ev(
  init: { key: string } & Partial<
    Pick<KeyboardEvent, "ctrlKey" | "metaKey" | "shiftKey" | "altKey">
  >,
): KeyboardEvent {
  return {
    key: init.key,
    ctrlKey: init.ctrlKey ?? false,
    metaKey: init.metaKey ?? false,
    shiftKey: init.shiftKey ?? false,
    altKey: init.altKey ?? false,
  } as KeyboardEvent;
}

describe("matchBinding", () => {
  it("matches a bare key without modifiers", () => {
    expect(matchBinding(ev({ key: "Escape" }), { key: "Escape" })).toBe(true);
  });

  it("rejects when modifier required but absent", () => {
    expect(matchBinding(ev({ key: "p" }), { mod: true, key: "p" })).toBe(false);
  });

  it("rejects when modifier unexpected but present", () => {
    expect(matchBinding(ev({ key: "Escape", ctrlKey: true }), { key: "Escape" })).toBe(false);
  });

  it("treats ctrl and meta as equivalent mod", () => {
    expect(matchBinding(ev({ key: "s", ctrlKey: true }), { mod: true, key: "s" })).toBe(true);
    expect(matchBinding(ev({ key: "s", metaKey: true }), { mod: true, key: "s" })).toBe(true);
  });

  it("respects shift requirement", () => {
    expect(
      matchBinding(ev({ key: "P", ctrlKey: true, shiftKey: true }), {
        mod: true,
        shift: true,
        key: "p",
      }),
    ).toBe(true);
    expect(
      matchBinding(ev({ key: "p", ctrlKey: true }), { mod: true, shift: true, key: "p" }),
    ).toBe(false);
  });

  it("is case-insensitive on the key", () => {
    expect(matchBinding(ev({ key: "W", ctrlKey: true }), { mod: true, key: "w" })).toBe(true);
  });

  it("matches punctuation keys literally", () => {
    expect(matchBinding(ev({ key: "\\", ctrlKey: true }), { mod: true, key: "\\" })).toBe(true);
    expect(
      matchBinding(ev({ key: "|", ctrlKey: true, shiftKey: true }), {
        mod: true,
        shift: true,
        key: "|",
      }),
    ).toBe(true);
  });
});
