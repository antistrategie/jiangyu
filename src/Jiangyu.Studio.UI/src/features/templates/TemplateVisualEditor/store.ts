import { createContext, use } from "react";
import type React from "react";
import {
  reorderByUiId,
  reorderDirectives,
  type StampedDirective,
  type StampedNode,
} from "./helpers";

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

    case "reorderCards":
      return reorderByUiId(nodes, action.fromId, action.toSlot);

    case "reorderRows":
      return nodes.map((n, i) =>
        i === action.nodeIndex
          ? { ...n, directives: reorderDirectives(n.directives, action.fromId, action.toSlot) }
          : n,
      );
  }
}

// --- Undo bookkeeping ---

/** Hard cap on retained undo frames; the oldest frame drops off first. */
export const UNDO_STACK_LIMIT = 100;

/** Two dispatches with the same coalesce key within this window merge into
 *  one undo frame, so a typing burst undoes as a single step. */
export const UNDO_COALESCE_WINDOW_MS = 1000;

/**
 * Coalescing identity for an action. Per-keystroke editors (combobox text,
 * value inputs) dispatch `updateNode` / `updateDirective` once per character
 * against the same target, so those actions key on that target and a burst
 * collapses into one undo frame. Structural actions return null and always
 * push their own frame. Commit-style inputs dispatch once per blur/Enter;
 * two commits to the same target only merge when they land within the
 * window, which still reads as one edit.
 */
export function undoCoalesceKey(action: EditorAction): string | null {
  switch (action.type) {
    case "updateNode":
      return `node:${action.nodeIndex}`;
    case "updateDirective":
      return `dir:${action.nodeIndex}:${action.dirIndex}`;
    default:
      return null;
  }
}

export interface UndoPushMeta {
  /** Coalesce key of the incoming action; null always pushes a frame. */
  readonly key: string | null;
  readonly now: number;
  /** Coalesce key and timestamp of the previous push. */
  readonly lastKey: string | null;
  readonly lastTime: number;
}

/**
 * Push `frame` onto the undo stack. When the incoming action coalesces with
 * the previous push (same non-null key, within the window), the top frame
 * already captures the state before the burst, so the stack is returned
 * unchanged (same reference). The stack is capped at UNDO_STACK_LIMIT.
 * Pure — never mutates the input array.
 */
export function pushUndoFrame<T>(stack: T[], frame: T, meta: UndoPushMeta): T[] {
  const coalesce =
    meta.key !== null &&
    meta.key === meta.lastKey &&
    meta.now - meta.lastTime <= UNDO_COALESCE_WINDOW_MS;
  if (coalesce && stack.length > 0) return stack;
  const next = [...stack, frame];
  if (next.length > UNDO_STACK_LIMIT) next.splice(0, next.length - UNDO_STACK_LIMIT);
  return next;
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

/**
 * The owning NodeCard's conversation source identifier — the templateId
 * for a ConversationTemplate patch, or the sourceId for a clone. Lets
 * deeply-nested rows (SAY/CHOICE composites within m_SerializedNodes)
 * look up the source conversation's Roles for the RoleGuid combobox
 * without threading the id through every wrapper. Null for non-
 * ConversationTemplate cards.
 */
export const ConversationSourceContext = createContext<string | null>(null);

export function useConversationSource(): string | null {
  return use(ConversationSourceContext);
}

/**
 * Ref to the editor's outer scroll container. Provided by
 * `TemplateVisualEditor` on its root `.root` div; consumed by row
 * virtualisers (DirectiveBody) so they can wire up TanStack Virtual to
 * the real scroll element without each level having to thread a
 * scrollContainerRef prop through every wrapper. Null outside the
 * editor (the consumer falls back to no virtualisation in that case).
 */
export const EditorScrollContainerContext =
  createContext<React.RefObject<HTMLDivElement | null> | null>(null);

export function useEditorScrollContainer(): React.RefObject<HTMLDivElement | null> | null {
  return use(EditorScrollContainerContext);
}

/**
 * Provided by the editor so deeply-nested CompositeEditors can read and
 * mutate their persisted collapse state without threading callbacks down
 * the tree. The control object is identity-stable for the editor's
 * lifetime and reads the live key/state maps through refs, so consumers
 * seed their own render state from `resolveState(directiveUiId)` at mount:
 * it returns the explicit persisted state (true=collapsed, false=expanded)
 * or undefined when the modder hasn't toggled this composite — the caller
 * then falls back to the content-based default (collapsed when populated).
 * `toggle` records the new state in the editor's persisted map so the
 * choice survives unmounts (node collapse, virtualised scroll-out, parse
 * reloads). Null outside the editor; callers keep plain local state with
 * no persistence.
 */
export interface CompositeCollapseControl {
  readonly resolveState: (directiveUiId: string) => boolean | undefined;
  readonly toggle: (directiveUiId: string, nextState: boolean) => void;
}

export const CompositeCollapseContext = createContext<CompositeCollapseControl | null>(null);

export function useCompositeCollapse(): CompositeCollapseControl | null {
  return use(CompositeCollapseContext);
}
