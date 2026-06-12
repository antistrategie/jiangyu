import React, { useCallback, useEffect, useMemo, useRef, useState } from "react";
import { Plus } from "lucide-react";
import type { EditorError, EditorNodeKind } from "./types";
import { parseCrossMemberPayload } from "@features/templates/crossMember";
import {
  parseCrossInstancePayload,
  INSTANCE_DRAG_TAG,
  MEMBER_DRAG_TAG,
} from "@features/templates/crossInstance";
import { useToastStore } from "@shared/toast";
import styles from "./TemplateVisualEditor.module.css";
import { stripUiIds, type StampedNode } from "./helpers";
import { CARD_REORDER_MIME, useDragReorder } from "./hooks";
import {
  CompositeCollapseContext,
  ConversationSourceContext,
  EditorDispatchContext,
  EditorScrollContainerContext,
  NodeIndexContext,
  editorReducer,
  pushUndoFrame,
  undoCoalesceKey,
  type CompositeCollapseControl,
  type EditorAction,
} from "./store";
import { uiId, stampNodes } from "./shared/uiId";
import {
  templatesParse,
  templatesSerialise,
  invalidateProjectClonesCache,
} from "./shared/rpcHelpers";
import { NodeCard } from "./cards/NodeCard";
import {
  computeCompositeKeyByUiId,
  computeNodeKeyByUiId,
  loadCollapsed,
  loadCompositeCollapse,
  pruneCollapsed,
  pruneCompositeCollapse,
  saveCollapsed,
  saveCompositeCollapse,
} from "./collapseStorage";

// --- Main component ---

