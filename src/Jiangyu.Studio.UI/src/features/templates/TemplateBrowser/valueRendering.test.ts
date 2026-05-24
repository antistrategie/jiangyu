import { describe, expect, it } from "vitest";
import type { InspectedFieldNode } from "@shared/rpc";
import {
  buildNamedArrayLabelMap,
  formatMatrixCell,
  formatValue,
  valueNodeKindIsScalar,
} from "./helpers";

function node(overrides: Partial<InspectedFieldNode>): InspectedFieldNode {
  return { kind: "object", ...overrides };
}

describe("formatValue", () => {
  it("renders an ellipsis for a missing value", () => {
    expect(formatValue(null)).toBe("…");
  });

  it("renders null sentinel", () => {
    expect(formatValue(node({ kind: "object", null: true }))).toBe("null");
  });

  it("renders a scalar value via String()", () => {
    expect(formatValue(node({ kind: "int", value: 42 }))).toBe("42");
  });

  it("renders string scalars via String()", () => {
    // The kind:"string" + JSON.stringify branch is unreachable because the
    // earlier `value !== undefined && value !== null` guard returns first;
    // strings just hit the generic scalar path. Documented here so a future
    // refactor that reorders the branches doesn't silently change the
    // collapsed-row preview.
    expect(formatValue(node({ kind: "string", value: "hello" }))).toBe("hello");
  });

  it("renders a reference by its name when the inspector resolved one", () => {
    expect(
      formatValue(node({ kind: "reference", reference: { pathId: 12, name: "MyTemplate" } })),
    ).toBe("MyTemplate");
  });

  it("falls back to pathId for an unresolved reference", () => {
    expect(formatValue(node({ kind: "reference", reference: { pathId: 99 } }))).toBe("[pathId=99]");
  });

  it("renders empty array as []", () => {
    expect(formatValue(node({ kind: "array", elements: [] }))).toBe("[]");
  });

  it("inlines short scalar arrays", () => {
    expect(
      formatValue(
        node({
          kind: "array",
          elements: [
            { kind: "int", value: 1 },
            { kind: "int", value: 2 },
            { kind: "int", value: 3 },
          ],
        }),
      ),
    ).toBe("[1, 2, 3]");
  });

  it("summarises long arrays with a count", () => {
    expect(formatValue(node({ kind: "array", count: 50 }))).toBe("[50 items]");
  });

  it("summarises arrays longer than 4 even when all elements are scalar", () => {
    // The inline rule cuts off above 4 — anything longer collapses to count.
    expect(
      formatValue(
        node({
          kind: "array",
          elements: [
            { kind: "int", value: 1 },
            { kind: "int", value: 2 },
            { kind: "int", value: 3 },
            { kind: "int", value: 4 },
            { kind: "int", value: 5 },
          ],
        }),
      ),
    ).toBe("[5 items]");
  });

  it("summarises objects with a singular field count", () => {
    expect(
      formatValue(
        node({
          kind: "object",
          fields: [{ kind: "int", value: 1 }],
        }),
      ),
    ).toBe("{ 1 field }");
  });

  it("summarises objects with a plural field count", () => {
    expect(formatValue(node({ kind: "object", fields: [] }))).toBe("{ 0 fields }");
  });

  it("renders assetReference with the resolved asset name", () => {
    const value = node({ kind: "assetReference" }) as InspectedFieldNode & {
      asset?: { name?: string } | null;
    };
    value.asset = { name: "Helmet" };
    expect(formatValue(value)).toBe("→ Helmet");
  });

  it("renders assetReference with question mark when the asset has no name", () => {
    expect(formatValue(node({ kind: "assetReference" }))).toBe("→ ?");
  });
});

describe("formatMatrixCell", () => {
  it("renders bool true as solid square", () => {
    expect(formatMatrixCell(node({ kind: "bool", value: true }))).toBe("■");
  });

  it("renders bool false as a dot", () => {
    expect(formatMatrixCell(node({ kind: "bool", value: false }))).toBe("·");
  });

  it("renders a numeric scalar via String()", () => {
    expect(formatMatrixCell(node({ kind: "int", value: 7 }))).toBe("7");
  });

  it("renders missing or null cells as a dot", () => {
    expect(formatMatrixCell(undefined)).toBe("·");
    expect(formatMatrixCell(node({ kind: "int" }))).toBe("·");
  });
});

describe("valueNodeKindIsScalar", () => {
  it("returns false for array, object, and reference", () => {
    expect(valueNodeKindIsScalar(node({ kind: "array" }))).toBe(false);
    expect(valueNodeKindIsScalar(node({ kind: "object" }))).toBe(false);
    expect(valueNodeKindIsScalar(node({ kind: "reference" }))).toBe(false);
  });

  it("returns true for primitive kinds", () => {
    expect(valueNodeKindIsScalar(node({ kind: "int" }))).toBe(true);
    expect(valueNodeKindIsScalar(node({ kind: "bool" }))).toBe(true);
    expect(valueNodeKindIsScalar(node({ kind: "string" }))).toBe(true);
  });
});

describe("buildNamedArrayLabelMap", () => {
  it("indexes members by their numeric value", () => {
    expect(
      buildNamedArrayLabelMap([
        { name: "Helmet", value: 0 },
        { name: "ItemSlot", value: 9 },
      ]),
    ).toEqual({ 0: "Helmet", 9: "ItemSlot" });
  });

  it("returns null when members is null or undefined", () => {
    expect(buildNamedArrayLabelMap(null)).toBeNull();
    expect(buildNamedArrayLabelMap(undefined)).toBeNull();
  });

  it("collapses duplicates with last-wins (defensive — index never collides on real data)", () => {
    expect(
      buildNamedArrayLabelMap([
        { name: "First", value: 1 },
        { name: "Second", value: 1 },
      ]),
    ).toEqual({ 1: "Second" });
  });
});
