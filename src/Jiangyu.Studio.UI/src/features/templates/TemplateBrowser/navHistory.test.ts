import { describe, it, expect } from "vitest";
import { instanceKey, navStepBack, navStepForward, pushNavEntry } from "./helpers";

describe("instanceKey", () => {
  it("pairs collection + pathId so collisions on either alone are disambiguated", () => {
    expect(
      instanceKey({
        className: "PerkTemplate",
        name: "perk.x",
        identity: { collection: "globalgamemanagers", pathId: 42 },
      }),
    ).toBe("globalgamemanagers:42");
  });
});

describe("pushNavEntry", () => {
  it("appends a new entry at the end of the trail", () => {
    const next = pushNavEntry(["a", "b"], 1, "c");
    expect(next.history).toEqual(["a", "b", "c"]);
    expect(next.index).toBe(2);
  });

  it("truncates forward branches before pushing", () => {
    // History was [a, b, c, d], navIndex sits at b (index 1). Pushing "e"
    // discards [c, d] — same shape as a browser back button + new nav.
    const next = pushNavEntry(["a", "b", "c", "d"], 1, "e");
    expect(next.history).toEqual(["a", "b", "e"]);
    expect(next.index).toBe(2);
  });

  it("starts a fresh history when pushing onto the empty state", () => {
    const next = pushNavEntry([], -1, "a");
    expect(next.history).toEqual(["a"]);
    expect(next.index).toBe(0);
  });
});

describe("navStepBack", () => {
  it("returns the previous entry and decremented index", () => {
    const step = navStepBack(["a", "b", "c"], 2);
    expect(step).toEqual({ index: 1, key: "b" });
  });

  it("returns null at the start of history", () => {
    expect(navStepBack(["a"], 0)).toBeNull();
  });

  it("returns null on an empty history", () => {
    expect(navStepBack([], 0)).toBeNull();
  });
});

describe("navStepForward", () => {
  it("returns the next entry and incremented index", () => {
    const step = navStepForward(["a", "b", "c"], 0);
    expect(step).toEqual({ index: 1, key: "b" });
  });

  it("returns null at the end of history", () => {
    expect(navStepForward(["a", "b"], 1)).toBeNull();
  });

  it("returns null on an empty history", () => {
    expect(navStepForward([], 0)).toBeNull();
  });
});

describe("nav history round-trips", () => {
  // End-to-end check that push / back / forward compose as expected:
  // navigate forward through a trail, jump back, then push (which should
  // truncate the forward branch we'd just walked away from).
  it("push truncates after a back-step", () => {
    let history: readonly string[] = [];
    let index = -1;
    ({ history, index } = pushNavEntry(history, index, "a"));
    ({ history, index } = pushNavEntry(history, index, "b"));
    ({ history, index } = pushNavEntry(history, index, "c"));
    const back = navStepBack(history, index)!;
    index = back.index;
    expect(history).toEqual(["a", "b", "c"]);
    expect(index).toBe(1);
    ({ history, index } = pushNavEntry(history, index, "d"));
    expect(history).toEqual(["a", "b", "d"]);
    expect(index).toBe(2);
  });
});
