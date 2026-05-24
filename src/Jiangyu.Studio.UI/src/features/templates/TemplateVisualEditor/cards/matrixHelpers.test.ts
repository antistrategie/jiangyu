import { describe, expect, it } from "vitest";
import type { EnumMemberEntry } from "@shared/rpc";
import { formatFlagsLabel, formatFlagsTitle, isFlagsEnum, stripArraySuffix } from "./matrixHelpers";

const m = (name: string, value: number): EnumMemberEntry => ({ name, value });

describe("isFlagsEnum", () => {
  it("returns true for a standard [Flags] enum (powers of two)", () => {
    expect(isFlagsEnum([m("None", 0), m("A", 1), m("B", 2), m("C", 4), m("D", 8)])).toBe(true);
  });

  it("returns false when fewer than two non-zero members are powers of two", () => {
    // Only one bit set besides zero — needs at least two to count as flags.
    expect(isFlagsEnum([m("None", 0), m("Only", 1)])).toBe(false);
  });

  it("returns false when any non-zero member isn't a power of two", () => {
    expect(isFlagsEnum([m("None", 0), m("A", 1), m("B", 3)])).toBe(false);
  });

  it("returns false on negative members (sign bit invalidates the bitmask shape)", () => {
    expect(isFlagsEnum([m("A", 1), m("B", 2), m("Bad", -1)])).toBe(false);
  });

  it("returns false on an empty member list", () => {
    expect(isFlagsEnum([])).toBe(false);
  });
});

describe("stripArraySuffix", () => {
  it("removes single-dim brackets", () => {
    expect(stripArraySuffix("Int32[]")).toBe("Int32");
  });

  it("removes multi-dim brackets", () => {
    expect(stripArraySuffix("Boolean[,]")).toBe("Boolean");
    expect(stripArraySuffix("Single[,,]")).toBe("Single");
  });

  it("leaves non-array type names untouched", () => {
    expect(stripArraySuffix("List<Int32>")).toBe("List<Int32>");
  });

  it("only strips a trailing pair (interior brackets stay)", () => {
    expect(stripArraySuffix("Dictionary<String,Int32[]>")).toBe("Dictionary<String,Int32[]>");
  });
});

describe("formatFlagsLabel", () => {
  it("renders the zero/None value as a placeholder dot", () => {
    expect(formatFlagsLabel(0)).toBe("·");
  });

  it("renders 1–15 as upper-case single-glyph hex", () => {
    expect(formatFlagsLabel(1)).toBe("1");
    expect(formatFlagsLabel(0xa)).toBe("A");
    expect(formatFlagsLabel(0xf)).toBe("F");
  });

  it("renders 16+ as decimal so the glyph stays unambiguous", () => {
    expect(formatFlagsLabel(16)).toBe("16");
    expect(formatFlagsLabel(255)).toBe("255");
  });
});

describe("formatFlagsTitle", () => {
  const members: EnumMemberEntry[] = [m("None", 0), m("Walk", 1), m("Run", 2), m("Jump", 4)];

  it("titles a zero cell with the named None member when one exists", () => {
    expect(formatFlagsTitle(0, members, 1, 2, false)).toBe("[1,2] None");
  });

  it("falls back to (none) when no zero-named member exists", () => {
    expect(formatFlagsTitle(0, [m("Walk", 1)], 0, 0, false)).toBe("[0,0] (none)");
  });

  it("lists the matching bit names for a composite mask", () => {
    // 1 | 2 = Walk | Run
    expect(formatFlagsTitle(3, members, 0, 0, false)).toBe("[0,0] Walk | Run");
  });

  it("appends the (edited) marker when the cell is pending a flush", () => {
    expect(formatFlagsTitle(1, members, 4, 5, true)).toBe("[4,5] (edited) Walk");
  });

  it("falls back to the numeric value when no named bits match", () => {
    // 16 has no member; output is the raw decimal.
    expect(formatFlagsTitle(16, members, 0, 0, false)).toBe("[0,0] 16");
  });
});
