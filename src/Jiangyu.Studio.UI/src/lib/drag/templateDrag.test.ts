import { describe, expect, it } from "vitest";
import { isTemplateDragPayload } from "@lib/drag/templateDrag.ts";

describe("isTemplateDragPayload", () => {
  function dragData(types: readonly string[], plain = "") {
    return {
      types,
      getData: (format: string) => (format === "text/plain" ? plain : ""),
    };
  }

  it("accepts template tag MIME types", () => {
    expect(isTemplateDragPayload(dragData(["application/x-jiangyu-template-drag"]))).toBe(true);
    expect(isTemplateDragPayload(dragData(["application/x-jiangyu-instance-drag"]))).toBe(true);
    expect(isTemplateDragPayload(dragData(["application/x-jiangyu-member-drag"]))).toBe(true);
  });

  it("accepts cross-window template payload markers in text/plain", () => {
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
    expect(isTemplateDragPayload(dragData(["text/plain"], instanceRaw))).toBe(true);
    expect(isTemplateDragPayload(dragData(["text/plain"], memberRaw))).toBe(true);
  });

  it("rejects unrelated plain text", () => {
    expect(isTemplateDragPayload(dragData(["text/plain"], "hello"))).toBe(false);
    expect(isTemplateDragPayload(dragData(["application/x-jiangyu-tab"]))).toBe(false);
  });

  it("returns false when text/plain cannot be read", () => {
    const data = {
      types: ["text/plain"],
      getData: () => {
        throw new Error("blocked");
      },
    };
    expect(isTemplateDragPayload(data)).toBe(false);
  });
});
