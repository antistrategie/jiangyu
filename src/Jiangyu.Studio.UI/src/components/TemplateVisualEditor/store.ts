import { createContext, use } from "react";
import type React from "react";
import { reorderDirectives, type StampedDirective, type StampedNode } from "./helpers";

// Editor state lives in a reducer rather than a tree of useStates so the
// many structural mutations (add/delete/update node, the same on directives,
// per-card / per-row reorder, full-list replacement for descent operations)
// share one mutation surface and the undo wrapper can intercept everything
// in one place. Components dispatch actions via context instead of receiving
// six narrow callbacks down the tree.

export type EditorAction =
  /** Replace the entire node list. Used by parse-on-content and undo/redo
   *  paths; bypasses the undo push so it doesn't trample its own history. */
  | { type: "load"; nodes: StampedNode[] }
  | { type: "addNode"; node: StampedNode }
  | { type: "deleteNode"; nodeIndex: number }
  | { type: "updateNode"; nodeIndex: number; node: StampedNode }
  | { type: "addDirective"; nodeIndex: number; directive: StampedDirective }
  | {
      type: "updateDirective";
      nodeIndex: number;
      dirIndex: number;
      directive: StampedDirective;
    }
  | { type: "deleteDirective"; nodeIndex: number; dirIndex: number }
  | { type: "setDirectives"; nodeIndex: number; directives: StampedDirective[] }
  | { type: "reorderCards"; fromId: string; toSlot: number }
  | { type: "reorderRows"; nodeIndex: number; fromId: string; toSlot: number };

export function editorReducer(nodes: StampedNode[], action: EditorAction): StampedNode[] {
  switch (action.type) {
    case "load":
      return action.nodes;

    case "addNode":
      return [...nodes, action.node];

    case "deleteNode":
      return nodes.filter((_, i) => i !== action.nodeIndex);

    case "updateNode":
      return nodes.map((n, i) => (i === action.nodeIndex ? action.node : n));

    case "addDirective":
      return nodes.map((n, i) =>
        i === action.nodeIndex ? { ...n, directives: [...n.directives, action.directive] } : n,
      );

    case "updateDirective":
      return nodes.map((n, i) => {
        if (i !== action.nodeIndex) return n;
        return {
          ...n,
          directives: n.directives.map((d, di) => (di === action.dirIndex ? action.directive : d)),
        };
      });

    case "deleteDirective":
      return nodes.map((n, i) => {
        if (i !== action.nodeIndex) return n;
        return { ...n, directives: n.directives.filter((_, di) => di !== action.dirIndex) };
      });

    case "setDirectives":
      return nodes.map((n, i) =>
        i === action.nodeIndex ? { ...n, directives: action.directives } : n,
      );

    case "reorderCards": {
      const fromIndex = nodes.findIndex((n) => n._uiId === action.fromId);
      if (fromIndex === -1) return nodes;
      const next = [...nodes];
      const moved = next.splice(fromIndex, 1)[0];
      if (moved === undefined) return nodes;
      const insertAt = action.toSlot > fromIndex ? action.toSlot - 1 : action.toSlot;
      next.splice(insertAt, 0, moved);
      return next;
    }

    case "reorderRows":
      return nodes.map((n, i) =>
        i === action.nodeIndex
          ? { ...n, directives: reorderDirectives(n.directives, action.fromId, action.toSlot) }
          : n,
      );
  }
}

/**
 * Dispatch handle for the editor's mutation reducer. Available to every
 * descendant component so they don't need to thread per-action callbacks
 * through props. The top-level provider wraps `dispatch` to also push the
 * pre-mutation snapshot onto the undo stack (except for `load` actions,
 * which are themselves the result of parse / undo / redo and shouldn't
 * trample the history).
 */
export const EditorDispatchContext = createContext<React.Dispatch<EditorAction> | null>(null);

export function useEditorDispatch(): React.Dispatch<EditorAction> {
  const dispatch = use(EditorDispatchContext);
  if (!dispatch) throw new Error("useEditorDispatch must be used inside an EditorDispatchContext");
  return dispatch;
}

/**
 * The owning node's index. Provided by NodeCard so deeply-nested rows
 * (DirectiveRow, DescentGroup, FieldAdder, CompositeEditor) can build
 * dispatch actions without receiving a nodeIndex prop at every level.
 * Defaults to -1 outside a NodeCard so misuse fails loudly.
 */
export const NodeIndexContext = createContext<number>(-1);

export function useNodeIndex(): number {
  const ni = use(NodeIndexContext);
  if (ni < 0) throw new Error("useNodeIndex must be used inside a NodeIndexContext");
  return ni;
}
