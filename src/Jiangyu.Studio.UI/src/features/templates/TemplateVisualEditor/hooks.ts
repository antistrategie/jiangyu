import { useCallback, useEffect, useRef, useState } from "react";
import type React from "react";
import type { TemplateMember, TemplateQueryResult } from "@shared/rpc";
import { rpcCall } from "@shared/rpc";

// Per-(typeName,elementType) Promise cache so multiple cards / composites
// / descent groups targeting the same template share one RPC. Lifetime is
// the editor session; rebuilding the index requires reopening the editor.
const templateMembersCache = new Map<string, Promise<TemplateQueryResult>>();

function cachedTemplatesQuery(
  typeName: string,
  elementType: string | undefined,
): Promise<TemplateQueryResult> {
  // Cache key includes elementType because the resolver picks different
  // candidates when an element-type context narrows an ambiguous short
  // name. Without this, an "Attack" lookup with no context would
  // permanently shadow the SkillEventHandler context that needs a
  // different result.
  const key = elementType ? `${typeName}|${elementType}` : typeName;
  let cached = templateMembersCache.get(key);
  if (!cached) {
    cached = rpcCall<TemplateQueryResult>("templatesQuery", {
      typeName,
      elementType,
    });
    templateMembersCache.set(key, cached);
  }
  return cached;
}

/**
 * Fetch the member list for `typeName`. Returns `members=[]`/`loaded=false`
 * while the RPC is pending, then the resolved members and `loaded=true`
 * (empty list on RPC failure).
 *
 * Pass `enabled=false` to suppress the fetch (e.g. a collapsed NodeCard).
 * The previously-resolved members stay cached so re-enabling doesn't flash
 * a loading state. The fetch is only re-issued when typeName changes or
 * when re-enabling after the typeName changed under the gate.
 *
 * `elementType` is the parent collection's element-type when typeName is a
 * subtype short name within a polymorphic family (e.g. typeName="Attack"
 * with elementType="SkillEventHandlerTemplate"). The resolver uses this to
 * pick the correct candidate when the short name is otherwise ambiguous.
 *
 * Stale-result guard: the resolved cache is keyed by typeName + elementType,
 * so a late promise from a previous (typeName, elementType) can't overwrite
 * a newer fetch.
 */
export function useTemplateMembers(
  typeName: string | undefined,
  enabled = true,
  elementType?: string,
): {
  readonly members: readonly TemplateMember[];
  readonly loaded: boolean;
} {
  const [resolved, setResolved] = useState<{
    readonly type: string;
    readonly elementType: string | undefined;
    readonly members: readonly TemplateMember[];
  }>({ type: "", elementType: undefined, members: [] });

  useEffect(() => {
    if (!enabled || !typeName) return;
    let cancelled = false;
    void cachedTemplatesQuery(typeName, elementType)
      .then((result) => {
        if (cancelled) return;
        setResolved({ type: typeName, elementType, members: result.members ?? [] });
      })
      .catch(() => {
        if (cancelled) return;
        setResolved({ type: typeName, elementType, members: [] });
      });
    return () => {
      cancelled = true;
    };
  }, [typeName, enabled, elementType]);

  if (!typeName) return { members: [], loaded: true };
  const matched = resolved.type === typeName && resolved.elementType === elementType;
  return {
    members: matched ? resolved.members : [],
    loaded: matched,
  };
}

/**
 * Drag-reorder state machine for a vertical list of rows. Owns the
 * (dragId, dragSlot) pair plus helpers for the two patterns the editor
 * uses today: per-row drag handlers (`buildHandlers`) and the indicator
 * predicate (`showIndicatorAt`).
 *
 * Generalises across loose rows (topSlot=index, bottomSlot=index+1) and
 * descent groups (topSlot=startFlatIndex, bottomSlot=endFlatIndex). The
 * grip's `setData` call stays in the consumer because the payload mime
 * varies (cards vs rows vs groups).
 */
export interface DragReorderState {
  readonly dragId: string | null;
  readonly dragSlot: number | null;
  buildHandlers: (
    ownerId: string,
    topSlot: number,
    bottomSlot: number,
  ) => {
    readonly isDragging: boolean;
    readonly onDragStart: () => void;
    readonly onDragEnd: () => void;
    readonly onDragOver: (e: React.DragEvent) => void;
    readonly onDrop: () => void;
  };
  showIndicatorAt: (slot: number, ownerId: string | null) => boolean;
}

// Document-level drop fallback. WebKitGTK (the Linux WebView Studio runs
// under) does not deliver React synthetic drop events to the dragged-over
// element even when dragover preventDefaults — the React tree never sees
// the drop and dragend fires straight after dragover. A native drop
// listener at the document level still receives the event, so we route
// the reorder commit through there for the row mime we care about.
// Chromium-based embeds also deliver the per-element React drop normally
// in addition to the document drop; either path clears drag state, so
// the duplicate is a no-op (the second path sees dragIdRef === null).
function useDocumentDropFallback(
  dragIdRef: React.RefObject<string | null>,
  dragSlotRef: React.RefObject<number | null>,
  onReorderRef: React.RefObject<(fromId: string, toSlot: number) => void>,
  cleanup: () => void,
) {
  useEffect(() => {
    const onDocDrop = (e: DragEvent) => {
      if (!e.dataTransfer?.types.includes("application/x-jiangyu-card-reorder")) return;
      e.preventDefault();
      const fromId = dragIdRef.current;
      const toSlot = dragSlotRef.current;
      if (fromId && toSlot !== null) {
        onReorderRef.current(fromId, toSlot);
      }
      cleanup();
    };
    const onDocDragOver = (e: DragEvent) => {
      // WebKitGTK only delivers the document-level drop when the
      // document itself marks the gesture as accepted via a dragover
      // preventDefault. Per-card preventDefaults aren't enough.
      if (e.dataTransfer?.types.includes("application/x-jiangyu-card-reorder")) {
        e.preventDefault();
      }
    };
    document.addEventListener("drop", onDocDrop);
    document.addEventListener("dragover", onDocDragOver);
    return () => {
      document.removeEventListener("drop", onDocDrop);
      document.removeEventListener("dragover", onDocDragOver);
    };
  }, [dragIdRef, dragSlotRef, onReorderRef, cleanup]);
}

