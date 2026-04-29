import { describe, it, expect } from "vitest";
import { resolveEnumLeafLabel } from "./TemplateBrowser";

describe("resolveEnumLeafLabel", () => {
  // Headline case: ItemSlot field surfaces as numeric index 9; the browser
  // should render the readable member name once the enum members are loaded.
  it("returns the enum member name for a defined numeric value", () => {
    const map = { 0: "Helmet", 8: "ModularVehicleLight", 9: "VehicleLightTurret" };
    expect(resolveEnumLeafLabel(9, map)).toBe("VehicleLightTurret");
  });

  // Members haven't loaded yet (or the field isn't an enum): caller falls
  // back to the raw value.
  it("returns null when the label map is null", () => {
    expect(resolveEnumLeafLabel(9, null)).toBeNull();
  });

  // Numeric form that isn't a defined member (e.g. modder authored an
  // out-of-set integer): keep the raw value visible rather than disappearing.
  it("returns null when the value is not a defined member", () => {
    expect(resolveEnumLeafLabel(99, { 0: "A", 1: "B" })).toBeNull();
  });

  // Unexpected value shapes (string, NaN, undefined): no enum lookup.
  it("returns null for non-finite or non-numeric values", () => {
    expect(resolveEnumLeafLabel("9", { 9: "X" })).toBeNull();
    expect(resolveEnumLeafLabel(Number.NaN, { 9: "X" })).toBeNull();
    expect(resolveEnumLeafLabel(undefined, { 9: "X" })).toBeNull();
  });
});
