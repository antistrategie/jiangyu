import { describe, it, expect } from "vitest";
import {
  buildDescentMemberDirective,
  insertAtPendingAnchor,
  reorderDirectives,
  rewriteDescentSlotIndex,
  type StampedDirective,
} from "./helpers";
import type { DescentStep } from "./types";

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
    const eventHandlerStep: DescentStep[] = [
      { field: "EventHandlers", index: 0, subtype: "AddSkill" },
    ];
    const list = [
      dir("g0", "A", { descent: eventHandlerStep }),
      dir("g1", "B", { descent: eventHandlerStep }),
      dir("g2", "C", { descent: eventHandlerStep }),
      dir("loose", "Cooldown"),
    ];
    const next = reorderDirectives(list, "g0", 4);
    expect(next.map((d) => d._uiId)).toEqual(["loose", "g0", "g1", "g2"]);
  });

  it("only the head id triggers group-span; non-head members move alone", () => {
    // Dragging g1 (the middle of the group) by its individual row grip
    // should move just g1, splitting the group as a side-effect (re-
    // grouping at render time absorbs the leftovers cleanly).
    const eventHandlerStep: DescentStep[] = [
      { field: "EventHandlers", index: 0, subtype: "AddSkill" },
    ];
    const list = [
      dir("g0", "A", { descent: eventHandlerStep }),
      dir("g1", "B", { descent: eventHandlerStep }),
      dir("g2", "C", { descent: eventHandlerStep }),
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
  it("prepends an outer descent step to the inner directive", () => {
    const out = buildDescentMemberDirective(
      "EventHandlers",
      0,
      "AddSkill",
      dir("id", "ShowHUDText"),
    );
    expect(out.fieldPath).toBe("ShowHUDText");
    expect(out.descent).toEqual([{ field: "EventHandlers", index: 0, subtype: "AddSkill" }]);
  });

  it("omits subtype on the outer step when subtype is null", () => {
    const out = buildDescentMemberDirective("Properties", 2, null, dir("id", "Amount"));
    expect(out.fieldPath).toBe("Amount");
    expect(out.descent).toEqual([{ field: "Properties", index: 2 }]);
    expect(out.descent?.[0]?.subtype).toBeUndefined();
  });

  it("preserves the inner directive's existing descent steps", () => {
    // Inner directive itself has a descent step (descent into a
    // polymorphic field of the constructed instance). Materialising
    // should keep that step while prepending the outer at segment 0.
    const inner = dir("id", "Amount", {
      descent: [{ field: "Properties", index: 0, subtype: "PropertyChange" }],
    });
    const out = buildDescentMemberDirective("EventHandlers", 0, "AddSkill", inner);
    expect(out.descent).toEqual([
      { field: "EventHandlers", index: 0, subtype: "AddSkill" },
      { field: "Properties", index: 0, subtype: "PropertyChange" },
    ]);
  });
});

describe("rewriteDescentSlotIndex", () => {
  it("rewrites the outer descent step's index in the [start, end) slice", () => {
    const handlerSlot0: DescentStep[] = [{ field: "EventHandlers", index: 0, subtype: "AddSkill" }];
    const handlerSlot1: DescentStep[] = [{ field: "EventHandlers", index: 1, subtype: "AddSkill" }];
    const list = [
      dir("a", "A", { descent: handlerSlot0 }),
      dir("b", "B", { descent: handlerSlot0 }),
      dir("c", "Cooldown"),
      dir("d", "X", { descent: handlerSlot1 }),
    ];
    const next = rewriteDescentSlotIndex(list, 0, 2, "EventHandlers", 0, 5);
    expect(next.map((d) => d.descent?.[0]?.index ?? null)).toEqual([5, 5, null, 1]);
    // Other step properties (field, subtype) survive intact.
    expect(next[0]?.descent?.[0]).toEqual({
      field: "EventHandlers",
      index: 5,
      subtype: "AddSkill",
    });
    // The fieldPath is unchanged (it's now inner-relative).
    expect(next.map((d) => d.fieldPath)).toEqual(["A", "B", "Cooldown", "X"]);
  });

  it("no-ops when newSlot equals oldSlot", () => {
    const list = [
      dir("a", "A", {
        descent: [{ field: "EventHandlers", index: 0, subtype: "AddSkill" }],
      }),
    ];
    const next = rewriteDescentSlotIndex(list, 0, 1, "EventHandlers", 0, 0);
    expect(next).toBe(list);
  });

  it("no-ops on negative new slot (defensive against bad number-input)", () => {
    const list = [
      dir("a", "A", {
        descent: [{ field: "EventHandlers", index: 0, subtype: "AddSkill" }],
      }),
    ];
    const next = rewriteDescentSlotIndex(list, 0, 1, "EventHandlers", 0, -1);
    expect(next).toBe(list);
  });
});
