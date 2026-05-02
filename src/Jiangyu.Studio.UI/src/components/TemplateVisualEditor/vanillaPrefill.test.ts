import { describe, it, expect } from "vitest";
import type { InspectedFieldNode, TemplateMember } from "@lib/rpc";
import { inspectedFieldToEditorValue } from "./helpers";

function member(overrides: Partial<TemplateMember> = {}): TemplateMember {
  return {
    name: "Field",
    typeName: "",
    isWritable: true,
    isInherited: false,
    ...overrides,
  };
}

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
