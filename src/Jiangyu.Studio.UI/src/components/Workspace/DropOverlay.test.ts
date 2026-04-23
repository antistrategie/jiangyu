import { describe, expect, it } from "vitest";
import { zoneFor } from "./DropOverlay.tsx";

// 100x100 grid; edge band is the outer 25% (EDGE_FRACTION in zoneFor).
const W = 100;
const H = 100;

describe("zoneFor", () => {
  it("returns centre when the cursor is inside the central 50% band", () => {
    expect(zoneFor(50, 50, W, H)).toBe("centre");
    expect(zoneFor(30, 30, W, H)).toBe("centre");
    expect(zoneFor(70, 70, W, H)).toBe("centre");
  });

  it("returns the nearer horizontal edge when the cursor is closer to left/right than to top/bottom", () => {
    expect(zoneFor(5, 50, W, H)).toBe("left");
    expect(zoneFor(95, 50, W, H)).toBe("right");
  });

  it("returns the nearer vertical edge when the cursor is closer to top/bottom than to left/right", () => {
    expect(zoneFor(50, 5, W, H)).toBe("top");
    expect(zoneFor(50, 95, W, H)).toBe("bottom");
  });

  it("picks the strictly-closer edge in a lopsided corner", () => {
    // Closer to the top edge (fy=0.03) than to left/right (fx=0.10) → "top".
    expect(zoneFor(10, 3, W, H)).toBe("top");
    // Closer to left (fx=0.03) than to top/bottom (fy=0.10) → "left".
    expect(zoneFor(3, 10, W, H)).toBe("left");
    // Closer to bottom than to right.
    expect(zoneFor(90, 97, W, H)).toBe("bottom");
    // Closer to right than to top.
    expect(zoneFor(97, 10, W, H)).toBe("right");
  });

  it("handles non-square rectangles", () => {
    // Wide rectangle: the 25% edge band at left is 0..100 of a 400 width.
    expect(zoneFor(10, 100, 400, 200)).toBe("left");
    expect(zoneFor(200, 100, 400, 200)).toBe("centre");
    expect(zoneFor(390, 100, 400, 200)).toBe("right");
  });
});
