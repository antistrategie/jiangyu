import { describe, expect, it } from "vitest";
import { clampZoom, zoomTowardsCursor } from "./zoomMath.ts";

describe("clampZoom", () => {
  it("returns the value when within bounds", () => {
    expect(clampZoom(2, 0.1, 32)).toBe(2);
  });

  it("clamps to minimum", () => {
    expect(clampZoom(0.01, 0.1, 32)).toBe(0.1);
  });

  it("clamps to maximum", () => {
    expect(clampZoom(100, 0.1, 32)).toBe(32);
  });

  it("returns min when value equals min", () => {
    expect(clampZoom(0.1, 0.1, 32)).toBe(0.1);
  });

  it("returns max when value equals max", () => {
    expect(clampZoom(32, 0.1, 32)).toBe(32);
  });
});

describe("zoomTowardsCursor", () => {
  it("preserves the cursor-point invariant", () => {
    // The image-space point under the cursor should be the same before and
    // after the zoom. In image-space: imagePoint = (cursor - offset) / zoom.
    const cursor = 200;
    const oldOffset = 50;
    const oldZoom = 1;
    const newZoom = 2;

    const newOffset = zoomTowardsCursor(cursor, oldOffset, oldZoom, newZoom);

    const imagePtBefore = (cursor - oldOffset) / oldZoom;
    const imagePtAfter = (cursor - newOffset) / newZoom;
    expect(imagePtAfter).toBeCloseTo(imagePtBefore, 10);
  });

  it("preserves cursor-point invariant when zooming out", () => {
    const cursor = 300;
    const oldOffset = -100;
    const oldZoom = 4;
    const newZoom = 1;

    const newOffset = zoomTowardsCursor(cursor, oldOffset, oldZoom, newZoom);

    const imagePtBefore = (cursor - oldOffset) / oldZoom;
    const imagePtAfter = (cursor - newOffset) / newZoom;
    expect(imagePtAfter).toBeCloseTo(imagePtBefore, 10);
  });

  it("is a no-op when zoom does not change", () => {
    const cursor = 150;
    const oldOffset = 30;
    const oldZoom = 2;

    const newOffset = zoomTowardsCursor(cursor, oldOffset, oldZoom, oldZoom);
    expect(newOffset).toBeCloseTo(oldOffset, 10);
  });

  it("works with cursor at origin (offset zero)", () => {
    const cursor = 0;
    const oldOffset = 0;
    const oldZoom = 1;
    const newZoom = 3;

    const newOffset = zoomTowardsCursor(cursor, oldOffset, oldZoom, newZoom);
    expect(newOffset).toBeCloseTo(0, 10);
  });

  it("works with negative offsets", () => {
    const cursor = 100;
    const oldOffset = -200;
    const oldZoom = 2;
    const newZoom = 4;

    const newOffset = zoomTowardsCursor(cursor, oldOffset, oldZoom, newZoom);

    const imagePtBefore = (cursor - oldOffset) / oldZoom;
    const imagePtAfter = (cursor - newOffset) / newZoom;
    expect(imagePtAfter).toBeCloseTo(imagePtBefore, 10);
  });
});
