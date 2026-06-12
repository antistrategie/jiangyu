// @vitest-environment jsdom
import { describe, expect, it, afterEach } from "vitest";
import { renderHook, cleanup } from "@testing-library/react";
import { usePaneActions } from "./paneActions";
import { EMPTY_LAYOUT, openFile, splitRight } from "./layout";

afterEach(cleanup);

describe("usePaneActions", () => {
  it("registers pane-scoped actions when a pane is active", () => {
    // Two panes with one active: the close / tear-out / focus-cycling
    // actions must all be present. Guards the pane-id signature round-trip
    // (a broken delimiter once made `active` always null, silently dropping
    // these four actions from the palette).
    const layout = splitRight(openFile(EMPTY_LAYOUT, "/p/a.ts"));
    const { result } = renderHook(() =>
      usePaneActions({ projectPath: "/p", layout, onTearOutPane: () => undefined }),
    );
    const ids = result.current.map((a) => a.id);
    expect(ids).toContain("view.closePane");
    expect(ids).toContain("view.tearOutActivePane");
    expect(ids).toContain("view.focusNextPane");
    expect(ids).toContain("view.focusPrevPane");
  });

  it("returns no actions without a project", () => {
    const layout = openFile(EMPTY_LAYOUT, "/p/a.ts");
    const { result } = renderHook(() =>
      usePaneActions({ projectPath: null, layout, onTearOutPane: () => undefined }),
    );
    expect(result.current).toEqual([]);
  });
});
