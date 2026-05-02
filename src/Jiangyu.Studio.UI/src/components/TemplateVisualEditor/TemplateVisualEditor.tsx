import React, { useCallback, useEffect, useRef, useState } from "react";
import { Plus } from "lucide-react";
import type { EditorError } from "./types";
import { parseCrossMemberPayload } from "@lib/drag/crossMember";
import {
  parseCrossInstancePayload,
  INSTANCE_DRAG_TAG,
  MEMBER_DRAG_TAG,
} from "@lib/drag/crossInstance";
import { useToastStore } from "@lib/toast";
import styles from "./TemplateVisualEditor.module.css";
import { stripUiIds, type StampedNode } from "./helpers";
import { useDragReorder } from "./hooks";
import { EditorDispatchContext, NodeIndexContext, editorReducer, type EditorAction } from "./store";
import { uiId, stampNodes } from "./shared/uiId";
import {
  templatesParse,
  templatesSerialise,
  invalidateProjectClonesCache,
} from "./shared/rpcHelpers";
import { NodeCard } from "./cards/NodeCard";

// --- Main component ---

interface TemplateVisualEditorProps {
  readonly content: string;
  readonly filePath?: string | null | undefined;
  readonly onChange: (content: string) => void;
  readonly onRequestSourceMode?: (() => void) | undefined;
}

// Persist collapsed state across remounts (tab switches) — keyed by node uiId
const collapsedCache = new Map<string, Set<string>>();

