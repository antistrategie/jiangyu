import type { ComponentType } from "react";
import { AgentPanel } from "@features/agent/AgentPanel/AgentPanel";
import { UiInspector } from "@features/bridge/UiInspector/UiInspector";
import type { SimplePaneKind } from "./layout";

// Self-contained panes render the same component whether docked in the workspace or torn out
// into a secondary window (no project/file/state props). Declared once so the workspace
// (BrowserPane) and secondary-window (PaneWindow) renderers stay in sync: the Record is
// exhaustive, so a new SimplePaneKind is a compile error until it appears here.
const SIMPLE_PANE_COMPONENTS: Record<SimplePaneKind, ComponentType> = {
  agent: AgentPanel,
  uiInspector: UiInspector,
};

/** Renders the component for a self-contained pane kind. Used by both pane renderers. */
export function SimplePaneView({ kind }: { kind: SimplePaneKind }) {
  const Component = SIMPLE_PANE_COMPONENTS[kind];
  return <Component />;
}
