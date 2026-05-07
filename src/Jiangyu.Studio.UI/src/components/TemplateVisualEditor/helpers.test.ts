import { describe, it, expect } from "vitest";
import type { InspectedFieldNode, TemplateMember } from "@lib/rpc";
import {
  allowsMultipleDirectives,
  groupDirectives,
  inspectedFieldToEditorValue,
  isCellAddressedSet,
  isFieldBagValue,
  makeDefaultValue,
  resolveEnumCommitType,
  resolveRefTypeDisplay,
  shouldShowRefTypeSelector,
  stampDirective,
  stampNodes,
  stripDirectiveUiIds,
  stripUiIds,
} from "./helpers";
import type { DescentStep, EditorDirective, EditorDocument, EditorNode } from "./types";

// --- Factory helpers ---

function member(overrides: Partial<TemplateMember> = {}): TemplateMember {
  return {
    name: "Field",
    typeName: "",
    isWritable: true,
    isInherited: false,
    ...overrides,
  };
}

function counter(): () => string {
  let n = 0;
  return () => `id${++n}`;
}

// --- resolveEnumCommitType ---

describe("resolveEnumCommitType", () => {
  it("uses the declared enum type when present", () => {
    expect(resolveEnumCommitType("ItemSlot", "AddItemSlot")).toBe("ItemSlot");
    expect(resolveEnumCommitType("ItemSlot", undefined)).toBe("ItemSlot");
    expect(resolveEnumCommitType("ItemSlot", "")).toBe("ItemSlot");
  });

  it("falls back to the existing value when no declared type", () => {
    expect(resolveEnumCommitType(undefined, "ItemSlot")).toBe("ItemSlot");
    expect(resolveEnumCommitType("", "ItemSlot")).toBe("ItemSlot");
    expect(resolveEnumCommitType(undefined, undefined)).toBeUndefined();
    expect(resolveEnumCommitType("", undefined)).toBeUndefined();
  });
});

// --- shouldShowRefTypeSelector ---

describe("shouldShowRefTypeSelector", () => {
  it("hides the selector when the declared type is concrete and no explicit override", () => {
    expect(shouldShowRefTypeSelector("PerkTemplate", false, "")).toBe(false);
  });

  it("shows the selector when the declared type is polymorphic", () => {
    expect(shouldShowRefTypeSelector("BaseItemTemplate", true, "")).toBe(true);
  });

  it("shows the selector when the catalog supplied no declared type", () => {
    expect(shouldShowRefTypeSelector("", false, "")).toBe(true);
  });

  it("shows the selector for polymorphic destinations with an explicit type", () => {
    expect(
      shouldShowRefTypeSelector("BaseItemTemplate", true, "ModularVehicleWeaponTemplate"),
    ).toBe(true);
  });

  it("hides the selector when an explicit ref type matches the declared monomorphic type", () => {
    expect(shouldShowRefTypeSelector("SkillTemplate", false, "SkillTemplate")).toBe(false);
  });

  it("shows the selector when an explicit ref type contradicts the declared monomorphic type", () => {
    expect(shouldShowRefTypeSelector("SkillTemplate", false, "WeaponTemplate")).toBe(true);
  });
});

// --- resolveRefTypeDisplay ---

describe("resolveRefTypeDisplay", () => {
  it("does not fall back to the declared type for polymorphic destinations", () => {
    expect(resolveRefTypeDisplay("BaseItemTemplate", true, "")).toBe("");
  });

  it("shows the explicit concrete type once the modder picks one", () => {
    expect(resolveRefTypeDisplay("BaseItemTemplate", true, "ModularVehicleWeaponTemplate")).toBe(
      "ModularVehicleWeaponTemplate",
    );
  });

  it("falls back to the declared type for monomorphic destinations", () => {
    expect(resolveRefTypeDisplay("PerkTemplate", false, "")).toBe("PerkTemplate");
  });

  it("prefers the explicit type over the declared type when present", () => {
    expect(resolveRefTypeDisplay("PerkTemplate", false, "OtherPerkTemplate")).toBe(
      "OtherPerkTemplate",
    );
  });
});

