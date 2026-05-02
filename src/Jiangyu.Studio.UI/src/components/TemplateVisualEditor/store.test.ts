import { describe, it, expect } from "vitest";
import {
  buildDescentMemberDirective,
  groupDirectives,
  insertAtPendingAnchor,
  reorderDirectives,
  rewriteDescentSlotIndex,
  stampDirective,
  stampNodes,
  stripUiIds,
  type StampedDirective,
} from "./helpers";
import type { DescentStep, EditorDirective, EditorDocument } from "./types";

// --- Factory helpers ---

function counter(): () => string {
  let n = 0;
  return () => `id${++n}`;
}

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

// --- reorderDirectives ---

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

// --- insertAtPendingAnchor ---

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

// --- buildDescentMemberDirective ---

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

// --- rewriteDescentSlotIndex ---

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
    expect(next[0]?.descent?.[0]).toEqual({
      field: "EventHandlers",
      index: 5,
      subtype: "AddSkill",
    });
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

// --- Integration: editor-doc round-trip ---

describe("editor-doc round-trip", () => {
  it("stamp → strip is identity on a doc with mixed loose / descent / composite directives", () => {
    const original: EditorDocument = {
      nodes: [
        {
          kind: "Patch",
          templateType: "PerkTemplate",
          templateId: "perk.x",
          directives: [
            { op: "Set", fieldPath: "Cooldown", value: { kind: "Single", single: 2.5 } },
            {
              op: "Set",
              fieldPath: "ShowHUDText",
              descent: [{ field: "EventHandlers", index: 0, subtype: "AddSkill" }],
              value: { kind: "Boolean", boolean: true },
            },
            {
              op: "Set",
              fieldPath: "OnlyApplyOnHit",
              descent: [{ field: "EventHandlers", index: 0, subtype: "AddSkill" }],
              value: { kind: "Boolean", boolean: true },
            },
            {
              op: "Append",
              fieldPath: "EventHandlers",
              value: {
                kind: "HandlerConstruction",
                compositeType: "ChangeProperty",
                compositeDirectives: [
                  {
                    op: "Append",
                    fieldPath: "Properties",
                    value: {
                      kind: "Composite",
                      compositeType: "PropertyChange",
                      compositeDirectives: [
                        { op: "Set", fieldPath: "Amount", value: { kind: "Int32", int32: 5 } },
                      ],
                    },
                  },
                ],
              },
            },
          ],
        },
      ],
      errors: [],
    };
    const stamped = { ...original, nodes: stampNodes(original.nodes, counter()) };
    const stripped = stripUiIds(stamped);
    expect(stripped).toEqual(original);
  });
});

// --- descent group lifecycle ---

