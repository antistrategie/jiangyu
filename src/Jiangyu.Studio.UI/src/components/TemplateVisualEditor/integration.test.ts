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
import type { EditorDirective, EditorDocument } from "./types";

// End-to-end integration tests — exercise the realistic pipeline a
// modder hits when authoring a patch in the visual editor: parse
// (synthesised here from typed directives) → stamp → mutate → strip.
// The components themselves aren't rendered (no DOM env); these tests
// prove the pure helpers compose into the right end state.

function counter(): () => string {
  let n = 0;
  return () => `id${++n}`;
}

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
            // Descent: edit-in-place on slot 0 of EventHandlers.
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
            // Construct-and-replace a different slot. Composite/handler
            // bodies are inner directives without descent prefix.
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

describe("descent group lifecycle", () => {
  // Mirrors the modder flow: start a descent group via the FieldAdder's
  // "Edit slot…" entry (pending state), pick subtype, add first field
  // (materialises into flat directives), then add a sibling field.
  it("pending → materialise → second add stays in the same group", () => {
    const newId = counter();
    let directives: StampedDirective[] = [];

    // Pending state held in UI, no directive yet. Modder picks the
    // first inner field; it materialises at the "end" anchor since
    // pending was started from the top-level FieldAdder.
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

    // Now grouping should put it in a one-member descent group.
    const groups = groupDirectives(directives);
    expect(groups).toHaveLength(1);
    expect(groups[0]).toMatchObject({
      kind: "group",
      field: "EventHandlers",
      index: 0,
      subtype: "AddSkill",
      members: [{ suffix: "ShowHUDText" }],
    });

    // Modder adds a second field via the inner FieldAdder. The
    // DescentGroup inserts at the group's endIndex (= 1 here) so the
    // new directive stays contiguous with the existing one.
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
    // Initial: two-member descent group sandwiched between loose ops.
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

    // Convert to pending: drop the group's directives [1, 3) and
    // anchor pending after the loose Cooldown (flat index 0).
    const groupStart = 1;
    const groupEnd = 3;
    const afterClear = [...directives.slice(0, groupStart), ...directives.slice(groupEnd)];
    expect(afterClear.map((d) => d.fieldPath)).toEqual(["Cooldown", "Tier"]);

    // Modder picks new subtype + first field. Anchor was
    // {kind: "afterIndex", flatIndex: 0} — the directive sitting
    // directly above the cleared group.
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

    // New group lands between Cooldown and Tier — same visual position
    // as the original group. Subtype is the new one.
    expect(final.map((d) => d.fieldPath)).toEqual(["Cooldown", "Event", "Tier"]);
    expect(final[1]?.descent).toEqual([
      { field: "EventHandlers", index: 0, subtype: "ChangeProperty" },
    ]);
  });
});

describe("descent group + reorder interaction", () => {
  it("reordering moves the whole group, leaving siblings undisturbed", () => {
    // [loose, group(g0, g1), loose] — drag group head to the end.
    // Expected: [loose, loose, g0, g1].
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
    // Group is still detectable as a contiguous run after the reorder.
    const groups = groupDirectives(next);
    const groupCount = groups.filter((g) => g.kind === "group").length;
    expect(groupCount).toBe(1);
  });
});

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
