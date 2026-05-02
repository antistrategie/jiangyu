import { describe, it, expect } from "vitest";
import { stampDirective, stampNodes, stripDirectiveUiIds, stripUiIds } from "./helpers";
import type { EditorDirective, EditorDocument, EditorNode } from "./types";

// Deterministic id generator so tests can assert on stamped output
// without relying on the editor's module-level counter.
function counter(): () => string {
  let n = 0;
  return () => `id${++n}`;
}

describe("stampDirective", () => {
  it("stamps a flat directive with a fresh id", () => {
    const stamped = stampDirective(
      { op: "Set", fieldPath: "X", value: { kind: "Int32", int32: 1 } },
      counter(),
    );
    expect(stamped._uiId).toBe("id1");
    expect(stamped.fieldPath).toBe("X");
  });

  it("preserves an already-stamped id", () => {
    const stamped = stampDirective(
      {
        op: "Set",
        fieldPath: "X",
        value: { kind: "Int32", int32: 1 },
        _uiId: "preserve-me",
      },
      counter(),
    );
    expect(stamped._uiId).toBe("preserve-me");
  });

  it("recurses into composite values and stamps each inner directive", () => {
    const newId = counter();
    const stamped = stampDirective(
      {
        op: "Append",
        fieldPath: "Perks",
        value: {
          kind: "Composite",
          compositeType: "Perk",
          compositeDirectives: [
            { op: "Set", fieldPath: "Tier", value: { kind: "Int32", int32: 3 } },
            { op: "Append", fieldPath: "Tags" },
          ],
        },
      },
      newId,
    );
    expect(stamped._uiId).toBe("id1");
    const inner = stamped.value!.compositeDirectives;
    expect(inner).toBeDefined();
    expect(inner![0]?._uiId).toBe("id2");
    expect(inner![1]?._uiId).toBe("id3");
  });

  it("recurses into HandlerConstruction values too", () => {
    const stamped = stampDirective(
      {
        op: "Append",
        fieldPath: "EventHandlers",
        value: {
          kind: "HandlerConstruction",
          compositeType: "AddSkill",
          compositeDirectives: [
            { op: "Set", fieldPath: "ShowHUDText", value: { kind: "Boolean", boolean: true } },
          ],
        },
      },
      counter(),
    );
    const inner = stamped.value!.compositeDirectives;
    expect(inner![0]?._uiId).toBe("id2");
  });

  it("nests recursively — composite inside handler inside outer", () => {
    const newId = counter();
    const stamped = stampDirective(
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
      newId,
    );
    const outer = stamped._uiId;
    const handler1 = stamped.value!.compositeDirectives![0]!;
    const composite1 = handler1.value!.compositeDirectives![0]!;
    expect([outer, handler1._uiId, composite1._uiId]).toEqual(["id1", "id2", "id3"]);
  });

  it("does not stamp scalar values", () => {
    const stamped = stampDirective(
      { op: "Set", fieldPath: "X", value: { kind: "String", string: "hi" } },
      counter(),
    );
    expect(stamped.value).toEqual({ kind: "String", string: "hi" });
  });
});

describe("stampNodes", () => {
  it("stamps each node and each of its directives", () => {
    const newId = counter();
    const nodes: EditorNode[] = [
      {
        kind: "Patch",
        templateType: "PerkTemplate",
        templateId: "perk.x",
        directives: [{ op: "Set", fieldPath: "Tier", value: { kind: "Int32", int32: 3 } }],
      },
      {
        kind: "Patch",
        templateType: "PerkTemplate",
        templateId: "perk.y",
        directives: [],
      },
    ];
    const stamped = stampNodes(nodes, newId);
    // Node ids first, then each node's directive ids in order.
    expect(stamped[0]?._uiId).toBe("id1");
    expect(stamped[0]?.directives[0]?._uiId).toBe("id2");
    expect(stamped[1]?._uiId).toBe("id3");
  });
});

describe("strip*UiIds", () => {
  it("removes _uiId from a flat directive", () => {
    const stripped = stripDirectiveUiIds({
      op: "Set",
      fieldPath: "X",
      value: { kind: "Int32", int32: 1 },
      _uiId: "id1",
    });
    expect(stripped).toEqual({
      op: "Set",
      fieldPath: "X",
      value: { kind: "Int32", int32: 1 },
    });
  });

  it("recurses into composite directives", () => {
    const stripped = stripDirectiveUiIds({
      op: "Append",
      fieldPath: "Perks",
      _uiId: "outer",
      value: {
        kind: "Composite",
        compositeType: "Perk",
        compositeDirectives: [
          { op: "Set", fieldPath: "Tier", value: { kind: "Int32", int32: 3 }, _uiId: "inner" },
        ],
      },
    });
    expect(stripped._uiId).toBeUndefined();
    expect(stripped.value!.compositeDirectives![0]!._uiId).toBeUndefined();
  });

  it("round-trips: stamp → strip is identity on the wire shape", () => {
    const original: EditorDirective = {
      op: "Set",
      fieldPath: "ShowHUDText",
      descent: [{ field: "EventHandlers", index: 0, subtype: "AddSkill" }],
      value: { kind: "Boolean", boolean: true },
    };
    const stamped = stampDirective(original, counter());
    const stripped = stripDirectiveUiIds(stamped);
    expect(stripped).toEqual(original);
  });

  it("stripUiIds clears node ids and directive ids together", () => {
    const doc: EditorDocument = {
      nodes: [
        {
          kind: "Patch",
          templateType: "PerkTemplate",
          templateId: "perk.x",
          directives: [{ op: "Set", fieldPath: "Tier", value: { kind: "Int32", int32: 3 } }],
        },
      ],
      errors: [],
    };
    const stamped = { ...doc, nodes: stampNodes(doc.nodes, counter()) };
    const stripped = stripUiIds(stamped);
    expect(stripped.nodes[0]?._uiId).toBeUndefined();
    expect(stripped.nodes[0]?.directives[0]?._uiId).toBeUndefined();
  });
});
