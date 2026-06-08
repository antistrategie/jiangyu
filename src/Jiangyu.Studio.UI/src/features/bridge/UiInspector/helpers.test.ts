import { describe, expect, it } from "vitest";
import type { UiNode } from "@features/bridge/bridge";
import { bestSelector, nodeMatches, selectorsOf, styleEntries, truncate } from "./helpers";

const node = (over: Partial<UiNode> = {}): UiNode => ({
  type: "VisualElement",
  name: null,
  text: null,
  classes: null,
  children: null,
  style: null,
  ...over,
});

describe("selectorsOf", () => {
  it("emits name, type, then each class, most specific first", () => {
    const n = node({ type: "ProgressBar", name: "HealthBar", classes: ["bar", "hp"] });
    expect(selectorsOf(n)).toEqual([
      'UiSelector.Name("HealthBar")',
      'UiSelector.TypeName("ProgressBar")',
      'UiSelector.Class("bar")',
      'UiSelector.Class("hp")',
    ]);
  });

  it("omits an empty name and missing classes", () => {
    expect(selectorsOf(node({ type: "Label", name: "", classes: null }))).toEqual([
      'UiSelector.TypeName("Label")',
    ]);
  });

  it("returns nothing for an anonymous, typeless node", () => {
    expect(selectorsOf(node({ type: null, name: null }))).toEqual([]);
  });
});

describe("bestSelector", () => {
  it("prefers the name, falls back to the type, else null", () => {
    expect(bestSelector(node({ type: "X", name: "n" }))).toBe('UiSelector.Name("n")');
    expect(bestSelector(node({ type: "X", name: null }))).toBe('UiSelector.TypeName("X")');
    expect(bestSelector(node({ type: null, name: null }))).toBeNull();
  });
});

describe("nodeMatches", () => {
  const n = node({
    type: "ArmoryUnitSelectSlot",
    name: "Slot",
    text: "Pick",
    classes: ["pickable"],
  });

  it("matches type, name, text, or class, case-insensitively", () => {
    expect(nodeMatches(n, "ARMORY")).toBe(true);
    expect(nodeMatches(n, "slot")).toBe(true);
    expect(nodeMatches(n, "pic")).toBe(true);
    expect(nodeMatches(n, "PICKABLE")).toBe(true);
  });

  it("returns false when nothing contains the query", () => {
    expect(nodeMatches(n, "zzz")).toBe(false);
  });

  it("treats an empty query as a match", () => {
    expect(nodeMatches(node({ type: null }), "")).toBe(true);
  });
});

describe("truncate", () => {
  it("collapses runs of whitespace", () => {
    expect(truncate("  a   b\nc  ")).toBe("a b c");
  });

  it("caps long text with an ellipsis", () => {
    expect(truncate("x".repeat(50))).toBe(`${"x".repeat(40)}…`);
  });

  it("leaves short text untouched", () => {
    expect(truncate("short")).toBe("short");
  });
});

describe("styleEntries", () => {
  it("returns nothing when the node has no style", () => {
    expect(styleEntries(node({ style: null }))).toEqual([]);
  });

  it("orders known keys, stringifies numbers, and flags colours for a swatch", () => {
    const entries = styleEntries(
      node({ style: { color: "rgba(255, 0, 0, 1)", w: 46, display: "None" } }),
    );
    expect(entries).toEqual([
      { key: "display", value: "None", color: null },
      { key: "w", value: "46", color: null },
      { key: "color", value: "rgba(255, 0, 0, 1)", color: "rgba(255, 0, 0, 1)" },
    ]);
  });

  it("sorts unknown keys to the end, alphabetically", () => {
    const keys = styleEntries(node({ style: { zebra: 1, x: 2, apple: 3 } })).map((e) => e.key);
    expect(keys).toEqual(["x", "apple", "zebra"]);
  });
});
