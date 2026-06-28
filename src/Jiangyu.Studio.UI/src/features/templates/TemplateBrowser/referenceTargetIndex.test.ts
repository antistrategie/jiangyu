import { describe, it, expect } from "vitest";
import type { TemplateInstanceEntry } from "@shared/rpc";
import { buildReferenceTargetIndex, resolveReferenceTargetKey } from "./helpers";

function inst(collection: string, pathId: number, name: string): TemplateInstanceEntry {
  return { name, className: "UnitTemplate", identity: { collection, pathId } };
}

// References carry a pathId and sometimes a name; their fileId is a
// dependency index rather than a collection name, so pathIds can repeat
// across collections. The index must resolve named references to the
// name-matching instance and unnamed ones to the first instance with the
// pathId, mirroring iteration order over the instance list.
describe("referenceTargetIndex", () => {
  const instances = [
    inst("units", 10, "archer"),
    inst("buildings", 10, "barracks"),
    inst("units", 20, "swordsman"),
  ];
  const index = buildReferenceTargetIndex(instances);

  it("resolves a named reference to the name-matching instance", () => {
    expect(resolveReferenceTargetKey(index, 10, "barracks")).toBe("buildings:10");
  });

  it("resolves an unnamed reference to the sole instance with an unambiguous pathId", () => {
    expect(resolveReferenceTargetKey(index, 20, undefined)).toBe("units:20");
  });

  it("returns null for an unnamed reference at an ambiguous pathId", () => {
    // pathId 10 is owned by both units:10 and buildings:10.
    expect(resolveReferenceTargetKey(index, 10, undefined)).toBeNull();
  });

  it("keeps the first entry on (pathId, name) collisions", () => {
    const dup = buildReferenceTargetIndex([inst("a", 1, "x"), inst("b", 1, "x")]);
    expect(resolveReferenceTargetKey(dup, 1, "x")).toBe("a:1");
  });

  it("returns null for an unknown pathId", () => {
    expect(resolveReferenceTargetKey(index, 99, undefined)).toBeNull();
    expect(resolveReferenceTargetKey(index, 99, "archer")).toBeNull();
  });

  it("returns null when the name does not match any instance at the pathId", () => {
    expect(resolveReferenceTargetKey(index, 20, "archer")).toBeNull();
  });

  it("returns null for a null name (instance names are always strings)", () => {
    expect(resolveReferenceTargetKey(index, 10, null)).toBeNull();
  });

  it("treats an empty-string name like a missing name (falls back to unambiguous pathId)", () => {
    expect(resolveReferenceTargetKey(index, 20, "")).toBe("units:20");
  });
});
