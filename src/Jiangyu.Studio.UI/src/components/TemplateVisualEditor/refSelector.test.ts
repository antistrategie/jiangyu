import { describe, it, expect } from "vitest";
import { resolveRefTypeDisplay, shouldShowRefTypeSelector } from "./helpers";

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

  // Polymorphic destination with explicit type: keep showing — the explicit
  // value IS the disambiguation and changing it is a meaningful operation.
  it("shows the selector for polymorphic destinations with an explicit type", () => {
    expect(
      shouldShowRefTypeSelector("BaseItemTemplate", true, "ModularVehicleWeaponTemplate"),
    ).toBe(true);
  });

  // Monomorphic destination where the explicit type is redundant (matches the
  // declared concrete type): hide. The modder has no other valid choice; the
  // dropdown is misleading noise.
  it("hides the selector when an explicit ref type matches the declared monomorphic type", () => {
    expect(shouldShowRefTypeSelector("SkillTemplate", false, "SkillTemplate")).toBe(false);
  });

  // Monomorphic destination but the explicit type doesn't match: show so the
  // modder sees the inconsistency and can fix it.
  it("shows the selector when an explicit ref type contradicts the declared monomorphic type", () => {
    expect(shouldShowRefTypeSelector("SkillTemplate", false, "WeaponTemplate")).toBe(true);
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