interface TemplateVisualEditorProps {
  readonly content: string;
  readonly filePath?: string | null | undefined;
  readonly onChange: (content: string) => void;
  readonly onRequestSourceMode?: (() => void) | undefined;
}

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
  // Collapse state holds stable structural keys (kind+templateType+id, with
  // an occurrence index for duplicates) — never `_uiId`, which regenerates
  // on every parse. Loaded from localStorage on file change; persisted on
  // every mutation so state survives tab switches, file reopens, and app
  // restarts.
  const [collapsed, setCollapsed] = useState<Set<string>>(() => loadCollapsed(cacheKey));
  const lastSerialisedRef = useRef<string>("");
  const serialiseVersionRef = useRef(0);

  // Undo/redo history. undoMetaRef carries the coalescing state: the key
  // and timestamp of the last undo push, so a per-keystroke dispatch burst
  // against the same field collapses into one frame (see pushUndoFrame).
  const undoStackRef = useRef<StampedNode[][]>([]);
  const redoStackRef = useRef<StampedNode[][]>([]);
  const undoMetaRef = useRef<{ key: string | null; time: number }>({ key: null, time: 0 });
  // Mirror nodes into a ref so callbacks read the latest value without
  // stale closures. Written synchronously in dispatch (every nodes change
  // flows through it) so back-to-back dispatches in one tick compose.
  const nodesRef = useRef<StampedNode[]>(nodes);

  // Latest-value mirror of the onChange prop. Call sites pass fresh inline
  // arrows, so closing over the prop directly would re-mint
  // scheduleSerialise / dispatch (and with them the dispatch context value)
  // after every parent re-render.
  const onChangeRef = useRef(onChange);
  useEffect(() => {
    onChangeRef.current = onChange;
  }, [onChange]);

  // Latest-value mirrors of the collapse maps and cache key, so the
  // collapse callbacks and the composite-collapse control below can stay
  // identity-stable for the component's lifetime. Synced after every
  // commit; the uiId→key maps are additionally written synchronously in
  // dispatch so children mounting in the same commit as a nodes change
  // (e.g. a parse reload) resolve against the fresh maps.
  const keyByUiIdRef = useRef<Map<string, string>>(new Map());
  const compositeKeyByUiIdRef = useRef<Map<string, string>>(new Map());
  const collapsedRef = useRef(collapsed);
  const compositeCollapseRef = useRef<Map<string, boolean>>(new Map());
  const cacheKeyRef = useRef(cacheKey);

  // Debounced serialise of `updated` back to KDL text. Side-effect-only;
  // run after each mutation to push the new content out via onChange and
  // refresh parse-error diagnostics.
  const serialiseTimerRef = useRef<ReturnType<typeof setTimeout> | undefined>(undefined);

  const scheduleSerialise = useCallback((updated: StampedNode[]) => {
    const version = ++serialiseVersionRef.current;
    clearTimeout(serialiseTimerRef.current);
    serialiseTimerRef.current = setTimeout(() => {
      const doc = { nodes: updated, errors: [] as EditorError[] };
      void templatesSerialise(stripUiIds(doc)).then(async (result) => {
        if (version !== serialiseVersionRef.current) return;
        lastSerialisedRef.current = result.text;
        onChangeRef.current(result.text);
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
  }, []);

  // The dispatch handle exposed via context. Wraps the pure reducer with
  // undo-stack bookkeeping and the serialise side effect. Identity-stable
  // for the component's lifetime so the context never re-renders consumers.
  const dispatch = useCallback<React.Dispatch<EditorAction>>(
    (action) => {
      const next = editorReducer(nodesRef.current, action);
      if (action.type === "load") {
        // Loads come from parse / undo / redo; the next edit must open a
        // fresh undo frame rather than coalesce across the boundary.
        undoMetaRef.current = { key: null, time: 0 };
      } else {
        const key = undoCoalesceKey(action);
        const now = Date.now();
        undoStackRef.current = pushUndoFrame(undoStackRef.current, nodesRef.current, {
          key,
          now,
          lastKey: undoMetaRef.current.key,
          lastTime: undoMetaRef.current.time,
        });
        undoMetaRef.current = { key, time: now };
        redoStackRef.current = [];
        invalidateProjectClonesCache();
        scheduleSerialise(next);
      }
      nodesRef.current = next;
      // Refresh the stable-key maps alongside the nodes so children that
      // mount in the resulting commit (CompositeEditor seeding its collapse
      // state) resolve against the new tree, not last commit's.
      const nodeKeys = computeNodeKeyByUiId(next);
      keyByUiIdRef.current = nodeKeys;
      compositeKeyByUiIdRef.current = computeCompositeKeyByUiId(next, nodeKeys);
      setNodes(next);
    },
    [scheduleSerialise],
  );

  // Parse via RPC when content changes externally.
  // The bail-out compares against the last parsed-or-serialised content so
  // our own serialise round-trip doesn't trigger a redundant re-parse +
  // load dispatch — which would mint fresh `_uiId`s and remount every node
  // card under the editor.
  useEffect(() => {
    if (content === lastSerialisedRef.current) return;

    void templatesParse(content)
      .then((doc) => {
        lastSerialisedRef.current = content;
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

  // Scroll-container ref handed to descendant virtualisers via
  // EditorScrollContainerContext. DirectiveBody uses it as TanStack
  // Virtual's getScrollElement so the outer .root's scroll position
  // drives row mounting for huge clones (voicelines.kdl: 374 directives).
  // Doubles as the editor's root element for scoping the undo handler.
  const scrollContainerRef = useRef<HTMLDivElement>(null);

  // Undo/redo keyboard handler. Scoped to this editor instance: with
  // several editors mounted (split panes) an unscoped handler would pop
  // every stack at once, and it would steal Ctrl+Z from inputs the editor
  // doesn't own (e.g. a browser pane's search box).
  useEffect(() => {
    const handler = (e: KeyboardEvent) => {
      const isMod = e.metaKey || e.ctrlKey;
      if (!isMod || e.key.toLowerCase() !== "z") return;
      const root = scrollContainerRef.current;
      const el = document.activeElement;
      if (!root || !el || !root.contains(el)) return;
      e.preventDefault();

      // Editor-owned input with uncommitted text (CommitInput is
      // uncontrolled; its defaultValue is the last committed value):
      // revert the draft instead of popping a frame. Controlled inputs
      // keep value and defaultValue in sync, so they fall through.
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

  // Stable structural key per node, recomputed each render. Toggle handlers
  // and per-card lookups translate uiId -> stableKey via this map.
  const keyByUiId = useMemo(() => computeNodeKeyByUiId(nodes), [nodes]);
  // Stable composite key map: walks every composite-bearing directive in
  // the tree and assigns a positional path key. Lifted here so the entire
  // editor shares one map; CompositeEditor reads from context via
  // directive._uiId rather than recomputing keys per render.
  const compositeKeyByUiId = useMemo(
    () => computeCompositeKeyByUiId(nodes, keyByUiId),
    [nodes, keyByUiId],
  );

  // Persisted composite collapse map. Map<stableKey, explicitState>
  // because the default state depends on content (collapsed when
  // populated, expanded when empty); a presence-only set would conflate
  // "default" with "explicitly expanded".
  const [compositeCollapse, setCompositeCollapse] = useState<Map<string, boolean>>(() =>
    loadCompositeCollapse(cacheKey),
  );

  // When the active file changes (tab switch, file open), reload collapse
  // state from localStorage. React's recommended pattern for adjusting state
  // on prop change: track the previous prop in state and reset during render
  // so the same render uses the new value (no cascading effect).
  const [prevCacheKey, setPrevCacheKey] = useState(cacheKey);
  if (prevCacheKey !== cacheKey) {
    setPrevCacheKey(cacheKey);
    setCollapsed(loadCollapsed(cacheKey));
    setCompositeCollapse(loadCompositeCollapse(cacheKey));
  }

  // Keep the latest-value mirrors current after every commit. The collapse
  // callbacks and composite control only fire from event handlers, which
  // run after this effect, so they always see the values just rendered.
  useEffect(() => {
    keyByUiIdRef.current = keyByUiId;
    compositeKeyByUiIdRef.current = compositeKeyByUiId;
    collapsedRef.current = collapsed;
    compositeCollapseRef.current = compositeCollapse;
    cacheKeyRef.current = cacheKey;
  });

  // Intersect a candidate set with the current nodes' stable keys before
  // persisting, so stale entries (from renamed nodes) silently drop out the
  // next time the user toggles/expands/collapses. Keeps the persisted state
  // clean without a separate prune pass. Reads through the refs so the
  // callbacks below stay identity-stable for the component's lifetime —
  // a fresh onToggleCollapse per render would defeat memo(NodeCard) for
  // every card on every dispatch.
  const persistCollapsed = useCallback((next: Set<string>) => {
    const cleaned = pruneCollapsed(next, keyByUiIdRef.current.values());
    saveCollapsed(cacheKeyRef.current, cleaned);
    collapsedRef.current = cleaned;
    setCollapsed(cleaned);
  }, []);

  // Composite-collapse control surface, threaded to CompositeEditor via
  // context. Identity-stable so the context never re-renders consumers;
  // each CompositeEditor seeds its own render state from resolveState at
  // mount and re-renders itself on toggle. Toggle materialises the
  // explicit state into the persisted map (true=collapsed, false=expanded)
  // with the same prune-on-persist pattern as node collapse, and writes
  // the ref synchronously so composites mounting before the next commit's
  // ref sync still read the recorded state.
  const compositeCollapseControl = useMemo<CompositeCollapseControl>(
    () => ({
      resolveState: (uiId) => {
        const key = compositeKeyByUiIdRef.current.get(uiId);
        if (key === undefined) return undefined;
        return compositeCollapseRef.current.get(key);
      },
      toggle: (uiId, nextState) => {
        const key = compositeKeyByUiIdRef.current.get(uiId);
        if (key === undefined) return;
        const next = new Map(compositeCollapseRef.current);
        next.set(key, nextState);
        const cleaned = pruneCompositeCollapse(next, compositeKeyByUiIdRef.current.values());
        saveCompositeCollapse(cacheKeyRef.current, cleaned);
        compositeCollapseRef.current = cleaned;
        setCompositeCollapse(cleaned);
      },
    }),
    [],
  );

  const handleToggleCollapse = useCallback(
    (nodeUiId: string) => {
      const stableKey = keyByUiIdRef.current.get(nodeUiId);
      if (stableKey === undefined) return;
      const next = new Set(collapsedRef.current);
      if (next.has(stableKey)) next.delete(stableKey);
      else next.add(stableKey);
      persistCollapsed(next);
    },
    [persistCollapsed],
  );

  const handleExpandAll = useCallback(() => {
    persistCollapsed(new Set());
  }, [persistCollapsed]);

  const handleCollapseAll = useCallback(() => {
    persistCollapsed(new Set(keyByUiIdRef.current.values()));
  }, [persistCollapsed]);

  const handleAddNode = useCallback(
    (kind: EditorNodeKind) => {
      dispatch({
        type: "addNode",
        node: { kind, templateType: "", directives: [], _uiId: uiId() },
      });
    },
    [dispatch],
  );

  const cardReorder = useDragReorder(
    (fromId, toSlot) => dispatch({ type: "reorderCards", fromId, toSlot }),
    CARD_REORDER_MIME,
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
      <EditorScrollContainerContext value={scrollContainerRef}>
        <CompositeCollapseContext value={compositeCollapseControl}>
          <div className={styles.root} ref={scrollContainerRef}>
            {rpcError && (
              <div className={styles.parseError}>
                <span className={styles.errorSummary}>RPC error: {rpcError}</span>
                {onRequestSourceMode && (
                  <button
                    type="button"
                    className={styles.fallbackBtn}
                    onClick={onRequestSourceMode}
                  >
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
                    <button
                      type="button"
                      className={styles.fallbackBtn}
                      onClick={onRequestSourceMode}
                    >
                      Switch to Source
                    </button>
                  )}
                </div>
                <div className={styles.errorList}>
                  {parseErrors.map((err) => (
                    <div key={`${err.line ?? "?"}:${err.message}`} className={styles.errorItem}>
                      {err.line != null && (
                        <span className={styles.errorLine}>line {err.line}</span>
                      )}
                      <span className={styles.errorMessage}>{err.message}</span>
                    </div>
                  ))}
                </div>
              </div>
            )}

            {nodes.length > 1 && (
              <div className={styles.editorToolbar}>
                <button
                  type="button"
                  className={styles.toolbarBtn}
                  onClick={handleExpandAll}
                  disabled={nodes.every((n) => !collapsed.has(keyByUiId.get(n._uiId) ?? ""))}
                >
                  Reverse collapse
                </button>
                <button
                  type="button"
                  className={styles.toolbarBtn}
                  onClick={handleCollapseAll}
                  disabled={nodes.every((n) => collapsed.has(keyByUiId.get(n._uiId) ?? ""))}
                >
                  Collapse all
                </button>
              </div>
            )}

            {nodes.map((node, ni) => {
              const handlers = cardReorder.buildHandlers(node._uiId, ni, ni + 1);
              // ConversationTemplate source for the role combobox: clones
              // carry the from= source; patches operate on the live
              // template, so the template id IS the source. Null for any
              // non-conversation card so RoleGuid editors fall back to
              // free text.
              const conversationSource: string | null =
                node.templateType === "ConversationTemplate"
                  ? node.kind === "Clone"
                    ? (node.sourceId ?? null)
                    : (node.templateId ?? null)
                  : null;
              return (
                <NodeIndexContext key={node._uiId} value={ni}>
                  <ConversationSourceContext value={conversationSource}>
                    {cardReorder.showIndicatorAt(ni, node._uiId) && (
                      <div className={styles.dropIndicator} />
                    )}
                    <NodeCard
                      node={node}
                      collapsed={collapsed.has(keyByUiId.get(node._uiId) ?? "")}
                      onToggleCollapse={handleToggleCollapse}
                      isDragging={handlers.isDragging}
                      onDragStart={handlers.onDragStart}
                      onDragEnd={handlers.onDragEnd}
                      onDragOverCard={handlers.onDragOver}
                      onDropCard={handlers.onDrop}
                    />
                  </ConversationSourceContext>
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
              {/* Create makes a fresh template with no source, so it is a
                  button only — there is no instance to drag onto it. */}
              <div className={styles.addNodeZone}>
                <button
                  type="button"
                  className={styles.addNodeBtn}
                  onClick={() => handleAddNode("Create")}
                >
                  <Plus size={12} />
                  Add Create
                </button>
              </div>
            </div>
          </div>
        </CompositeCollapseContext>
      </EditorScrollContainerContext>
    </EditorDispatchContext>
  );
}
