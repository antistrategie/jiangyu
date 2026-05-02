import { useEffect, useState } from "react";
import type React from "react";
import type { TemplateMember, TemplateQueryResult } from "@lib/rpc";
import { rpcCall } from "@lib/rpc";

// Per-typeName Promise cache so multiple cards / composites / descent
// groups targeting the same template share one RPC. Lifetime is the
// editor session; rebuilding the index requires reopening the editor.
const templateMembersCache = new Map<string, Promise<TemplateQueryResult>>();

function cachedTemplatesQuery(typeName: string): Promise<TemplateQueryResult> {
  let cached = templateMembersCache.get(typeName);
  if (!cached) {
    cached = rpcCall<TemplateQueryResult>("templatesQuery", { typeName });
    templateMembersCache.set(typeName, cached);
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
 * Stale-result guard: the resolved cache is keyed by typeName, so a late
 * promise from a previous (typeName) can't overwrite a newer fetch.
 */
export function useTemplateMembers(
  typeName: string | undefined,
  enabled = true,
): {
  readonly members: readonly TemplateMember[];
  readonly loaded: boolean;
} {
  const [resolved, setResolved] = useState<{
    readonly type: string;
    readonly members: readonly TemplateMember[];
  }>({ type: "", members: [] });

  useEffect(() => {
    if (!enabled || !typeName) return;
    let cancelled = false;
    void cachedTemplatesQuery(typeName)
      .then((result) => {
        if (cancelled) return;
        setResolved({ type: typeName, members: result.members ?? [] });
      })
      .catch(() => {
        if (cancelled) return;
        setResolved({ type: typeName, members: [] });
      });
    return () => {
      cancelled = true;
    };
  }, [typeName, enabled]);

  if (!typeName) return { members: [], loaded: true };
  const matched = resolved.type === typeName;
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

export function useDragReorder(
  onReorder: (fromId: string, toSlot: number) => void,
): DragReorderState {
  const [dragId, setDragId] = useState<string | null>(null);
  const [dragSlot, setDragSlot] = useState<number | null>(null);

  const endDrag = () => {
    setDragId(null);
    setDragSlot(null);
  };

  const buildHandlers = (ownerId: string, topSlot: number, bottomSlot: number) => ({
    isDragging: dragId === ownerId,
    onDragStart: () => setDragId(ownerId),
    onDragEnd: endDrag,
    onDragOver: (e: React.DragEvent) => {
      if (!dragId || dragId === ownerId) return;
      e.preventDefault();
      e.dataTransfer.dropEffect = "move";
      const rect = e.currentTarget.getBoundingClientRect();
      const y = e.clientY - rect.top;
      setDragSlot(y < rect.height / 2 ? topSlot : bottomSlot);
    },
    onDrop: () => {
      if (dragId && dragSlot !== null) onReorder(dragId, dragSlot);
      endDrag();
    },
  });

  const showIndicatorAt = (slot: number, ownerId: string | null) =>
    dragSlot === slot && (ownerId === null || dragId !== ownerId);

  return { dragId, dragSlot, buildHandlers, showIndicatorAt };
}