// --- allowsMultipleDirectives ---

describe("allowsMultipleDirectives", () => {
  it("is true for collections", () => {
    expect(allowsMultipleDirectives({ isCollection: true })).toBe(true);
  });

  it("is true for Odin multi-dim arrays (one Set per cell)", () => {
    expect(allowsMultipleDirectives({ isOdinMultiDimArray: true })).toBe(true);
  });

  it("is false for non-collection scalars", () => {
    expect(allowsMultipleDirectives({})).toBe(false);
  });

  it("is false when isCollection is null", () => {
    expect(allowsMultipleDirectives({ isCollection: null })).toBe(false);
  });
});

// --- isCellAddressedSet ---

describe("isCellAddressedSet", () => {
  it("matches Set ops with a 2D indexPath", () => {
    expect(isCellAddressedSet({ op: "Set", indexPath: [0, 0] })).toBe(true);
    expect(isCellAddressedSet({ op: "Set", indexPath: [4, 4] })).toBe(true);
  });

  it("matches Set ops with a 3D indexPath", () => {
    expect(isCellAddressedSet({ op: "Set", indexPath: [1, 2, 3] })).toBe(true);
  });

  it("rejects Set ops without an indexPath", () => {
    expect(isCellAddressedSet({ op: "Set" })).toBe(false);
    expect(isCellAddressedSet({ op: "Set", indexPath: null })).toBe(false);
  });

  it("rejects Set ops with a 1D indexPath (those are list indices, not cells)", () => {
    expect(isCellAddressedSet({ op: "Set", indexPath: [3] })).toBe(false);
  });

  it("rejects non-Set ops even when indexPath is present", () => {
    expect(isCellAddressedSet({ op: "Append", indexPath: [0, 0] })).toBe(false);
    expect(isCellAddressedSet({ op: "Remove", indexPath: [0, 0] })).toBe(false);
  });
});

// --- inspectedFieldToEditorValue ---

