import type { EditorNode } from "../types";
import { stampNodes as stampNodesHelper, type StampedNode } from "../helpers";

// Module-state counter for the editor's stamping helpers. Tests don't go
// through this; they pass their own deterministic generator into
// stampDirective / stampNodes from helpers.

let _nextId = 0;

export function uiId(): string {
  return `_ui_${++_nextId}`;
}

export const stampNodes = (nodes: EditorNode[]): StampedNode[] => stampNodesHelper(nodes, uiId);
