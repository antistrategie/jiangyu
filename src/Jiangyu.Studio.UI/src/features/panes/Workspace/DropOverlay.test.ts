import { describe, expect, it } from "vitest";
import { zoneFor } from "./dropZone";
import { acceptsPaneDropDragData } from "./dropOverlayDragData";

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

describe("acceptsPaneDropDragData", () => {
  const ACCEPTED = ["application/x-jiangyu-tab", "application/x-jiangyu-pane"] as const;

  function data(types: readonly string[], plain = "") {
    return {
      types,
      getData: (format: string) => (format === "text/plain" ? plain : ""),
    };
  }

  it("accepts known pane/tab drag MIME types", () => {
    expect(acceptsPaneDropDragData(data(["application/x-jiangyu-tab"]), ACCEPTED)).toBe(true);
    expect(acceptsPaneDropDragData(data(["application/x-jiangyu-pane"]), ACCEPTED)).toBe(true);
  });

  it("rejects template drag tag MIME", () => {
    expect(
      acceptsPaneDropDragData(
        data(["application/x-jiangyu-template-drag", "application/x-jiangyu-tab"]),
        ACCEPTED,
      ),
    ).toBe(false);
  });

  it("rejects template payload markers carried in text/plain", () => {
    const instanceRaw = JSON.stringify({
      m: "jiangyu-instance-drag/1",
      name: "unit.alpha",
      className: "EntityTemplate",
    });
    const memberRaw = JSON.stringify({
      m: "jiangyu-member-drag/1",
      templateType: "EntityTemplate",
      fieldPath: "Health",
    });

    expect(acceptsPaneDropDragData(data(["text/plain"], instanceRaw), ACCEPTED)).toBe(false);
    expect(acceptsPaneDropDragData(data(["text/plain"], memberRaw), ACCEPTED)).toBe(false);
  });

  it("ignores unrelated text/plain payloads", () => {
    expect(acceptsPaneDropDragData(data(["text/plain"], "random"), ACCEPTED)).toBe(false);
    expect(
      acceptsPaneDropDragData(
        data(["application/x-jiangyu-tab", "text/plain"], "random"),
        ACCEPTED,
      ),
    ).toBe(true);
  });
});