describe("inspectedFieldToEditorValue", () => {
  it("maps int kind to Int32 by default", () => {
    const node: InspectedFieldNode = { kind: "int", value: 7 };
    expect(inspectedFieldToEditorValue(node, member({ patchScalarKind: "Int32" }))).toEqual({
      kind: "Int32",
      int32: 7,
    });
  });

  it("maps int kind to Byte when the member declares Byte", () => {
    const node: InspectedFieldNode = { kind: "int", value: 5 };
    expect(inspectedFieldToEditorValue(node, member({ patchScalarKind: "Byte" }))).toEqual({
      kind: "Byte",
      int32: 5,
    });
  });

  it("truncates float values destined for an int slot", () => {
    const node: InspectedFieldNode = { kind: "int", value: 3.7 };
    expect(inspectedFieldToEditorValue(node, member({ patchScalarKind: "Int32" }))).toEqual({
      kind: "Int32",
      int32: 3,
    });
  });

  it("maps float kind to Single", () => {
    const node: InspectedFieldNode = { kind: "float", value: 2.5 };
    expect(inspectedFieldToEditorValue(node, member({ patchScalarKind: "Single" }))).toEqual({
      kind: "Single",
      single: 2.5,
    });
  });

  it("maps string kind", () => {
    const node: InspectedFieldNode = { kind: "string", value: "hello" };
    expect(inspectedFieldToEditorValue(node, member({ patchScalarKind: "String" }))).toEqual({
      kind: "String",
      string: "hello",
    });
  });

  it("maps bool kind", () => {
    const node: InspectedFieldNode = { kind: "bool", value: true };
    expect(inspectedFieldToEditorValue(node, member({ patchScalarKind: "Boolean" }))).toEqual({
      kind: "Boolean",
      boolean: true,
    });
  });

  it("maps enum kind using the member's declared element type", () => {
    const node: InspectedFieldNode = { kind: "enum", value: "Heavy" };
    const m = member({ patchScalarKind: "Enum", elementTypeName: "ItemSlot" });
    expect(inspectedFieldToEditorValue(node, m)).toEqual({
      kind: "Enum",
      enumType: "ItemSlot",
      enumValue: "Heavy",
    });
  });

  it("falls back to typeName when elementTypeName is absent for enums", () => {
    const node: InspectedFieldNode = { kind: "enum", value: "Light" };
    const m = member({ patchScalarKind: "Enum", typeName: "ItemSlot" });
    expect(inspectedFieldToEditorValue(node, m)).toEqual({
      kind: "Enum",
      enumType: "ItemSlot",
      enumValue: "Light",
    });
  });

  it("emits a TemplateReference without referenceType for monomorphic refs", () => {
    const node: InspectedFieldNode = {
      kind: "reference",
      reference: { name: "weapon.foo", className: "WeaponTemplate" },
    };
    const m = member({ patchScalarKind: "TemplateReference" });
    expect(inspectedFieldToEditorValue(node, m)).toEqual({
      kind: "TemplateReference",
      referenceId: "weapon.foo",
    });
  });

  it("attaches referenceType for polymorphic ref destinations", () => {
    const node: InspectedFieldNode = {
      kind: "reference",
      reference: { name: "weapon.foo", className: "ModularVehicleWeaponTemplate" },
    };
    const m = member({
      patchScalarKind: "TemplateReference",
      isReferenceTypePolymorphic: true,
    });
    expect(inspectedFieldToEditorValue(node, m)).toEqual({
      kind: "TemplateReference",
      referenceId: "weapon.foo",
      referenceType: "ModularVehicleWeaponTemplate",
    });
  });

  it("returns undefined for null nodes so the caller keeps the neutral default", () => {
    const node: InspectedFieldNode = { kind: "string", null: true };
    expect(
      inspectedFieldToEditorValue(node, member({ patchScalarKind: "String" })),
    ).toBeUndefined();
  });

  it("returns undefined when the member's scalar kind doesn't match the inspected value", () => {
    const node: InspectedFieldNode = { kind: "string", value: "not-a-number" };
    expect(inspectedFieldToEditorValue(node, member({ patchScalarKind: "Int32" }))).toBeUndefined();
  });

  it("converts an object node into a Composite with sub-fields converted by kind", () => {
    const node: InspectedFieldNode = {
      kind: "object",
      fields: [
        { name: "m_DefaultTranslation", kind: "string", value: "Long Range Missile" },
        { name: "m_DontReuseTranslation", kind: "bool", value: false },
      ],
    };
    const m = member({ typeName: "LocalizedLine" });
    expect(inspectedFieldToEditorValue(node, m)).toEqual({
      kind: "Composite",
      compositeType: "LocalizedLine",
      compositeDirectives: [
        {
          op: "Set",
          fieldPath: "m_DefaultTranslation",
          value: { kind: "String", string: "Long Range Missile" },
        },
        {
          op: "Set",
          fieldPath: "m_DontReuseTranslation",
          value: { kind: "Boolean", boolean: false },
        },
      ],
    });
  });

  it("returns undefined when no fallback type is available for a composite", () => {
    const node: InspectedFieldNode = { kind: "object", fields: [] };
    expect(inspectedFieldToEditorValue(node, member())).toBeUndefined();
  });

  it("returns undefined when given an undefined node", () => {
    expect(
      inspectedFieldToEditorValue(undefined, member({ patchScalarKind: "Int32" })),
    ).toBeUndefined();
  });

  it("preserves enum sub-fields inside a composite when the inspected node carries fieldTypeName", () => {
    const node: InspectedFieldNode = {
      kind: "object",
      fields: [
        { name: "m_Slot", kind: "enum", value: "Heavy", fieldTypeName: "ItemSlot" },
        { name: "m_Count", kind: "int", value: 3 },
      ],
    };
    const m = member({ typeName: "Loadout" });
    expect(inspectedFieldToEditorValue(node, m)).toEqual({
      kind: "Composite",
      compositeType: "Loadout",
      compositeDirectives: [
        {
          op: "Set",
          fieldPath: "m_Slot",
          value: { kind: "Enum", enumType: "ItemSlot", enumValue: "Heavy" },
        },
        {
          op: "Set",
          fieldPath: "m_Count",
          value: { kind: "Int32", int32: 3 },
        },
      ],
    });
  });

  it("drops enum sub-fields inside a composite when fieldTypeName is missing", () => {
    const node: InspectedFieldNode = {
      kind: "object",
      fields: [{ name: "m_Slot", kind: "enum", value: "Heavy" }],
    };
    const m = member({ typeName: "Loadout" });
    expect(inspectedFieldToEditorValue(node, m)).toEqual({
      kind: "Composite",
      compositeType: "Loadout",
      compositeDirectives: [],
    });
  });
});

