import { describe, it, expect } from "vitest";
import type { TemplateMember } from "@lib/rpc";
import { isFieldBagValue, makeDefaultValue } from "./helpers";

function member(overrides: Partial<TemplateMember> = {}): TemplateMember {
  return {
    name: "EventHandlers",
    typeName: "List<BaseEventHandlerTemplate>",
    isWritable: true,
    isInherited: false,
    isCollection: true,
    elementTypeName: "BaseEventHandlerTemplate",
    ...overrides,
  };
}

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

describe("makeDefaultValue (handler construction)", () => {
  it("emits HandlerConstruction with empty compositeType when multiple subtypes are available", () => {
    const m = member({
      elementSubtypes: ["OnAddedEventHandlerTemplate", "OnAttackEventHandlerTemplate"],
    });
    expect(makeDefaultValue(m)).toEqual({
      kind: "HandlerConstruction",
      compositeType: "",
      compositeDirectives: [],
    });
  });

  it("pre-fills compositeType when exactly one subtype is available", () => {
    const m = member({ elementSubtypes: ["OnAttackEventHandlerTemplate"] });
    expect(makeDefaultValue(m)).toEqual({
      kind: "HandlerConstruction",
      compositeType: "OnAttackEventHandlerTemplate",
      compositeDirectives: [],
    });
  });

  it("falls back to a Composite when the member has no subtype hints", () => {
    const m = member({ elementTypeName: "Loadout" });
    expect(makeDefaultValue(m)).toEqual({
      kind: "Composite",
      compositeType: "Loadout",
      compositeDirectives: [],
    });
  });

  it("falls back to a Composite when subtypes list is empty", () => {
    const m = member({ elementTypeName: "Loadout", elementSubtypes: [] });
    expect(makeDefaultValue(m)).toEqual({
      kind: "Composite",
      compositeType: "Loadout",
      compositeDirectives: [],
    });
  });
});
