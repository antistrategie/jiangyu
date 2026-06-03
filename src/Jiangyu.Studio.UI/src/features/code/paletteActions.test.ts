import { describe, expect, it } from "vitest";
import { PALETTE_SCOPE } from "@shared/palette/actions";
import { buildCodeActions, type CodeActionHandlers } from "@features/code/paletteActions";

function makeHandlers(): CodeActionHandlers & { calls: { sync: number } } {
  const calls = { sync: 0 };
  return {
    calls,
    syncCode: () => void calls.sync++,
  };
}

describe("buildCodeActions", () => {
  it("returns no actions when no project is open", () => {
    const actions = buildCodeActions(null, makeHandlers());
    expect(actions).toEqual([]);
  });

  it("returns the sync action when a project is open", () => {
    const actions = buildCodeActions("/a/b", makeHandlers());
    expect(actions.map((a) => a.id)).toEqual(["code.sync"]);
  });

  it("wires the run callback to the provided handler", () => {
    const h = makeHandlers();
    const actions = buildCodeActions("/home/a", h);
    void actions.find((a) => a.id === "code.sync")!.run();
    expect(h.calls).toEqual({ sync: 1 });
  });

  it("scopes the sync action under CODE", () => {
    const actions = buildCodeActions("/home/a", makeHandlers());
    expect(actions.every((a) => a.scope === PALETTE_SCOPE.Code)).toBe(true);
  });
});