// --- isFieldBagValue ---

describe("isFieldBagValue", () => {
  it("treats Composite values as field bags", () => {
    expect(
      isFieldBagValue({ kind: "Composite", compositeType: "Loadout", compositeDirectives: [] }),
    ).toBe(true);
  });

  it("treats HandlerConstruction values as field bags", () => {
    expect(
      isFieldBagValue({
        kind: "HandlerConstruction",
        compositeType: "OnAttackEventHandlerTemplate",
        compositeDirectives: [],
      }),
    ).toBe(true);
  });

  it("returns false for scalar / reference / enum values", () => {
    expect(isFieldBagValue({ kind: "String", string: "hi" })).toBe(false);
    expect(isFieldBagValue({ kind: "TemplateReference", referenceId: "weapon.foo" })).toBe(false);
    expect(isFieldBagValue({ kind: "Enum", enumType: "ItemSlot", enumValue: "Heavy" })).toBe(false);
    expect(isFieldBagValue(undefined)).toBe(false);
  });
});

// --- makeDefaultValue (handler construction) ---

describe("makeDefaultValue (handler construction)", () => {
  it("emits HandlerConstruction with empty compositeType when multiple subtypes are available", () => {
    const m = member({
      name: "EventHandlers",
      typeName: "List<BaseEventHandlerTemplate>",
      isCollection: true,
      elementTypeName: "BaseEventHandlerTemplate",
      elementSubtypes: ["OnAddedEventHandlerTemplate", "OnAttackEventHandlerTemplate"],
    });
    expect(makeDefaultValue(m)).toEqual({
      kind: "HandlerConstruction",
      compositeType: "",
      compositeDirectives: [],
    });
  });

  it("pre-fills compositeType when exactly one subtype is available", () => {
    const m = member({
      name: "EventHandlers",
      typeName: "List<BaseEventHandlerTemplate>",
      isCollection: true,
      elementTypeName: "BaseEventHandlerTemplate",
      elementSubtypes: ["OnAttackEventHandlerTemplate"],
    });
    expect(makeDefaultValue(m)).toEqual({
      kind: "HandlerConstruction",
      compositeType: "OnAttackEventHandlerTemplate",
      compositeDirectives: [],
    });
  });

  it("falls back to a Composite when the member has no subtype hints", () => {
    const m = member({
      name: "EventHandlers",
      typeName: "List<BaseEventHandlerTemplate>",
      isCollection: true,
      elementTypeName: "Loadout",
    });
    expect(makeDefaultValue(m)).toEqual({
      kind: "Composite",
      compositeType: "Loadout",
      compositeDirectives: [],
    });
  });

  it("falls back to a Composite when subtypes list is empty", () => {
    const m = member({
      name: "EventHandlers",
      typeName: "List<BaseEventHandlerTemplate>",
      isCollection: true,
      elementTypeName: "Loadout",
      elementSubtypes: [],
    });
    expect(makeDefaultValue(m)).toEqual({
      kind: "Composite",
      compositeType: "Loadout",
      compositeDirectives: [],
    });
  });
});

