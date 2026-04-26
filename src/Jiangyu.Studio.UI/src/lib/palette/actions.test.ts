import { beforeEach, describe, expect, it } from "vitest";
import { PALETTE_SCOPE, usePaletteStore, type PaletteAction } from "./actions";

function makeAction(id: string): PaletteAction {
  return { id, label: id, scope: PALETTE_SCOPE.App, run: () => {} };
}

describe("paletteStore", () => {
  beforeEach(() => {
    usePaletteStore.setState({ slots: {} });
  });

  it("register stores actions under the slot id", () => {
    const actions = [makeAction("a")];
    usePaletteStore.getState().register("slot1", actions);
    expect(usePaletteStore.getState().slots.slot1).toBe(actions);
  });

  it("register is a no-op when the same array reference is re-registered", () => {
    const actions = [makeAction("a")];
    usePaletteStore.getState().register("slot1", actions);
    const before = usePaletteStore.getState();
    usePaletteStore.getState().register("slot1", actions);
    expect(usePaletteStore.getState()).toBe(before);
  });

  it("register replaces the slot when a new array is passed", () => {
    usePaletteStore.getState().register("slot1", [makeAction("a")]);
    const next = [makeAction("b")];
    usePaletteStore.getState().register("slot1", next);
    expect(usePaletteStore.getState().slots.slot1).toBe(next);
  });

  it("register keeps unrelated slots untouched", () => {
    const a = [makeAction("a")];
    const b = [makeAction("b")];
    usePaletteStore.getState().register("slotA", a);
    usePaletteStore.getState().register("slotB", b);
    expect(usePaletteStore.getState().slots.slotA).toBe(a);
    expect(usePaletteStore.getState().slots.slotB).toBe(b);
  });

  it("unregister drops the slot", () => {
    usePaletteStore.getState().register("slot1", [makeAction("a")]);
    usePaletteStore.getState().unregister("slot1");
    expect(usePaletteStore.getState().slots.slot1).toBeUndefined();
  });

  it("unregister returns the same state reference for an unknown slot", () => {
    const before = usePaletteStore.getState();
    usePaletteStore.getState().unregister("nope");
    expect(usePaletteStore.getState()).toBe(before);
  });
});
