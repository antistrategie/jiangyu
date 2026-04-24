import { describe, expect, it, vi } from "vitest";
import { PALETTE_SCOPE } from "@lib/palette/actions.ts";
import { buildProjectActions, type ProjectActionHandlers } from "@lib/palette/appActions.ts";

function makeHandlers(): ProjectActionHandlers & {
  calls: { open: number; close: number; reveal: number; switch: string[] };
} {
  const calls = { open: 0, close: 0, reveal: 0, switch: [] as string[] };
  return {
    calls,
    openProject: () => void calls.open++,
    closeProject: () => void calls.close++,
    revealProject: () => void calls.reveal++,
    switchProject: (p) => calls.switch.push(p),
  };
}

describe("buildProjectActions", () => {
  it("returns only Open Project when no project is open and no recents", () => {
    const actions = buildProjectActions(null, [], makeHandlers());
    expect(actions.map((a) => a.id)).toEqual(["app.openProject"]);
  });

  it("adds Close Project and Reveal when a project is open", () => {
    const actions = buildProjectActions("/a/b", [], makeHandlers());
    expect(actions.map((a) => a.id)).toEqual([
      "app.openProject",
      "app.closeProject",
      "app.revealProject",
    ]);
  });

  it("lists recent projects as Open Recent entries with path in desc", () => {
    const actions = buildProjectActions(null, ["/home/a", "/home/b"], makeHandlers());
    expect(actions.map((a) => a.id)).toEqual([
      "app.openProject",
      "app.openRecent:/home/a",
      "app.openRecent:/home/b",
    ]);
    const recent = actions.find((a) => a.id === "app.openRecent:/home/a");
    expect(recent?.label).toBe("Open Recent: a");
    expect(recent?.desc).toBe("/home/a");
  });

  it("excludes the currently-open project from recents", () => {
    const actions = buildProjectActions("/home/a", ["/home/a", "/home/b"], makeHandlers());
    expect(actions.map((a) => a.id)).toEqual([
      "app.openProject",
      "app.closeProject",
      "app.revealProject",
      "app.openRecent:/home/b",
    ]);
  });

  it("wires the run callbacks to the provided handlers", () => {
    const h = makeHandlers();
    const actions = buildProjectActions("/home/a", ["/home/b"], h);
    const find = (id: string) => actions.find((a) => a.id === id)!;

    void find("app.openProject").run();
    void find("app.closeProject").run();
    void find("app.revealProject").run();
    void find("app.openRecent:/home/b").run();

    expect(h.calls).toEqual({ open: 1, close: 1, reveal: 1, switch: ["/home/b"] });
  });

  it("tags every action with the PROJECT scope", () => {
    const actions = buildProjectActions("/home/a", ["/home/b", "/home/c"], makeHandlers());
    expect(actions.every((a) => a.scope === PALETTE_SCOPE.Project)).toBe(true);
  });

  it("does not mutate the input recents array", () => {
    const recents: readonly string[] = ["/home/a", "/home/b"];
    const snapshot = [...recents];
    buildProjectActions("/home/a", recents, makeHandlers());
    expect([...recents]).toEqual(snapshot);
  });

  it("handles switchProject being called asynchronously without stale capture", () => {
    const h = makeHandlers();
    const spy = vi.spyOn(h, "switchProject");
    const actions = buildProjectActions(null, ["/home/x"], h);
    void actions.find((a) => a.id === "app.openRecent:/home/x")!.run();
    expect(spy).toHaveBeenCalledWith("/home/x");
  });
});
