import { describe, it, expect } from "vitest";
import {
  buildDescentMemberDirective,
  groupDirectives,
  insertAtPendingAnchor,
  reorderByUiId,
  reorderDirectives,
  rewriteDescentSlotIndex,
  stampDirective,
  stampNodes,
  stripUiIds,
  type StampedDirective,
} from "./helpers";
import { pushUndoFrame, undoCoalesceKey, UNDO_COALESCE_WINDOW_MS, UNDO_STACK_LIMIT } from "./store";
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
    const eventHandlerStep: DescentStep[] = [{ field: "EventHandlers", index: 0 }];
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
    const eventHandlerStep: DescentStep[] = [{ field: "EventHandlers", index: 0 }];
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
  it("prepends a no-subtype outer descent step to the inner directive", () => {
    const out = buildDescentMemberDirective("EventHandlers", 0, dir("id", "ShowHUDText"));
    expect(out.fieldPath).toBe("ShowHUDText");
    expect(out.descent).toEqual([{ field: "EventHandlers", index: 0 }]);
  });

  it("omits the index for an object-field edit (null slot)", () => {
    const out = buildDescentMemberDirective("AIRole", null, dir("id", "AvoidOpponents"));
    expect(out.fieldPath).toBe("AvoidOpponents");
    expect(out.descent).toEqual([{ field: "AIRole" }]);
  });

  it("preserves the inner directive's existing descent steps", () => {
    const inner = dir("id", "Amount", {
      descent: [{ field: "Properties", index: 0 }],
    });
    const out = buildDescentMemberDirective("EventHandlers", 0, inner);
    expect(out.descent).toEqual([
      { field: "EventHandlers", index: 0 },
      { field: "Properties", index: 0 },
    ]);
  });
});

// --- rewriteDescentSlotIndex ---

