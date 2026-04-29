import { describe, it, expect } from "vitest";
import { resolveRefTypeDisplay, shouldShowRefTypeSelector } from "./TemplateVisualEditor";

describe("shouldShowRefTypeSelector", () => {
  // Monomorphic concrete destination (e.g. PerkTemplate field): catalog
  // supplies a single concrete type, so the modder shouldn't have to pick.
  it("hides the selector when the declared type is concrete and no explicit override", () => {
    expect(shouldShowRefTypeSelector("PerkTemplate", false, "")).toBe(false);
  });

  // The headline case: RewardTableTemplate.Items → BaseItemTemplate (abstract).
  // The modder must pick ModularVehicleWeaponTemplate / ArmorTemplate / ...,
  // so the selector has to be visible even though the catalog supplied a name.
  it("shows the selector when the declared type is polymorphic", () => {
    expect(shouldShowRefTypeSelector("BaseItemTemplate", true, "")).toBe(true);
  });

  // Catalog couldn't resolve a declared type (rare; e.g. cross-assembly
  // reference where the leaf isn't loadable). Always allow the modder to
  // pick a type so the patch can still be authored.
  it("shows the selector when the catalog supplied no declared type", () => {
    expect(shouldShowRefTypeSelector("", false, "")).toBe(true);
  });

  // Existing explicit ref="..." authored value: keep showing the selector
  // so the modder can change or clear it without re-loading the row.
  it("shows the selector when an explicit ref type is present on the value", () => {
    expect(shouldShowRefTypeSelector("PerkTemplate", false, "PerkTemplate")).toBe(true);
    expect(
      shouldShowRefTypeSelector("BaseItemTemplate", true, "ModularVehicleWeaponTemplate"),
    ).toBe(true);
  });
});

describe("resolveRefTypeDisplay", () => {
  // Polymorphic: empty explicit value must stay empty in the combobox.
  // Falling back to the declared (abstract) type made it look like clearing
  // the field re-filled it with BaseItemTemplate.
  it("does not fall back to the declared type for polymorphic destinations", () => {
    expect(resolveRefTypeDisplay("BaseItemTemplate", true, "")).toBe("");
  });

  it("shows the explicit concrete type once the modder picks one", () => {
    expect(resolveRefTypeDisplay("BaseItemTemplate", true, "ModularVehicleWeaponTemplate")).toBe(
      "ModularVehicleWeaponTemplate",
    );
  });

  // Monomorphic: declared type is a useful idle display value.
  it("falls back to the declared type for monomorphic destinations", () => {
    expect(resolveRefTypeDisplay("PerkTemplate", false, "")).toBe("PerkTemplate");
  });

  it("prefers the explicit type over the declared type when present", () => {
    expect(resolveRefTypeDisplay("PerkTemplate", false, "OtherPerkTemplate")).toBe(
      "OtherPerkTemplate",
    );
  });
});