export function useDragReorder(
  onReorder: (fromId: string, toSlot: number) => void,
): DragReorderState {
  const [dragId, setDragId] = useState<string | null>(null);
  const [dragSlot, setDragSlot] = useState<number | null>(null);

  // Latest-value refs so the handlers returned by buildHandlers can read
  // current drag state without closing over it. Lets each row's handler
  // bag stay identity-stable across parent re-renders, which is the
  // load-bearing precondition for memo()'d NodeCard/SetRow to actually
  // skip work when scrolling or editing siblings.
  const dragIdRef = useRef<string | null>(null);
  const dragSlotRef = useRef<number | null>(null);
  const onReorderRef = useRef(onReorder);
  useEffect(() => {
    dragIdRef.current = dragId;
  }, [dragId]);
  useEffect(() => {
    dragSlotRef.current = dragSlot;
  }, [dragSlot]);
  useEffect(() => {
    onReorderRef.current = onReorder;
  }, [onReorder]);

  // Per-(ownerId, topSlot, bottomSlot) handler bag. Memoised so handing
  // the same triple back produces a referentially-identical bag, so
  // memo'd row components skip re-renders when only the parent's state
  // shifted. Cache key encodes the triple; each cell stores both
  // handlers AND the isDragging boolean snapshot for the current render.
  const handlersCacheRef = useRef(
    new Map<
      string,
      {
        readonly handlers: ReturnType<DragReorderState["buildHandlers"]>;
        readonly topSlot: number;
        readonly bottomSlot: number;
      }
    >(),
  );

  const buildHandlers = useCallback((ownerId: string, topSlot: number, bottomSlot: number) => {
    const cache = handlersCacheRef.current;
    const isDragging = dragIdRef.current === ownerId;
    const cached = cache.get(ownerId);
    // Reuse the cached bag when slot bounds haven't changed; otherwise
    // build a fresh one. The functions inside the bag always read from
    // refs, so they don't need to change even when isDragging does.
    // We DO rebuild the wrapping object when isDragging flips, so the
    // consumer's isDragging prop reflects current state.
    if (
      cached?.topSlot === topSlot &&
      cached.bottomSlot === bottomSlot &&
      cached.handlers.isDragging === isDragging
    ) {
      return cached.handlers;
    }
    // Refs are written synchronously alongside setState so subsequent
    // native drag events in the same gesture see up-to-date drag state.
    // Without this, the useEffect-driven ref sync lags by a render and
    // `onDragOver` returns early before calling preventDefault(), which
    // causes the browser to silently reject every drop target.
    const handlers = {
      isDragging,
      onDragStart: () => {
        dragIdRef.current = ownerId;
        setDragId(ownerId);
      },
      onDragEnd: () => {
        dragIdRef.current = null;
        dragSlotRef.current = null;
        setDragId(null);
        setDragSlot(null);
      },
      onDragOver: (e: React.DragEvent) => {
        const currentDragId = dragIdRef.current;
        if (!currentDragId || currentDragId === ownerId) return;
        e.preventDefault();
        e.dataTransfer.dropEffect = "move";
        const rect = e.currentTarget.getBoundingClientRect();
        const y = e.clientY - rect.top;
        const nextSlot = y < rect.height / 2 ? topSlot : bottomSlot;
        dragSlotRef.current = nextSlot;
        setDragSlot(nextSlot);
      },
      onDrop: () => {
        const currentDragId = dragIdRef.current;
        const currentDragSlot = dragSlotRef.current;
        if (currentDragId && currentDragSlot !== null) {
          onReorderRef.current(currentDragId, currentDragSlot);
        }
        dragIdRef.current = null;
        dragSlotRef.current = null;
        setDragId(null);
        setDragSlot(null);
      },
    };
    cache.set(ownerId, { handlers, topSlot, bottomSlot });
    return handlers;
  }, []);

  const showIndicatorAt = useCallback(
    (slot: number, ownerId: string | null) =>
      dragSlot === slot && (ownerId === null || dragId !== ownerId),
    [dragId, dragSlot],
  );

  // Cleanup helper closes over the same setState pair as the per-card
  // handlers so the document-level fallback can reset state without
  // duplicating the clear logic.
  const clearDragState = useCallback(() => {
    dragIdRef.current = null;
    dragSlotRef.current = null;
    setDragId(null);
    setDragSlot(null);
  }, []);
  useDocumentDropFallback(dragIdRef, dragSlotRef, onReorderRef, clearDragState);

  return { dragId, dragSlot, buildHandlers, showIndicatorAt };
}
