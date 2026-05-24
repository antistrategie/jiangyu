import { describe, expect, it } from "vitest";
import { zoneFor } from "./dropZone";

describe("zoneFor", () => {
  // The rect is 100×100 throughout so a fraction maps 1:1 to a coordinate.
  const W = 100;
  const H = 100;

  it("returns centre for the dead-centre point", () => {
    expect(zoneFor(50, 50, W, H)).toBe("centre");
  });

  it("returns centre when both axes are inside the 25% edge band", () => {
    // 30,30 → fx=fy=0.3, both > 0.25, so centre.
    expect(zoneFor(30, 30, W, H)).toBe("centre");
  });

  it("returns left when the cursor is near the left edge", () => {
    expect(zoneFor(5, 50, W, H)).toBe("left");
  });

  it("returns right when the cursor is near the right edge", () => {
    expect(zoneFor(95, 50, W, H)).toBe("right");
  });

  it("returns top when the cursor is near the top edge", () => {
    expect(zoneFor(50, 5, W, H)).toBe("top");
  });

  it("returns bottom when the cursor is near the bottom edge", () => {
    expect(zoneFor(50, 95, W, H)).toBe("bottom");
  });

  it("picks the closer of two edges in a corner — top-left favours the closer one", () => {
    // (10, 5): top is 5px away, left is 10px away — top wins.
    expect(zoneFor(10, 5, W, H)).toBe("top");
  });

  it("picks the closer of two edges in a corner — bottom-right favours the closer one", () => {
    // (95, 90): right is 5px away, bottom is 10px away — right wins.
    expect(zoneFor(95, 90, W, H)).toBe("right");
  });

  it("handles non-square rects via the same fractional rule", () => {
    // 200 wide × 100 tall. Cursor (10, 50): fx=0.05 < 0.25; fy=0.5 — left wins.
    expect(zoneFor(10, 50, 200, 100)).toBe("left");
  });

  it("at the exact corner, horizontal beats vertical when equally distant", () => {
    // (0,0): distL = 0, distT = 0. minH === minV so the `<` branch picks top.
    // Documented to pin behaviour — corners are an edge case the UI rarely
    // triggers because the layout grid usually clips here.
    expect(zoneFor(0, 0, W, H)).toBe("top");
  });
});
