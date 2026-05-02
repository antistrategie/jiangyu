import { describe, it, expect } from "vitest";
import { resolveEnumCommitType } from "./helpers";

describe("resolveEnumCommitType", () => {
  // Headline case: catalog declared 'ItemSlot' for the destination field.
  // No matter what the in-memory value carries, the committed enumType
  // snaps to the declared one. Stops the AddItemSlot/ItemSlot mismatch.
  it("uses the declared enum type when present", () => {
    expect(resolveEnumCommitType("ItemSlot", "AddItemSlot")).toBe("ItemSlot");
    expect(resolveEnumCommitType("ItemSlot", undefined)).toBe("ItemSlot");
    expect(resolveEnumCommitType("ItemSlot", "")).toBe("ItemSlot");
  });

  // Catalog couldn't supply a type (field's enum type isn't loadable, or
  // composite-context defaulting): preserve whatever the value carries so
  // round-tripping doesn't silently drop the type.
  it("falls back to the existing value when no declared type", () => {
    expect(resolveEnumCommitType(undefined, "ItemSlot")).toBe("ItemSlot");
    expect(resolveEnumCommitType("", "ItemSlot")).toBe("ItemSlot");
    expect(resolveEnumCommitType(undefined, undefined)).toBeUndefined();
    expect(resolveEnumCommitType("", undefined)).toBeUndefined();
  });
});
