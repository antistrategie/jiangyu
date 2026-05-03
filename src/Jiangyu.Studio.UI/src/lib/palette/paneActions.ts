import { useMemo } from "react";
import {
  BROWSER_KIND_META,
  getActivePane,
  getAllPanes,
  type BrowserPane,
  type Layout,
} from "@lib/layout";
import { useAiEnabled } from "@lib/settings";
import { useLayoutStore } from "@lib/panes/layoutStore";
import { PALETTE_SCOPE, type PaletteAction } from "./actions";

interface UsePaneActionsParams {
  readonly projectPath: string | null;
  readonly layout: Layout;
  readonly onTearOutPane: (paneId: string) => void;
}

// Non-whitespace delimiter for the pane-id signature. Pane ids are base36 +
// underscore only; this character never appears inside them, so join+split
// round-trips cleanly while keeping the memo key a plain string (cheap to
// compare vs. the full Layout object, which changes on every drag-resize).
const PANE_ID_DELIM = "";

export function usePaneActions({
  projectPath,
  layout,
  onTearOutPane,
}: UsePaneActionsParams): PaletteAction[] {
  // Topology-only signature so weight drags don't rebuild the palette.
  const paneIdSignature = useMemo(
    () =>
      getAllPanes(layout)
        .map((p) => p.id)
        .join(PANE_ID_DELIM),
    [layout],
  );
  const activePaneId = layout.activePaneId;
  const [aiEnabled] = useAiEnabled();

  return useMemo<PaletteAction[]>(() => {
    if (projectPath === null) return [];
    const paneIds = paneIdSignature.length > 0 ? paneIdSignature.split(PANE_ID_DELIM) : [];
    const active = activePaneId !== null && paneIds.includes(activePaneId) ? activePaneId : null;
    const actions: PaletteAction[] = [];

    for (const kind of Object.keys(BROWSER_KIND_META) as BrowserPane["kind"][]) {
      if (kind === "agent" && !aiEnabled) continue;
      const meta = BROWSER_KIND_META[kind];
      actions.push({
        id: `view.openBrowser:${kind}`,
        label: `Open ${meta.label}`,
        scope: PALETTE_SCOPE.View,
        run: () => useLayoutStore.getState().splitRight(kind),
      });
    }

    actions.push(
      {
        id: "view.splitRight",
        label: "Split Right",
        scope: PALETTE_SCOPE.View,
        cn: "右分屏",
        kbd: "Ctrl+\\",
        run: () => useLayoutStore.getState().splitRight(),
      },
      {
        id: "view.splitDown",
        label: "Split Down",
        scope: PALETTE_SCOPE.View,
        cn: "下分屏",
        kbd: "Ctrl+Shift+\\",
        run: () => useLayoutStore.getState().splitDown(),
      },
    );

    if (active !== null) {
      actions.push(
        {
          id: "view.closePane",
          label: "Close Pane",
          scope: PALETTE_SCOPE.View,
          cn: "关闭面板",
          kbd: "Ctrl+Shift+W",
          run: () => useLayoutStore.getState().closePane(active),
        },
        {
          id: "view.tearOutActivePane",
          label: "Move active pane to new window",
          scope: PALETTE_SCOPE.View,
          run: () => {
            const pane = getActivePane(useLayoutStore.getState().layout);
            if (pane === null) return;
            onTearOutPane(pane.id);
          },
        },
      );
    }

    if (paneIds.length > 1 && active !== null) {
      const idx = paneIds.indexOf(active);
      // Modular arithmetic against a non-empty array always yields a defined entry.
      const nextId = paneIds[(idx + 1) % paneIds.length];
      const prevId = paneIds[(idx - 1 + paneIds.length) % paneIds.length];
      if (nextId === undefined || prevId === undefined) return actions;
      actions.push(
        {
          id: "view.focusNextPane",
          label: "Focus Next Pane",
          scope: PALETTE_SCOPE.View,
          kbd: "Ctrl+Shift+]",
          run: () => useLayoutStore.getState().setActivePane(nextId),
        },
        {
          id: "view.focusPrevPane",
          label: "Focus Previous Pane",
          scope: PALETTE_SCOPE.View,
          kbd: "Ctrl+Shift+[",
          run: () => useLayoutStore.getState().setActivePane(prevId),
        },
      );
    }

    return actions;
  }, [projectPath, paneIdSignature, activePaneId, onTearOutPane, aiEnabled]);
}
