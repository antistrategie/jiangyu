import { describe, it, expect } from "vitest";
import {
  buildDescentMemberDirective,
  insertAtPendingAnchor,
  reorderDirectives,
  rewriteDescentSlotIndex,
  type StampedDirective,
} from "./helpers";

function dir(
  uiId: string,
  fieldPath: string,
  extras?: Partial<StampedDirective>,
): StampedDirective {
  return {
    op: "Set",
    fieldPath,
    value: { kind: "Int32", int32: 1 },
    _uiId: uiId,
    ...extras,
  };
}

describe("reorderDirectives", () => {
  it("moves a single row down", () => {
    const list = [dir("a", "A"), dir("b", "B"), dir("c", "C")];
    const next = reorderDirectives(list, "a", 3);
    expect(next.map((d) => d._uiId)).toEqual(["b", "c", "a"]);
  });

  it("moves a single row up", () => {
    const list = [dir("a", "A"), dir("b", "B"), dir("c", "C")];
    const next = reorderDirectives(list, "c", 0);
    expect(next.map((d) => d._uiId)).toEqual(["c", "a", "b"]);
  });

  it("returns the same list when fromId is unknown", () => {
    const list = [dir("a", "A"), dir("b", "B")];
    const next = reorderDirectives(list, "ghost", 0);
    expect(next).toEqual(list);
  });

  it("moves a descent group as a contiguous unit", () => {
    // Three-member descent group at indices [0, 1, 2], one loose row at 3.
    // Dragging the group's first member to slot 4 (after the loose row)
    // should move all three members together.
    const list = [
      dir("g0", "EventHandlers[0].A", { subtypeHints: { 0: "AddSkill" } }),
      dir("g1", "EventHandlers[0].B", { subtypeHints: { 0: "AddSkill" } }),
      dir("g2", "EventHandlers[0].C", { subtypeHints: { 0: "AddSkill" } }),
      dir("loose", "Cooldown"),
    ];
    const next = reorderDirectives(list, "g0", 4);
    expect(next.map((d) => d._uiId)).toEqual(["loose", "g0", "g1", "g2"]);
  });

  it("only the head id triggers group-span; non-head members move alone", () => {
    // Dragging g1 (the middle of the group) by its individual row grip
    // should move just g1, splitting the group as a side-effect (re-
    // grouping at render time absorbs the leftovers cleanly).
    const list = [
      dir("g0", "EventHandlers[0].A", { subtypeHints: { 0: "AddSkill" } }),
      dir("g1", "EventHandlers[0].B", { subtypeHints: { 0: "AddSkill" } }),
      dir("g2", "EventHandlers[0].C", { subtypeHints: { 0: "AddSkill" } }),
    ];
    const next = reorderDirectives(list, "g1", 0);
    expect(next.map((d) => d._uiId)).toEqual(["g1", "g0", "g2"]);
  });
});

describe("insertAtPendingAnchor", () => {
  it("end anchor pushes to the back", () => {
    const list = ["a", "b"];
    expect(insertAtPendingAnchor(list, "new", { kind: "end" })).toEqual(["a", "b", "new"]);
  });

  it("start anchor unshifts to the front", () => {
    const list = ["a", "b"];
    expect(insertAtPendingAnchor(list, "new", { kind: "start" })).toEqual(["new", "a", "b"]);
  });

  it("afterIndex anchor inserts immediately after the specified position", () => {
    const list = ["a", "b", "c"];
    expect(insertAtPendingAnchor(list, "new", { kind: "afterIndex", flatIndex: 0 })).toEqual([
      "a",
      "new",
      "b",
      "c",
    ]);
    expect(insertAtPendingAnchor(list, "new", { kind: "afterIndex", flatIndex: 1 })).toEqual([
      "a",
      "b",
      "new",
      "c",
    ]);
  });
});

describe("buildDescentMemberDirective", () => {
  it("prefixes the inner fieldPath with the outer field+index", () => {
    const out = buildDescentMemberDirective(
      "EventHandlers",
      0,
      "AddSkill",
      dir("id", "ShowHUDText"),
    );
    expect(out.fieldPath).toBe("EventHandlers[0].ShowHUDText");
    expect(out.subtypeHints).toEqual({ 0: "AddSkill" });
  });

  it("omits subtypeHints when subtype is null", () => {
    const out = buildDescentMemberDirective("Properties", 2, null, dir("id", "Amount"));
    expect(out.fieldPath).toBe("Properties[2].Amount");
    expect(out.subtypeHints).toBeUndefined();
  });

  it("preserves additional subtypeHints on the inner directive", () => {
    // Inner directive itself has a hint at segment 1 (descent into a
    // polymorphic field of the constructed instance). Materialising
    // should keep that hint while adding the outer's at segment 0.
    const inner = dir("id", "Properties[0].Amount", {
      subtypeHints: { 1: "PropertyChange" },
    });
    const out = buildDescentMemberDirective("EventHandlers", 0, "AddSkill", inner);
    expect(out.subtypeHints).toEqual({ 0: "AddSkill", 1: "PropertyChange" });
  });
});

describe("rewriteDescentSlotIndex", () => {
  it("rewrites every member fieldPath in the [start, end) slice", () => {
    const list = [
      dir("a", "EventHandlers[0].A"),
      dir("b", "EventHandlers[0].B"),
      dir("c", "Cooldown"),
      dir("d", "EventHandlers[1].X"),
    ];
    const next = rewriteDescentSlotIndex(list, 0, 2, "EventHandlers", 0, 5);
    expect(next.map((d) => d.fieldPath)).toEqual([
      "EventHandlers[5].A",
      "EventHandlers[5].B",
      "Cooldown",
      "EventHandlers[1].X",
    ]);
  });

  it("no-ops when newSlot equals oldSlot", () => {
    const list = [dir("a", "EventHandlers[0].A")];
    const next = rewriteDescentSlotIndex(list, 0, 1, "EventHandlers", 0, 0);
    expect(next).toBe(list);
  });

  it("no-ops on negative new slot (defensive against bad number-input)", () => {
    const list = [dir("a", "EventHandlers[0].A")];
    const next = rewriteDescentSlotIndex(list, 0, 1, "EventHandlers", 0, -1);
    expect(next).toBe(list);
  });
});