describe("rewriteDescentSlotIndex", () => {
  it("rewrites the outer descent step's index in the [start, end) slice", () => {
    const handlerSlot0: DescentStep[] = [{ field: "EventHandlers", index: 0 }];
    const handlerSlot1: DescentStep[] = [{ field: "EventHandlers", index: 1 }];
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
    });
    expect(next.map((d) => d.fieldPath)).toEqual(["A", "B", "Cooldown", "X"]);
  });

  it("no-ops when newSlot equals oldSlot", () => {
    const list = [
      dir("a", "A", {
        descent: [{ field: "EventHandlers", index: 0 }],
      }),
    ];
    const next = rewriteDescentSlotIndex(list, 0, 1, "EventHandlers", 0, 0);
    expect(next).toBe(list);
  });

  it("no-ops on negative new slot (defensive against bad number-input)", () => {
    const list = [
      dir("a", "A", {
        descent: [{ field: "EventHandlers", index: 0 }],
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
              descent: [{ field: "EventHandlers", index: 0 }],
              value: { kind: "Boolean", boolean: true },
            },
            {
              op: "Set",
              fieldPath: "OnlyApplyOnHit",
              descent: [{ field: "EventHandlers", index: 0 }],
              value: { kind: "Boolean", boolean: true },
            },
            {
              op: "Append",
              fieldPath: "EventHandlers",
              value: {
                kind: "TypeConstruction",
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
    const firstPrefixed = buildDescentMemberDirective("EventHandlers", 0, first);
    directives = insertAtPendingAnchor(directives, firstPrefixed, { kind: "end" });

    expect(directives).toHaveLength(1);
    expect(directives[0]?.fieldPath).toBe("ShowHUDText");
    expect(directives[0]?.descent).toEqual([{ field: "EventHandlers", index: 0 }]);

    const groups = groupDirectives(directives);
    expect(groups).toHaveLength(1);
    expect(groups[0]).toMatchObject({
      kind: "group",
      field: "EventHandlers",
      index: 0,
      members: [{ suffix: "ShowHUDText" }],
    });

    const second = stampDirective(
      { op: "Set", fieldPath: "OnlyApplyOnHit", value: { kind: "Boolean", boolean: true } },
      newId,
    );
    const secondPrefixed = buildDescentMemberDirective("EventHandlers", 0, second);
    directives = [...directives.slice(0, 1), secondPrefixed, ...directives.slice(1)];

    const updatedGroups = groupDirectives(directives);
    expect(updatedGroups).toHaveLength(1);
    expect(updatedGroups[0]).toMatchObject({
      kind: "group",
      members: [{ suffix: "ShowHUDText" }, { suffix: "OnlyApplyOnHit" }],
    });
  });

  it("materialising at an afterIndex anchor puts the new directive in the original position", () => {
    const directives: StampedDirective[] = [
      stampDirective(
        { op: "Set", fieldPath: "Cooldown", value: { kind: "Single", single: 1 } },
        counter(),
      ),
      stampDirective(
        {
          op: "Set",
          fieldPath: "ShowHUDText",
          descent: [{ field: "EventHandlers", index: 0 }],
          value: { kind: "Boolean", boolean: true },
        },
        counter(),
      ),
      stampDirective(
        {
          op: "Set",
          fieldPath: "OnlyApplyOnHit",
          descent: [{ field: "EventHandlers", index: 0 }],
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
    const firstPrefixed = buildDescentMemberDirective("EventHandlers", 0, first);
    const final = insertAtPendingAnchor(afterClear, firstPrefixed, {
      kind: "afterIndex",
      flatIndex: 0,
    });

    expect(final.map((d) => d.fieldPath)).toEqual(["Cooldown", "Event", "Tier"]);
    expect(final[1]?.descent).toEqual([{ field: "EventHandlers", index: 0 }]);
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
        descent: [{ field: "EventHandlers", index: 0 }],
        value: { kind: "Boolean", boolean: true },
        _uiId: "g0",
      },
      {
        op: "Set",
        fieldPath: "Y",
        descent: [{ field: "EventHandlers", index: 0 }],
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
        descent: [{ field: "EventHandlers", index: 0 }],
        value: { kind: "Int32", int32: 1 },
        _uiId: "g0",
      },
      {
        op: "Set",
        fieldPath: "B",
        descent: [{ field: "EventHandlers", index: 0 }],
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

// --- reorderByUiId ---

describe("reorderByUiId", () => {
  it("moves an entry down", () => {
    const list = [dir("a", "A"), dir("b", "B"), dir("c", "C")];
    const next = reorderByUiId(list, "a", 3);
    expect(next.map((d) => d._uiId)).toEqual(["b", "c", "a"]);
  });

  it("moves an entry up", () => {
    const list = [dir("a", "A"), dir("b", "B"), dir("c", "C")];
    const next = reorderByUiId(list, "c", 0);
    expect(next.map((d) => d._uiId)).toEqual(["c", "a", "b"]);
  });

  it("returns the same reference when fromId is unknown", () => {
    const list = [dir("a", "A"), dir("b", "B")];
    expect(reorderByUiId(list, "ghost", 0)).toBe(list);
  });

  it("does not mutate the input list", () => {
    const list = [dir("a", "A"), dir("b", "B")];
    reorderByUiId(list, "a", 2);
    expect(list.map((d) => d._uiId)).toEqual(["a", "b"]);
  });
});

// --- undoCoalesceKey ---

describe("undoCoalesceKey", () => {
  it("keys updateNode by node index", () => {
    expect(undoCoalesceKey({ type: "updateNode", nodeIndex: 2, node: node2() })).toBe("node:2");
  });

  it("keys updateDirective by node and directive index", () => {
    expect(
      undoCoalesceKey({
        type: "updateDirective",
        nodeIndex: 1,
        dirIndex: 4,
        directive: dir("a", "A"),
      }),
    ).toBe("dir:1:4");
  });

  it("returns null for structural actions", () => {
    expect(undoCoalesceKey({ type: "addNode", node: node2() })).toBeNull();
    expect(undoCoalesceKey({ type: "deleteNode", nodeIndex: 0 })).toBeNull();
    expect(undoCoalesceKey({ type: "reorderCards", fromId: "a", toSlot: 1 })).toBeNull();
    expect(undoCoalesceKey({ type: "load", nodes: [] })).toBeNull();
  });

  function node2() {
    return { kind: "Patch" as const, templateType: "T", directives: [], _uiId: "n" };
  }
});

// --- pushUndoFrame ---

describe("pushUndoFrame", () => {
  it("pushes when the keys differ", () => {
    const stack = ["f0"];
    const next = pushUndoFrame(stack, "f1", {
      key: "dir:0:0",
      now: 100,
      lastKey: "dir:0:1",
      lastTime: 90,
    });
    expect(next).toEqual(["f0", "f1"]);
  });

  it("pushes when the key is null even with matching timestamps", () => {
    const stack = ["f0"];
    const next = pushUndoFrame(stack, "f1", { key: null, now: 100, lastKey: null, lastTime: 100 });
    expect(next).toEqual(["f0", "f1"]);
  });

  it("coalesces a same-key push inside the window (same reference back)", () => {
    const stack = ["f0"];
    const next = pushUndoFrame(stack, "f1", {
      key: "dir:0:0",
      now: 100 + UNDO_COALESCE_WINDOW_MS,
      lastKey: "dir:0:0",
      lastTime: 100,
    });
    expect(next).toBe(stack);
  });

  it("pushes a same-key frame once the window has elapsed", () => {
    const stack = ["f0"];
    const next = pushUndoFrame(stack, "f1", {
      key: "dir:0:0",
      now: 101 + UNDO_COALESCE_WINDOW_MS,
      lastKey: "dir:0:0",
      lastTime: 100,
    });
    expect(next).toEqual(["f0", "f1"]);
  });

  it("never coalesces onto an empty stack", () => {
    const next = pushUndoFrame<string>([], "f0", {
      key: "dir:0:0",
      now: 100,
      lastKey: "dir:0:0",
      lastTime: 100,
    });
    expect(next).toEqual(["f0"]);
  });

  it("caps the stack at UNDO_STACK_LIMIT, dropping the oldest frames", () => {
    let stack: string[] = [];
    for (let i = 0; i < UNDO_STACK_LIMIT + 5; i++) {
      stack = pushUndoFrame(stack, `f${i}`, {
        key: null,
        now: i,
        lastKey: null,
        lastTime: i - 1,
      });
    }
    expect(stack).toHaveLength(UNDO_STACK_LIMIT);
    expect(stack[0]).toBe("f5");
    expect(stack[stack.length - 1]).toBe(`f${UNDO_STACK_LIMIT + 4}`);
  });
});
