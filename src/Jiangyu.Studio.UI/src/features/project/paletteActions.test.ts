import { describe, expect, it } from "vitest";
import { PALETTE_SCOPE } from "@shared/palette/actions";
import { buildProjectActions, type ProjectActionHandlers } from "@features/project/paletteActions";

function makeHandlers(): ProjectActionHandlers & {
  calls: { open: number; close: number; reveal: number };
} {
  const calls = { open: 0, close: 0, reveal: 0 };
  return {
    calls,
    openProject: () => void calls.open++,
    closeProject: () => void calls.close++,
    revealProject: () => void calls.reveal++,
  };
}

describe("buildProjectActions", () => {
  it("returns only Open Project when no project is open", () => {
    const actions = buildProjectActions(null, makeHandlers());
    expect(actions.map((a) => a.id)).toEqual(["app.openProject"]);
  });

  it("adds Close Project and Reveal when a project is open", () => {
    const actions = buildProjectActions("/a/b", makeHandlers());
    expect(actions.map((a) => a.id)).toEqual([
      "app.openProject",
      "app.closeProject",
      "app.revealProject",
    ]);
  });

  it("wires the run callbacks to the provided handlers", () => {
    const h = makeHandlers();
    const actions = buildProjectActions("/home/a", h);
    const find = (id: string) => actions.find((a) => a.id === id)!;

    void find("app.openProject").run();
    void find("app.closeProject").run();
    void find("app.revealProject").run();

    expect(h.calls).toEqual({ open: 1, close: 1, reveal: 1 });
  });

  it("tags every action with the PROJECT scope", () => {
    const actions = buildProjectActions("/home/a", makeHandlers());
    expect(actions.every((a) => a.scope === PALETTE_SCOPE.Project)).toBe(true);
  });
});