// --- stampDirective ---

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

// --- stampNodes ---

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
    expect(stamped[0]?._uiId).toBe("id1");
    expect(stamped[0]?.directives[0]?._uiId).toBe("id2");
    expect(stamped[1]?._uiId).toBe("id3");
  });
});

// --- strip*UiIds ---

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

// --- groupDirectives ---

describe("groupDirectives", () => {
  interface D {
    op: string;
    fieldPath: string;
    descent?: DescentStep[];
  }

  it("groups consecutive descent directives sharing field+index+subtype", () => {
    const directives: D[] = [
      {
        op: "Set",
        fieldPath: "ShowHUDText",
        descent: [{ field: "EventHandlers", index: 0, subtype: "AddSkill" }],
      },
      {
        op: "Set",
        fieldPath: "OnlyApplyOnHit",
        descent: [{ field: "EventHandlers", index: 0, subtype: "AddSkill" }],
      },
    ];
    const groups = groupDirectives(directives);
    expect(groups).toHaveLength(1);
    expect(groups[0]).toMatchObject({
      kind: "group",
      field: "EventHandlers",
      index: 0,
      subtype: "AddSkill",
      members: [{ suffix: "ShowHUDText" }, { suffix: "OnlyApplyOnHit" }],
    });
  });

  it("splits when subtype hints differ at segment 0", () => {
    const directives: D[] = [
      {
        op: "Set",
        fieldPath: "A",
        descent: [{ field: "EventHandlers", index: 0, subtype: "AddSkill" }],
      },
      {
        op: "Set",
        fieldPath: "B",
        descent: [{ field: "EventHandlers", index: 0, subtype: "ChangePropertyConditional" }],
      },
    ];
    const groups = groupDirectives(directives);
    expect(groups).toHaveLength(2);
  });

  it("interleaves loose directives between groups", () => {
    const directives: D[] = [
      { op: "Set", fieldPath: "Cooldown" },
      {
        op: "Set",
        fieldPath: "ShowHUDText",
        descent: [{ field: "EventHandlers", index: 0, subtype: "AddSkill" }],
      },
      { op: "Append", fieldPath: "Tags" },
    ];
    const groups = groupDirectives(directives);
    expect(groups).toHaveLength(3);
    expect(groups[0]?.kind).toBe("loose");
    expect(groups[1]?.kind).toBe("group");
    expect(groups[2]?.kind).toBe("loose");
  });

  it("does not merge non-consecutive descent directives", () => {
    const directives: D[] = [
      {
        op: "Set",
        fieldPath: "A",
        descent: [{ field: "EventHandlers", index: 0, subtype: "X" }],
      },
      { op: "Set", fieldPath: "Cooldown" },
      {
        op: "Set",
        fieldPath: "B",
        descent: [{ field: "EventHandlers", index: 0, subtype: "X" }],
      },
    ];
    const groups = groupDirectives(directives);
    expect(groups).toHaveLength(3);
    expect(groups[0]?.kind).toBe("group");
    expect(groups[2]?.kind).toBe("group");
  });

  it("treats missing subtype hint as null and groups by null subtype", () => {
    const directives: D[] = [
      {
        op: "Set",
        fieldPath: "Name",
        descent: [{ field: "Items", index: 0 }],
      },
      {
        op: "Set",
        fieldPath: "Cost",
        descent: [{ field: "Items", index: 0 }],
      },
    ];
    const groups = groupDirectives(directives);
    expect(groups).toHaveLength(1);
    expect(groups[0]).toMatchObject({ kind: "group", subtype: null });
  });
});