export function TemplateVisualEditor({
  content,
  filePath,
  onChange,
  onRequestSourceMode,
}: TemplateVisualEditorProps) {
  const [nodes, setNodes] = useState<StampedNode[]>([]);
  const [parseErrors, setParseErrors] = useState<EditorError[]>([]);
  const [rpcError, setRpcError] = useState<string | null>(null);
  const cacheKey = filePath ?? "";
  const [collapsed, setCollapsed] = useState<Set<string>>(
    () => collapsedCache.get(cacheKey) ?? new Set(),
  );
  const lastSerialisedRef = useRef<string>("");
  const serialiseVersionRef = useRef(0);

  // Undo/redo history
  const undoStackRef = useRef<StampedNode[][]>([]);
  const redoStackRef = useRef<StampedNode[][]>([]);
  // Mirror nodes into a ref so async callbacks see the latest value without
  // stale closures. Synced via effect after each render.
  const nodesRef = useRef<StampedNode[]>(nodes);
  useEffect(() => {
    nodesRef.current = nodes;
  });

  // Debounced serialise of `updated` back to KDL text. Side-effect-only;
  // run after each mutation to push the new content out via onChange and
  // refresh parse-error diagnostics.
  const serialiseTimerRef = useRef<ReturnType<typeof setTimeout> | undefined>(undefined);

  const scheduleSerialise = useCallback(
    (updated: StampedNode[]) => {
      const version = ++serialiseVersionRef.current;
      clearTimeout(serialiseTimerRef.current);
      serialiseTimerRef.current = setTimeout(() => {
        const doc = { nodes: updated, errors: [] as EditorError[] };
        void templatesSerialise(stripUiIds(doc)).then(async (result) => {
          if (version !== serialiseVersionRef.current) return;
          lastSerialisedRef.current = result.text;
          onChange(result.text);
          // Refresh validation errors after local edits — the parse-on-content
          // path is gated by `content === lastSerialisedRef.current` to avoid
          // clobbering in-progress nodes, so errors would otherwise go stale.
          try {
            const parsed = await templatesParse(result.text);
            if (version !== serialiseVersionRef.current) return;
            setParseErrors(parsed.errors);
          } catch {
            /* keep previous errors — a failed re-parse shouldn't wipe them */
          }
        });
      }, 150);
    },
    [onChange],
  );

  // The dispatch handle exposed via context. Wraps the pure reducer with
  // undo-stack bookkeeping and the serialise side effect.
  const dispatch = useCallback<React.Dispatch<EditorAction>>(
    (action) => {
      const next = editorReducer(nodesRef.current, action);
      if (action.type !== "load") {
        undoStackRef.current = [...undoStackRef.current, nodesRef.current];
        redoStackRef.current = [];
        invalidateProjectClonesCache();
        scheduleSerialise(next);
      }
      setNodes(next);
    },
    [scheduleSerialise],
  );

  // Parse via RPC when content changes externally
  useEffect(() => {
    if (content === lastSerialisedRef.current) return;

    void templatesParse(content)
      .then((doc) => {
        dispatch({ type: "load", nodes: stampNodes(doc.nodes) });
        setParseErrors(doc.errors);
        setRpcError(null);
        undoStackRef.current = [];
        redoStackRef.current = [];
      })
      .catch((err: unknown) => {
        setRpcError(err instanceof Error ? err.message : "Parse RPC failed");
      });
  }, [content, dispatch]);

  // Undo/redo keyboard handler.
  useEffect(() => {
    const handler = (e: KeyboardEvent) => {
      const isMod = e.metaKey || e.ctrlKey;
      if (!isMod || e.key.toLowerCase() !== "z") return;
      e.preventDefault();

      const el = document.activeElement;
      if (el instanceof HTMLInputElement || el instanceof HTMLTextAreaElement) {
        if (el.value !== el.defaultValue) {
          el.value = el.defaultValue;
          return;
        }
      }

      if (e.shiftKey) {
        const next = redoStackRef.current.pop();
        if (next === undefined) return;
        undoStackRef.current = [...undoStackRef.current, nodesRef.current];
        dispatch({ type: "load", nodes: next });
        scheduleSerialise(next);
      } else {
        const prev = undoStackRef.current.pop();
        if (prev === undefined) return;
        redoStackRef.current = [...redoStackRef.current, nodesRef.current];
        dispatch({ type: "load", nodes: prev });
        scheduleSerialise(prev);
      }
    };
    document.addEventListener("keydown", handler, true);
    return () => document.removeEventListener("keydown", handler, true);
  }, [dispatch, scheduleSerialise]);

  const handleToggleCollapse = useCallback(
    (nodeUiId: string) => {
      setCollapsed((prev) => {
        const next = new Set(prev);
        if (next.has(nodeUiId)) next.delete(nodeUiId);
        else next.add(nodeUiId);
        collapsedCache.set(cacheKey, next);
        return next;
      });
    },
    [cacheKey],
  );

  const handleAddNode = useCallback(
    (kind: "Patch" | "Clone") => {
      dispatch({
        type: "addNode",
        node: { kind, templateType: "", directives: [], _uiId: uiId() },
      });
    },
    [dispatch],
  );

  const cardReorder = useDragReorder((fromId, toSlot) =>
    dispatch({ type: "reorderCards", fromId, toSlot }),
  );

  const [bottomDragOver, setBottomDragOver] = useState<"Patch" | "Clone" | "reject" | false>(false);

  const handleBottomDrop = useCallback(
    (kind: "Patch" | "Clone", e: React.DragEvent) => {
      setBottomDragOver(false);
      const raw = e.dataTransfer.getData("text/plain");
      const inst = parseCrossInstancePayload(raw);
      if (!inst) {
        if (parseCrossMemberPayload(raw)) {
          useToastStore.getState().push({
            variant: "error",
            message: "Fields can't be dropped on the node area",
            detail: "Drag the template instance instead.",
          });
        }
        return;
      }
      e.preventDefault();
      const newNode: StampedNode =
        kind === "Patch"
          ? {
              kind: "Patch",
              templateType: inst.className,
              templateId: inst.name,
              directives: [],
              _uiId: uiId(),
            }
          : {
              kind: "Clone",
              templateType: inst.className,
              sourceId: inst.name,
              cloneId: "",
              directives: [],
              _uiId: uiId(),
            };
      dispatch({ type: "addNode", node: newNode });
    },
    [dispatch],
  );

  return (
    <EditorDispatchContext value={dispatch}>
      <div className={styles.root}>
        {rpcError && (
          <div className={styles.parseError}>
            <span className={styles.errorSummary}>RPC error: {rpcError}</span>
            {onRequestSourceMode && (
              <button type="button" className={styles.fallbackBtn} onClick={onRequestSourceMode}>
                Switch to Source
              </button>
            )}
          </div>
        )}

        {parseErrors.length > 0 && (
          <div className={styles.errorPanel}>
            <div className={styles.parseError}>
              <span className={styles.errorSummary}>
                {parseErrors.length} parse error{parseErrors.length > 1 ? "s" : ""}
              </span>
              {onRequestSourceMode && (
                <button type="button" className={styles.fallbackBtn} onClick={onRequestSourceMode}>
                  Switch to Source
                </button>
              )}
            </div>
            <div className={styles.errorList}>
              {parseErrors.map((err) => (
                <div key={`${err.line ?? "?"}:${err.message}`} className={styles.errorItem}>
                  {err.line != null && <span className={styles.errorLine}>line {err.line}</span>}
                  <span className={styles.errorMessage}>{err.message}</span>
                </div>
              ))}
            </div>
          </div>
        )}

        {nodes.map((node, ni) => {
          const handlers = cardReorder.buildHandlers(node._uiId, ni, ni + 1);
          return (
            <NodeIndexContext key={node._uiId} value={ni}>
              {cardReorder.showIndicatorAt(ni, node._uiId) && (
                <div className={styles.dropIndicator} />
              )}
              <NodeCard
                node={node}
                collapsed={collapsed.has(node._uiId)}
                onToggleCollapse={() => handleToggleCollapse(node._uiId)}
                isDragging={handlers.isDragging}
                onDragStart={handlers.onDragStart}
                onDragEnd={handlers.onDragEnd}
                onDragOverCard={handlers.onDragOver}
                onDropCard={handlers.onDrop}
              />
            </NodeIndexContext>
          );
        })}
        {cardReorder.showIndicatorAt(nodes.length, null) && (
          <div className={styles.dropIndicator} />
        )}

        {nodes.length === 0 && parseErrors.length === 0 && (
          <div className={styles.empty}>
            <span>No template nodes</span>
            <span>Use the buttons below or drag a template from the browser</span>
          </div>
        )}

        <div className={styles.addNodeArea}>
          {(["Patch", "Clone"] as const).map((kind) => (
            <div
              key={kind}
              className={`${styles.addNodeZone} ${bottomDragOver === kind ? styles.addNodeZoneDragOver : ""} ${bottomDragOver === "reject" ? styles.addNodeZoneDragReject : ""}`}
              onDragOver={(e) => {
                const types = e.dataTransfer.types;
                if (types.includes(INSTANCE_DRAG_TAG)) {
                  e.preventDefault();
                  e.dataTransfer.dropEffect = "copy";
                  setBottomDragOver(kind);
                } else if (types.includes(MEMBER_DRAG_TAG)) {
                  setBottomDragOver("reject");
                } else if (types.includes("text/plain")) {
                  e.preventDefault();
                  e.dataTransfer.dropEffect = "copy";
                  setBottomDragOver(kind);
                }
              }}
              onDragLeave={() => setBottomDragOver(false)}
              onDrop={(e) => handleBottomDrop(kind, e)}
            >
              <button
                type="button"
                className={styles.addNodeBtn}
                onClick={() => handleAddNode(kind)}
              >
                <Plus size={12} />
                Add {kind}
              </button>
            </div>
          ))}
        </div>
      </div>
    </EditorDispatchContext>
  );
}
