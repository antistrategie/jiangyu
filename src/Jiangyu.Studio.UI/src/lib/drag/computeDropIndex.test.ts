import { describe, expect, it } from "vitest";
import { computeTabDropIndex } from "./computeDropIndex.ts";

// Fake DOM container that returns a canned NodeList of tab stubs with
// predictable bounding rects — avoids spinning up jsdom just for geometry.
function fakeContainer(tabs: { left: number; width: number }[]): Element {
  const elements = tabs.map((t) => ({
    getBoundingClientRect: () => ({ left: t.left, width: t.width }) as DOMRect,
  }));
  return {
    querySelectorAll: (_selector: string) => elements as unknown as NodeListOf<HTMLElement>,
  } as unknown as Element;
}

describe("computeTabDropIndex", () => {
  // Three 100px-wide tabs laid out end-to-end: [0..100][100..200][200..300].
  const three = fakeContainer([
    { left: 0, width: 100 },
    { left: 100, width: 100 },
    { left: 200, width: 100 },
  ]);

  it("returns 0 when the cursor is left of every tab midpoint", () => {
    expect(computeTabDropIndex(three, 10)).toBe(0);
  });

  it("returns 1 when the cursor is past the first tab's midpoint", () => {
    expect(computeTabDropIndex(three, 60)).toBe(1);
  });

  it("returns 2 when the cursor is past the second tab's midpoint", () => {
    expect(computeTabDropIndex(three, 160)).toBe(2);
  });

  it("returns tabs.length (end) when the cursor is past the last tab's midpoint", () => {
    expect(computeTabDropIndex(three, 260)).toBe(3);
  });

  it("treats the midpoint itself as 'after' — strict-less-than comparison", () => {
    // clientX === left + width/2 lands on the NEXT index, since the rule is
    // "left of midpoint" for insertion.
    expect(computeTabDropIndex(three, 50)).toBe(1);
  });

  it("returns 0 for an empty tab bar", () => {
    expect(computeTabDropIndex(fakeContainer([]), 100)).toBe(0);
  });
});
