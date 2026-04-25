import { describe, expect, it } from "vitest";
import { encodeCrossMemberPayload, parseCrossMemberPayload } from "@lib/drag/crossMember.ts";

describe("encode/parseCrossMemberPayload", () => {
  it("round-trips a minimal member drag payload", () => {
    const raw = encodeCrossMemberPayload({
      templateType: "EntityTemplate",
      fieldPath: "DisplayName",
    });
    expect(parseCrossMemberPayload(raw)).toEqual({
      m: "jiangyu-member-drag/1",
      templateType: "EntityTemplate",
      fieldPath: "DisplayName",
    });
  });

  it("preserves optional descriptors (scalar kind, enum type, ref target)", () => {
    const raw = encodeCrossMemberPayload({
      templateType: "UnitLeaderTemplate",
      fieldPath: "Faction",
      patchScalarKind: "Enum",
      enumTypeName: "FactionTag",
      referenceTypeName: "PerkTreeTemplate",
    });
    const parsed = parseCrossMemberPayload(raw);
    expect(parsed?.patchScalarKind).toBe("Enum");
    expect(parsed?.enumTypeName).toBe("FactionTag");
    expect(parsed?.referenceTypeName).toBe("PerkTreeTemplate");
  });

  it("returns null for empty input", () => {
    expect(parseCrossMemberPayload("")).toBeNull();
  });

  it("returns null for non-JSON input", () => {
    expect(parseCrossMemberPayload("not json")).toBeNull();
  });

  it("returns null when the marker is missing", () => {
    expect(
      parseCrossMemberPayload(JSON.stringify({ templateType: "X", fieldPath: "Y" })),
    ).toBeNull();
  });

  it("returns null when the marker is wrong (e.g. crossInstance payload)", () => {
    const raw = JSON.stringify({
      m: "jiangyu-instance-drag/1",
      templateType: "X",
      fieldPath: "Y",
    });
    expect(parseCrossMemberPayload(raw)).toBeNull();
  });

  it("rejects payloads with non-string required fields", () => {
    const raw = JSON.stringify({
      m: "jiangyu-member-drag/1",
      templateType: 42,
      fieldPath: "Y",
    });
    expect(parseCrossMemberPayload(raw)).toBeNull();
  });

  it("returns null for JSON null/primitives without throwing", () => {
    expect(parseCrossMemberPayload("null")).toBeNull();
    expect(parseCrossMemberPayload("0")).toBeNull();
    expect(parseCrossMemberPayload('"hello"')).toBeNull();
  });
});
