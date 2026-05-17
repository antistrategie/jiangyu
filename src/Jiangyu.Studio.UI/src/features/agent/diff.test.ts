import { describe, it, expect } from "vitest";
import { computeLineDiff, diffStats } from "./diff";

describe("computeLineDiff", () => {
  it("returns no lines for two empty strings", () => {
    expect(computeLineDiff("", "")).toEqual([]);
  });

  it("treats a brand-new file as all-added", () => {
    const out = computeLineDiff("", "alpha\nbeta\ngamma");
    expect(out).toEqual([
      { kind: "added", text: "alpha" },
      { kind: "added", text: "beta" },
      { kind: "added", text: "gamma" },
    ]);
  });

  it("treats a deleted file as all-removed", () => {
    const out = computeLineDiff("alpha\nbeta", "");
    expect(out).toEqual([
      { kind: "removed", text: "alpha" },
      { kind: "removed", text: "beta" },
    ]);
  });

  it("emits no diff for identical inputs", () => {
    const out = computeLineDiff("alpha\nbeta\ngamma", "alpha\nbeta\ngamma");
    expect(out.every((l) => l.kind === "context")).toBe(true);
    expect(out.length).toBe(3);
  });

  it("flags single-line replacement as removed-then-added with surrounding context", () => {
    const out = computeLineDiff("alpha\nbeta\ngamma", "alpha\nBETA\ngamma");
    expect(out).toEqual([
      { kind: "context", text: "alpha" },
      { kind: "removed", text: "beta" },
      { kind: "added", text: "BETA" },
      { kind: "context", text: "gamma" },
    ]);
  });

  it("handles pure inserts in the middle", () => {
    const out = computeLineDiff("alpha\ngamma", "alpha\nbeta\ngamma");
    expect(out).toEqual([
      { kind: "context", text: "alpha" },
      { kind: "added", text: "beta" },
      { kind: "context", text: "gamma" },
    ]);
  });

  it("handles pure deletes in the middle", () => {
    const out = computeLineDiff("alpha\nbeta\ngamma", "alpha\ngamma");
    expect(out).toEqual([
      { kind: "context", text: "alpha" },
      { kind: "removed", text: "beta" },
      { kind: "context", text: "gamma" },
    ]);
  });

  it("preserves blank lines", () => {
    const out = computeLineDiff("alpha\n\ngamma", "alpha\nbeta\ngamma");
    // The empty middle line is removed and beta is added; alpha and gamma are
    // context. Order matters: the LCS walker produces removes before adds at
    // the same position, matching `git diff` convention.
    expect(out).toEqual([
      { kind: "context", text: "alpha" },
      { kind: "removed", text: "" },
      { kind: "added", text: "beta" },
      { kind: "context", text: "gamma" },
    ]);
  });

  it("handles full replacement", () => {
    const out = computeLineDiff("a\nb", "c\nd");
    expect(out).toEqual([
      { kind: "removed", text: "a" },
      { kind: "removed", text: "b" },
      { kind: "added", text: "c" },
      { kind: "added", text: "d" },
    ]);
  });
});

describe("diffStats", () => {
  it("counts added and removed lines", () => {
    const out = computeLineDiff("alpha\nbeta\ngamma", "alpha\nBETA\ndelta\ngamma");
    expect(diffStats(out)).toEqual({ added: 2, removed: 1 });
  });

  it("returns zeros for an unchanged diff", () => {
    expect(diffStats(computeLineDiff("a\nb", "a\nb"))).toEqual({ added: 0, removed: 0 });
  });

  it("counts a brand-new file as all-added", () => {
    expect(diffStats(computeLineDiff("", "x\ny\nz"))).toEqual({ added: 3, removed: 0 });
  });
});