describe("descent group lifecycle", () => {
  it("pending → materialise → second add stays in the same group", () => {
    const newId = counter();
    let directives: StampedDirective[] = [];

    const first = stampDirective(
      { op: "Set", fieldPath: "ShowHUDText", value: { kind: "Boolean", boolean: true } },
      newId,
    );
    const firstPrefixed = buildDescentMemberDirective("EventHandlers", 0, "AddSkill", first);
    directives = insertAtPendingAnchor(directives, firstPrefixed, { kind: "end" });

    expect(directives).toHaveLength(1);
    expect(directives[0]?.fieldPath).toBe("ShowHUDText");
    expect(directives[0]?.descent).toEqual([
      { field: "EventHandlers", index: 0, subtype: "AddSkill" },
    ]);

    const groups = groupDirectives(directives);
    expect(groups).toHaveLength(1);
    expect(groups[0]).toMatchObject({
      kind: "group",
      field: "EventHandlers",
      index: 0,
      subtype: "AddSkill",
      members: [{ suffix: "ShowHUDText" }],
    });

    const second = stampDirective(
      { op: "Set", fieldPath: "OnlyApplyOnHit", value: { kind: "Boolean", boolean: true } },
      newId,
    );
    const secondPrefixed = buildDescentMemberDirective("EventHandlers", 0, "AddSkill", second);
    directives = [...directives.slice(0, 1), secondPrefixed, ...directives.slice(1)];

    const updatedGroups = groupDirectives(directives);
    expect(updatedGroups).toHaveLength(1);
    expect(updatedGroups[0]).toMatchObject({
      kind: "group",
      members: [{ suffix: "ShowHUDText" }, { suffix: "OnlyApplyOnHit" }],
    });
  });

  it("clearing subtype anchors pending state, materialise puts new directive in original position", () => {
    const directives: StampedDirective[] = [
      stampDirective(
        { op: "Set", fieldPath: "Cooldown", value: { kind: "Single", single: 1 } },
        counter(),
      ),
      stampDirective(
        {
          op: "Set",
          fieldPath: "ShowHUDText",
          descent: [{ field: "EventHandlers", index: 0, subtype: "AddSkill" }],
          value: { kind: "Boolean", boolean: true },
        },
        counter(),
      ),
      stampDirective(
        {
          op: "Set",
          fieldPath: "OnlyApplyOnHit",
          descent: [{ field: "EventHandlers", index: 0, subtype: "AddSkill" }],
          value: { kind: "Boolean", boolean: true },
        },
        counter(),
      ),
      stampDirective(
        { op: "Set", fieldPath: "Tier", value: { kind: "Int32", int32: 2 } },
        counter(),
      ),
    ];

    const groupStart = 1;
    const groupEnd = 3;
    const afterClear = [...directives.slice(0, groupStart), ...directives.slice(groupEnd)];
    expect(afterClear.map((d) => d.fieldPath)).toEqual(["Cooldown", "Tier"]);

    const first = stampDirective(
      {
        op: "Set",
        fieldPath: "Event",
        value: { kind: "Enum", enumType: "EventType", enumValue: "OnUpdate" },
      },
      counter(),
    );
    const firstPrefixed = buildDescentMemberDirective("EventHandlers", 0, "ChangeProperty", first);
    const final = insertAtPendingAnchor(afterClear, firstPrefixed, {
      kind: "afterIndex",
      flatIndex: 0,
    });

    expect(final.map((d) => d.fieldPath)).toEqual(["Cooldown", "Event", "Tier"]);
    expect(final[1]?.descent).toEqual([
      { field: "EventHandlers", index: 0, subtype: "ChangeProperty" },
    ]);
  });
});

// --- descent group + reorder interaction ---

describe("descent group + reorder interaction", () => {
  it("reordering moves the whole group, leaving siblings undisturbed", () => {
    const list: StampedDirective[] = [
      { op: "Set", fieldPath: "A", value: { kind: "Int32", int32: 1 }, _uiId: "a" },
      {
        op: "Set",
        fieldPath: "X",
        descent: [{ field: "EventHandlers", index: 0, subtype: "AddSkill" }],
        value: { kind: "Boolean", boolean: true },
        _uiId: "g0",
      },
      {
        op: "Set",
        fieldPath: "Y",
        descent: [{ field: "EventHandlers", index: 0, subtype: "AddSkill" }],
        value: { kind: "Boolean", boolean: false },
        _uiId: "g1",
      },
      { op: "Set", fieldPath: "B", value: { kind: "Int32", int32: 2 }, _uiId: "b" },
    ];

    const next = reorderDirectives(list, "g0", 4);
    expect(next.map((d) => d._uiId)).toEqual(["a", "b", "g0", "g1"]);
    const groups = groupDirectives(next);
    const groupCount = groups.filter((g) => g.kind === "group").length;
    expect(groupCount).toBe(1);
  });
});

// --- rewriteDescentSlotIndex composes with grouping ---

describe("rewriteDescentSlotIndex composes with grouping", () => {
  it("changing slot N→M keeps the group as one contiguous run", () => {
    const list: StampedDirective[] = [
      {
        op: "Set",
        fieldPath: "A",
        descent: [{ field: "EventHandlers", index: 0, subtype: "X" }],
        value: { kind: "Int32", int32: 1 },
        _uiId: "g0",
      },
      {
        op: "Set",
        fieldPath: "B",
        descent: [{ field: "EventHandlers", index: 0, subtype: "X" }],
        value: { kind: "Int32", int32: 2 },
        _uiId: "g1",
      },
    ];
    const updated: StampedDirective[] = rewriteDescentSlotIndex(
      list as EditorDirective[],
      0,
      2,
      "EventHandlers",
      0,
      3,
    ) as StampedDirective[];
    const groups = groupDirectives(updated);
    expect(groups).toHaveLength(1);
    expect(groups[0]).toMatchObject({ kind: "group", index: 3 });
  });
});
