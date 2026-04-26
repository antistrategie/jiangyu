import { useEffect } from "react";
import { getActiveCodePane, getActivePane, getAllPanes } from "@lib/layout";
import type { PaletteAction } from "@lib/palette/actions";
import type { CompileState } from "@lib/compile";
import { useLayoutStore } from "@lib/panes/layoutStore";
import { matchBinding, type KeyBinding } from "./shortcuts";

interface UseAppShortcutsParams {
  readonly setPaletteOpen: React.Dispatch<React.SetStateAction<boolean>>;
  readonly toggleSidebar: () => void;

  readonly projectPathRef: React.RefObject<string | null>;
  readonly compileStateRef: React.RefObject<CompileState>;
  readonly startCompileRef: React.RefObject<() => void>;
  readonly allActionsRef: React.RefObject<readonly PaletteAction[]>;
}

// Top-level keyboard shortcuts. Bindings fire the palette-registered action
// where one exists (so adding a shortcut to an existing action doesn't
// duplicate the behaviour) and fall back to direct store calls for flow
// that isn't a palette action (pane focus cycling, fullscreen exit,
// palette toggle itself).
export function useAppShortcuts({
  setPaletteOpen,
  toggleSidebar,
  projectPathRef,
  compileStateRef,
  startCompileRef,
  allActionsRef,
}: UseAppShortcutsParams): void {
  useEffect(() => {
    const runActionById = (id: string): boolean => {
      const action = allActionsRef.current.find((a) => a.id === id);
      if (!action) return false;
      void action.run();
      return true;
    };
    const focusAdjacentPane = (direction: 1 | -1): boolean => {
      const layout = useLayoutStore.getState().layout;
      const panes = getAllPanes(layout);
      const active = getActivePane(layout);
      if (active === null || panes.length < 2) return false;
      const idx = panes.findIndex((p) => p.id === active.id);
      const target = panes[(idx + direction + panes.length) % panes.length];
      if (target === undefined) return false;
      useLayoutStore.getState().setActivePane(target.id);
      return true;
    };
    const shortcuts: { binding: KeyBinding; run: () => boolean }[] = [
      {
        binding: { key: "Escape" },
        run: () => {
          if (useLayoutStore.getState().fullscreenPaneId === null) return false;
          useLayoutStore.getState().setFullscreenPaneId(null);
          return true;
        },
      },
      {
        binding: { mod: true, shift: true, key: "p" },
        run: () => {
          setPaletteOpen((o) => !o);
          return true;
        },
      },
      {
        binding: { mod: true, key: "k" },
        run: () => {
          setPaletteOpen((o) => !o);
          return true;
        },
      },
      // Ctrl+S delegates to the palette-registered save action so it works
      // even when the editor doesn't have keyboard focus.
      { binding: { mod: true, key: "s" }, run: () => runActionById("editor.save") },
      {
        binding: { mod: true, key: "w" },
        run: () => {
          const active = getActiveCodePane(useLayoutStore.getState().layout);
          if (active?.activeTab == null) return false;
          useLayoutStore.getState().closeTabsInPane(active.id, [active.activeTab]);
          return true;
        },
      },
      {
        binding: { mod: true, shift: true, key: "w" },
        run: () => {
          const active = getActivePane(useLayoutStore.getState().layout);
          if (active === null) return false;
          useLayoutStore.getState().closePane(active.id);
          return true;
        },
      },
      {
        binding: { mod: true, key: "\\" },
        run: () => {
          useLayoutStore.getState().splitRight();
          return true;
        },
      },
      {
        binding: { mod: true, shift: true, key: "|" },
        run: () => {
          useLayoutStore.getState().splitDown();
          return true;
        },
      },
      { binding: { mod: true, shift: true, key: "]" }, run: () => focusAdjacentPane(1) },
      { binding: { mod: true, shift: true, key: "[" }, run: () => focusAdjacentPane(-1) },
      {
        binding: { mod: true, key: "b" },
        run: () => {
          toggleSidebar();
          return true;
        },
      },
      {
        binding: { mod: true, shift: true, key: "b" },
        run: () => {
          if (projectPathRef.current === null) return false;
          if (compileStateRef.current.status === "running") return false;
          startCompileRef.current();
          return true;
        },
      },
    ];
    const onKey = (e: KeyboardEvent) => {
      for (const { binding, run } of shortcuts) {
        if (!matchBinding(e, binding)) continue;
        if (run()) e.preventDefault();
        return;
      }
    };
    window.addEventListener("keydown", onKey);
    return () => window.removeEventListener("keydown", onKey);
  }, [
    setPaletteOpen,
    toggleSidebar,
    projectPathRef,
    compileStateRef,
    startCompileRef,
    allActionsRef,
  ]);
}
