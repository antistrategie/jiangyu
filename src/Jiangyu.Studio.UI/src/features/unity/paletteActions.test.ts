import { describe, expect, it } from "vitest";
import { PALETTE_SCOPE } from "@shared/palette/actions";
import { buildUnityActions, type UnityActionHandlers } from "@features/unity/paletteActions";

function makeHandlers(): UnityActionHandlers & {
  calls: { init: number; open: number };
} {
  const calls = { init: 0, open: 0 };
  return {
    calls,
    initUnity: () => void calls.init++,
    openUnity: () => void calls.open++,
  };
}

describe("buildUnityActions", () => {
  it("returns no actions when no project is open", () => {
    const actions = buildUnityActions(null, makeHandlers());
    expect(actions).toEqual([]);
  });

  it("returns sync + open when a project is open", () => {
    const actions = buildUnityActions("/a/b", makeHandlers());
    expect(actions.map((a) => a.id)).toEqual(["unity.init", "unity.open"]);
  });

  it("wires the run callbacks to the provided handlers", () => {
    const h = makeHandlers();
    const actions = buildUnityActions("/home/a", h);
    const find = (id: string) => actions.find((a) => a.id === id)!;

    void find("unity.init").run();
    void find("unity.open").run();

    expect(h.calls).toEqual({ init: 1, open: 1 });
  });

  it("tags every action with the UNITY scope", () => {
    const actions = buildUnityActions("/home/a", makeHandlers());
    expect(actions.every((a) => a.scope === PALETTE_SCOPE.Unity)).toBe(true);
  });
});
