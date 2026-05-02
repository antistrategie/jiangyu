import React, { useCallback, useEffect, useMemo, useRef, useState } from "react";
import { ChevronDown, GripVertical, Plus, X } from "lucide-react";
import { useVirtualizer } from "@tanstack/react-virtual";
import type { CrossMemberPayload } from "@lib/drag/crossMember";
import type {
  EditorNode,
  DirectiveOp,
  EditorValue,
  EditorValueKind,
  EditorDocument,
  EditorError,
} from "./types";
import type {
  EnumMemberEntry,
  EnumMembersResult,
  InspectedFieldNode,
  TemplateMember,
  TemplateSearchResult,
  TemplateValueResult,
} from "@lib/rpc";
import { parseCrossMemberPayload } from "@lib/drag/crossMember";
import {
  parseCrossInstancePayload,
  INSTANCE_DRAG_TAG,
  MEMBER_DRAG_TAG,
  getActiveTemplateDrag,
} from "@lib/drag/crossInstance";
import { useToastStore } from "@lib/toast";
import { rpcCall } from "@lib/rpc";
import { onKeyActivate } from "@lib/ui/a11y";
import styles from "./TemplateVisualEditor.module.css";
import {
  allowsMultipleDirectives,
  buildDescentMemberDirective,
  groupDirectives,
  insertAtPendingAnchor,
  inspectedFieldToEditorValue,
  isFieldBagValue,
  makeDefaultValue,
  resolveEnumCommitType,
  resolveRefTypeDisplay,
  rewriteDescentSlotIndex,
  shouldShowRefTypeSelector,
  stampNodes as stampNodesHelper,
  stripUiIds,
  type PendingAnchor,
  type StampedDirective,
  type StampedNode,
} from "./helpers";
import { useDragReorder, useTemplateMembers } from "./hooks";
import {
  EditorDispatchContext,
  NodeIndexContext,
  editorReducer,
  useEditorDispatch,
  useNodeIndex,
  type EditorAction,
} from "./store";

// --- Stable UI IDs ---
// Module-state counter for the editor's stamping helpers. Tests don't go
// through this; they pass their own deterministic generator into
// stampDirective / stampNodes from helpers.

let _nextId = 0;
function uiId(): string {
  return `_ui_${++_nextId}`;
}

const stampNodes = (nodes: EditorNode[]): StampedNode[] => stampNodesHelper(nodes, uiId);

// --- CommitInput ---
// Uncontrolled input that preserves native browser undo. Commits value to
// React on blur or Enter. Re-keys on external value changes so the DOM
// input resets to the authoritative value.

interface CommitInputProps extends Omit<
  React.InputHTMLAttributes<HTMLInputElement>,
  "onChange" | "defaultValue"
> {
  readonly value: string | number;
  readonly onCommit: (value: string) => void;
}

function CommitInput({ value, onCommit, onKeyDown, ...rest }: CommitInputProps) {
  const ref = useRef<HTMLInputElement>(null);
  return (
    <input
      key={String(value)}
      {...rest}
      ref={ref}
      defaultValue={value}
      onBlur={(e) => {
        if (e.target.value !== String(value)) onCommit(e.target.value);
      }}
      onKeyDown={(e) => {
        if (e.key === "Enter") {
          e.currentTarget.blur();
        }
        onKeyDown?.(e);
      }}
    />
  );
}

// --- RPC helpers ---

function templatesSearch(className?: string): Promise<TemplateSearchResult> {
  return rpcCall<TemplateSearchResult>("templatesSearch", className ? { className } : undefined);
}

function templatesEnumMembers(typeName: string): Promise<EnumMembersResult> {
  return rpcCall<EnumMembersResult>("templatesEnumMembers", { typeName });
}

// Simple in-memory cache for enum members and template type lists to avoid repeated RPCs
const enumMembersCache = new Map<string, readonly EnumMemberEntry[]>();
const templateTypesCache = { types: null as readonly string[] | null };

// Per-(typeName, id) Promise cache so multiple cards targeting the same vanilla
// template share one RPC and stay consistent. Lifetime is the editor session;
// rebuilding the index requires reopening the editor to pick up new values.
const templateValuesCache = new Map<string, Promise<TemplateValueResult>>();

// U+0000 separator can't appear in either a C# identifier (so the class
// name is safe) or a Unity template id, so this composite key uniquely
// encodes the (typeName, id) tuple without collision risk between sites.
function vanillaCacheKey(typeName: string, id: string): string {
  return `${typeName}\u0000${id}`;
}

function templatesValue(typeName: string, id: string): Promise<TemplateValueResult> {
  const key = vanillaCacheKey(typeName, id);
  let cached = templateValuesCache.get(key);
  if (!cached) {
    cached = rpcCall<TemplateValueResult>("templatesValue", { typeName, id });
    templateValuesCache.set(key, cached);
  }
  return cached;
}

// Pure helpers (vanilla pre-fill, kind predicates, default-value factories,
// etc.) live in `helpers.ts` so this JSX module only exports React
// components — keeps Vite fast-refresh happy and gives the unit tests a
// stable, side-effect-free import surface.

// Empty lookup returned when no target is selected or the RPC hasn't
// resolved yet. Module-level constant so consumers' useMemo dependency
// arrays stay stable on the empty case.
const EMPTY_VANILLA_FIELDS: ReadonlyMap<string, InspectedFieldNode> = new Map();

// Hook: fetches vanilla field values for the (typeName, id) target and returns
// a name → InspectedFieldNode lookup map. Empty until the RPC resolves; falls
// back to empty on failure (callers use neutral defaults). Tagged by lookup
// key so a stale resolution from a previous (typeName, id) doesn't surface
// after the inputs change; the empty fallback is returned until the new
// effect resolves.
function useVanillaFields(
  typeName: string | undefined,
  id: string | undefined,
): ReadonlyMap<string, InspectedFieldNode> {
  const [resolved, setResolved] = useState<{
    key: string;
    map: Map<string, InspectedFieldNode>;
  } | null>(null);

  useEffect(() => {
    if (!typeName || !id) return;
    const key = vanillaCacheKey(typeName, id);
    let cancelled = false;
    void templatesValue(typeName, id)
      .then((result) => {
        if (cancelled) return;
        const map = new Map<string, InspectedFieldNode>();
        for (const f of result.fields) {
          if (f.name) map.set(f.name, f);
        }
        setResolved({ key, map });
      })
      .catch(() => {
        if (!cancelled) setResolved({ key, map: new Map() });
      });
    return () => {
      cancelled = true;
    };
  }, [typeName, id]);

  if (!typeName || !id) return EMPTY_VANILLA_FIELDS;
  const expectedKey = vanillaCacheKey(typeName, id);
  return resolved?.key === expectedKey ? resolved.map : EMPTY_VANILLA_FIELDS;
}

interface ProjectCloneEntry {
  readonly templateType: string;
  readonly id: string;
  readonly file: string;
}

let projectClonesCache: readonly ProjectCloneEntry[] | null = null;

function templatesProjectClones(): Promise<{ clones: readonly ProjectCloneEntry[] }> {
  return rpcCall<{ clones: readonly ProjectCloneEntry[] }>("templatesProjectClones");
}

async function getCachedProjectClones(): Promise<readonly ProjectCloneEntry[]> {
  if (projectClonesCache) return projectClonesCache;
  const result = await templatesProjectClones();
  projectClonesCache = result.clones;
  return result.clones;
}

function invalidateProjectClonesCache() {
  projectClonesCache = null;
}

async function getCachedEnumEntries(typeName: string): Promise<readonly EnumMemberEntry[]> {
  const cached = enumMembersCache.get(typeName);
  if (cached) return cached;
  const result = await templatesEnumMembers(typeName);
  enumMembersCache.set(typeName, result.members);
  return result.members;
}

async function getCachedEnumMembers(typeName: string): Promise<readonly string[]> {
  const entries = await getCachedEnumEntries(typeName);
  return entries.map((e) => e.name);
}

async function getCachedTemplateTypes(): Promise<readonly string[]> {
  if (templateTypesCache.types) return templateTypesCache.types;
  const result = await templatesSearch();
  const types = [...new Set(result.instances.map((i) => i.className))].sort();
  templateTypesCache.types = types;
  return types;
}

function templatesParse(text: string): Promise<EditorDocument> {
  return rpcCall<EditorDocument>("templatesParse", { text });
}

function templatesSerialise(document: EditorDocument): Promise<{ text: string }> {
  return rpcCall<{ text: string }>("templatesSerialise", document);
}

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
        const doc: EditorDocument = { nodes: updated, errors: [] };
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
  // undo-stack bookkeeping and the serialise side effect: every mutation
  // pushes a snapshot onto the undo stack, clears redo, invalidates the
  // project-clones cache, and schedules a debounced text emit. Bypasses
  // all of that for `load` actions (parse / undo / redo paths handle
  // their own history management).
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
        // External change resets undo history.
        undoStackRef.current = [];
        redoStackRef.current = [];
      })
      .catch((err: unknown) => {
        setRpcError(err instanceof Error ? err.message : "Parse RPC failed");
      });
  }, [content, dispatch]);

  // Undo/redo keyboard handler. Pulls a snapshot from the appropriate
  // stack and dispatches a `load` action; pushes the current state onto
  // the inverse stack so the action is reversible. Schedules its own
  // serialise since the dispatch wrapper skips serialise on `load`.
  useEffect(() => {
    const handler = (e: KeyboardEvent) => {
      const isMod = e.metaKey || e.ctrlKey;
      if (!isMod || e.key.toLowerCase() !== "z") return;
      e.preventDefault();

      // If a focused input has uncommitted edits, discard them first.
      // Structural undo fires on the next Ctrl+Z when the input is clean.
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

  // Bottom instance drop (on the add-node area). "accept" = instance drag,
  // "reject" = member drag (wrong kind), false = no drag.
  // Per-button drag state: which button (if any) is currently the active
  // drop target, or "reject" when the dragged kind doesn't fit either button.
  const [bottomDragOver, setBottomDragOver] = useState<"Patch" | "Clone" | "reject" | false>(false);

  const handleBottomDrop = useCallback(
    (kind: "Patch" | "Clone", e: React.DragEvent) => {
      setBottomDragOver(false);
      const raw = e.dataTransfer.getData("text/plain");
      const inst = parseCrossInstancePayload(raw);
      if (!inst) {
        // Cross-window fallback accepted any text/plain; if it wasn't an
        // instance payload (e.g. a field drag), surface the mismatch.
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
                  // Member drags don't make sense as new nodes — show reject
                  // styling and don't preventDefault so onDrop never fires.
                  setBottomDragOver("reject");
                } else if (types.includes("text/plain")) {
                  // Cross-window fallback — kind tag doesn't cross WebKitGTK.
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

// --- NodeCard ---

interface NodeCardProps {
  node: StampedNode;
  collapsed: boolean;
  onToggleCollapse: () => void;
  isDragging: boolean;
  onDragStart: () => void;
  onDragEnd: () => void;
  onDragOverCard: (e: React.DragEvent) => void;
  onDropCard: () => void;
}

function NodeCard({
  node,
  collapsed,
  onToggleCollapse,
  isDragging,
  onDragStart,
  onDragEnd,
  onDragOverCard,
  onDropCard,
}: NodeCardProps) {
  const dispatch = useEditorDispatch();
  const nodeIndex = useNodeIndex();
  const isPatch = node.kind === "Patch";
  const justDraggedRef = useRef(false);
  const { members, loaded: membersLoaded } = useTemplateMembers(node.templateType, !collapsed);

  // Vanilla values for pre-filling newly-added directives. Patch targets the
  // node's templateId; Clone targets the source the new clone copies from.
  // Lookup is empty until the RPC resolves; consumers fall back to neutral
  // defaults in that window.
  const vanillaTargetId = isPatch ? node.templateId : node.sourceId;
  const vanillaFields = useVanillaFields(node.templateType, vanillaTargetId);

  const memberMap = new Map(members.map((m) => [m.name, m]));

  // Card-level drop: accept member drags to add set directives. Drop-time
  // checks cover the cross-window case where the dragOver-time context isn't
  // available; same-window drops already got rejected visually by the
  // FieldAdder's own dragOver gate.
  const handleNodeDrop = useCallback(
    (e: React.DragEvent) => {
      const raw = e.dataTransfer.getData("text/plain");
      const member = parseCrossMemberPayload(raw);
      if (!member) return;
      e.preventDefault();
      const toast = useToastStore.getState().push;
      if (node.templateType !== "" && member.templateType !== node.templateType) {
        toast({
          variant: "error",
          message: `Field does not belong to ${node.templateType}`,
          detail: `"${member.fieldPath}" belongs to ${member.templateType}`,
        });
        return;
      }
      if (
        !allowsMultipleDirectives(member) &&
        node.directives.some((d) => d.fieldPath === member.fieldPath)
      ) {
        toast({
          variant: "info",
          message: `"${member.fieldPath}" is already on this node`,
        });
        return;
      }
      const synthMember = synthMemberFromPayload(member);
      const vanilla = vanillaFields.get(member.fieldPath);
      dispatch({
        type: "addDirective",
        nodeIndex,
        directive: makeDefaultDirective(synthMember, vanilla),
      });
    },
    [node, vanillaFields, dispatch, nodeIndex],
  );

  const fetchInstances = useCallback(async (): Promise<readonly SuggestionItem[]> => {
    if (!node.templateType) return [];
    const [searchResult, projectClones] = await Promise.all([
      templatesSearch(node.templateType),
      getCachedProjectClones(),
    ]);
    const gameItems: SuggestionItem[] = searchResult.instances.map((i) => ({ label: i.name }));
    const gameLabels = new Set(gameItems.map((i) => i.label));
    const cloneItems: SuggestionItem[] = projectClones
      .filter((c) => c.templateType === node.templateType && !gameLabels.has(c.id))
      .map((c) => ({ label: c.id, tag: "clone" }));
    return [...cloneItems, ...gameItems];
  }, [node.templateType]);

  return (
    <div
      className={`${styles.card} ${isDragging ? styles.cardDragging : ""}`}
      role="presentation"
      onDragOver={onDragOverCard}
      onDrop={(e) => {
        e.preventDefault();
        onDropCard();
      }}
    >
      <div
        className={styles.cardHeader}
        role="button"
        tabIndex={0}
        aria-expanded={!collapsed}
        onClick={() => {
          if (!justDraggedRef.current) onToggleCollapse();
          justDraggedRef.current = false;
        }}
        onKeyDown={onKeyActivate(() => {
          onToggleCollapse();
        })}
      >
        <span
          className={styles.dragGrip}
          role="presentation"
          draggable
          onClick={(e) => e.stopPropagation()}
          onPointerDown={(e) => e.stopPropagation()}
          onDragStart={(e) => {
            e.stopPropagation();
            justDraggedRef.current = true;
            e.dataTransfer.effectAllowed = "move";
            e.dataTransfer.setData("application/x-jiangyu-card-reorder", node._uiId);
            onDragStart();
          }}
          onDragEnd={onDragEnd}
          title="Drag to reorder"
        >
          <GripVertical size={12} />
        </span>
        <span
          className={`${styles.cardBadge} ${isPatch ? styles.cardBadgePatch : styles.cardBadgeClone}`}
        >
          {isPatch ? "patch" : "clone"}
        </span>
        <SuggestionCombobox
          value={node.templateType}
          placeholder="Type"
          fetchSuggestions={getCachedTemplateTypes}
          onChange={(t) =>
            dispatch({ type: "updateNode", nodeIndex, node: { ...node, templateType: t } })
          }
          className={styles.cardTypeInput}
        />
        {isPatch ? (
          <SuggestionCombobox
            value={node.templateId ?? ""}
            placeholder="ID"
            fetchSuggestions={fetchInstances}
            onChange={(v) =>
              dispatch({ type: "updateNode", nodeIndex, node: { ...node, templateId: v } })
            }
            className={styles.cardIdInput}
          />
        ) : (
          <>
            <span className={styles.cardProp}>from</span>
            <SuggestionCombobox
              value={node.sourceId ?? ""}
              placeholder="Source ID"
              fetchSuggestions={fetchInstances}
              onChange={(v) =>
                dispatch({ type: "updateNode", nodeIndex, node: { ...node, sourceId: v } })
              }
              className={styles.cardIdInput}
            />
            <span className={styles.cardProp}>id</span>
            <div
              className={styles.cardIdInput}
              role="presentation"
              onClick={(e) => e.stopPropagation()}
            >
              <CommitInput
                type="text"
                className={styles.setValueInput}
                value={node.cloneId ?? ""}
                placeholder="Clone ID"
                onCommit={(v) =>
                  dispatch({ type: "updateNode", nodeIndex, node: { ...node, cloneId: v } })
                }
              />
            </div>
          </>
        )}
        <span
          className={`${styles.cardExpander} ${collapsed ? "" : styles.cardExpanderOpen}`}
          aria-hidden
        >
          <ChevronDown size={14} />
        </span>
        <button
          type="button"
          className={styles.cardDelete}
          onClick={(e) => {
            e.stopPropagation();
            dispatch({ type: "deleteNode", nodeIndex });
          }}
          title="Remove node"
        >
          <X size={14} />
        </button>
      </div>

      {!collapsed && (
        <div className={styles.cardBody}>
          <DirectiveBody
            node={node}
            members={members}
            membersLoaded={membersLoaded}
            memberMap={memberMap}
            vanillaFields={vanillaFields}
            handleNodeDrop={handleNodeDrop}
          />
        </div>
      )}
    </div>
  );
}

// --- DirectiveRow ---
//
// Renders one stamped directive as a SetRow with its drag indicator and
// reorder handlers wired through `reorder`. Used by DirectiveBody (loose
// rows + group members), CompositeEditor (composite body rows), and any
// future directive-list site so the rendering and drag wiring stays in
// one place. Caller threads onChange/onDelete in by flatIndex so the
// owning list can splice in / out at the right slot.

interface DirectiveRowProps {
  readonly directive: StampedDirective;
  readonly flatIndex: number;
  /** Display-only override for the field-name label; lets descent groups
   *  show the inner suffix (e.g. "ShowHUDText") instead of the underlying
   *  flat fieldPath ("EventHandlers[0].ShowHUDText"). The wire format is
   *  unchanged. */
  readonly displayFieldPath?: string | undefined;
  readonly memberMap: Map<string, TemplateMember>;
  readonly vanillaFields: ReadonlyMap<string, InspectedFieldNode>;
  readonly reorder: ReturnType<typeof useDragReorder>;
  readonly onChange: (flatIndex: number, directive: StampedDirective) => void;
  readonly onDelete: (flatIndex: number) => void;
}

function DirectiveRow({
  directive,
  flatIndex,
  displayFieldPath,
  memberMap,
  vanillaFields,
  reorder,
  onChange,
  onDelete,
}: DirectiveRowProps) {
  // The directive's fieldPath is the inner-relative member name (descent
  // context lives on directive.descent, not in the path). Take the first
  // dotted segment so deeper composite-member writes like "Sub.Field" still
  // resolve their top-level member from the parent's catalog.
  const baseName = (displayFieldPath ?? directive.fieldPath).split(".")[0] ?? "";
  const handlers = reorder.buildHandlers(directive._uiId, flatIndex, flatIndex + 1);
  return (
    <>
      {reorder.showIndicatorAt(flatIndex, directive._uiId) && (
        <div className={styles.dropIndicator} />
      )}
      <SetRow
        directive={directive}
        member={memberMap.get(baseName)}
        vanillaNode={vanillaFields.get(baseName)}
        displayFieldPath={displayFieldPath}
        onChange={(updated) => onChange(flatIndex, updated)}
        onDelete={() => onDelete(flatIndex)}
        isDragging={handlers.isDragging}
        onDragStart={handlers.onDragStart}
        onDragEnd={handlers.onDragEnd}
        onDragOverRow={handlers.onDragOver}
        onDropRow={handlers.onDrop}
      />
    </>
  );
}

// --- DirectiveBody ---
//
// Renders a node's directive list with descent grouping. Walks
// `groupDirectives(node.directives)` and emits a SetRow per loose entry or
// a DescentGroup per consecutive descent run. The group component owns the
// inner FieldAdder and member-edit operations; member directives still live
// in the flat node.directives list, so reorder, parse / serialise round-
// trip, and validation see them exactly as before.

interface DirectiveBodyProps {
  node: StampedNode;
  members: readonly TemplateMember[];
  membersLoaded: boolean;
  memberMap: Map<string, TemplateMember>;
  vanillaFields: ReadonlyMap<string, InspectedFieldNode>;
  handleNodeDrop: (e: React.DragEvent) => void;
}

function DirectiveBody({
  node,
  members,
  membersLoaded,
  memberMap,
  vanillaFields,
  handleNodeDrop,
}: DirectiveBodyProps) {
  const dispatch = useEditorDispatch();
  const nodeIndex = useNodeIndex();
  const reorder = useDragReorder((fromId, toSlot) =>
    dispatch({ type: "reorderRows", nodeIndex, fromId, toSlot }),
  );
  const onSetDirectives = useCallback(
    (directives: StampedDirective[]) => dispatch({ type: "setDirectives", nodeIndex, directives }),
    [dispatch, nodeIndex],
  );
  const onUpdateDirective = useCallback(
    (dirIndex: number, directive: StampedDirective) =>
      dispatch({ type: "updateDirective", nodeIndex, dirIndex, directive }),
    [dispatch, nodeIndex],
  );
  const onDeleteDirective = useCallback(
    (dirIndex: number) => dispatch({ type: "deleteDirective", nodeIndex, dirIndex }),
    [dispatch, nodeIndex],
  );
  const onAddDirective = useCallback(
    (directive: StampedDirective) => dispatch({ type: "addDirective", nodeIndex, directive }),
    [dispatch, nodeIndex],
  );
  const groups = useMemo(() => groupDirectives(node.directives), [node.directives]);
  // Pre-compute each rendered item's flat-index range so reorder and
  // group operations can splice the right slice of node.directives. Done
  // up-front rather than via a let counter inside .map() so the render
  // stays a pure function.
  const groupStartIndices = useMemo(() => {
    const starts: number[] = [];
    let cursor = 0;
    for (const g of groups) {
      starts.push(cursor);
      cursor += g.kind === "loose" ? 1 : g.members.length;
    }
    return starts;
  }, [groups]);

  // Pending descent group: UI-only state for a group whose subtype +
  // first member directive aren't yet committed. Two paths reach here:
  //  1. "Edit slot…" picked from the top-level FieldAdder — pending
  //     renders at the end of the list (anchor = "end").
  //  2. Subtype chip cleared on an existing group — that group's
  //     member directives get deleted and pending takes over its visual
  //     position (anchor = "after-flat-index" of whatever was directly
  //     above the cleared group, or "start" if the group was first).
  // Empty groups can't exist in the data model, so this UI state holds
  // field+index+subtype until the first member is picked, then we
  // materialise the directive and dismiss pending.
  const [pending, setPending] = useState<{
    field: string;
    index: number;
    subtype: string | null;
    anchor: PendingAnchor;
  } | null>(null);

  const handleStartPending = (
    field: string,
    _elementType: string,
    elementSubtypes: readonly string[] | null,
  ) => {
    // Pre-pick subtype only when there's exactly one concrete choice —
    // saves a click on monomorphic-via-single-impl collections. Multiple
    // subtypes or no subtypes (monomorphic via element type) both leave
    // the field null; the picker shown in the pending header lets the
    // modder fill it in or accept the implicit element type.
    const presetSubtype = elementSubtypes?.length === 1 ? (elementSubtypes[0] ?? null) : null;
    setPending({ field, index: 0, subtype: presetSubtype, anchor: { kind: "end" } });
  };

  const handleConvertGroupToPending = (
    field: string,
    index: number,
    startFlatIndex: number,
    endFlatIndex: number,
  ) => {
    // Clearing the subtype drops every member directive — they're bound
    // to subtype-specific fields that won't survive a type change. The
    // group's structural position is preserved by anchoring pending to
    // whatever directive sat immediately above (or "start" if it was
    // the first thing in the node).
    const anchor: PendingAnchor =
      startFlatIndex === 0
        ? { kind: "start" }
        : { kind: "afterIndex", flatIndex: startFlatIndex - 1 };
    onSetDirectives([
      ...node.directives.slice(0, startFlatIndex),
      ...node.directives.slice(endFlatIndex),
    ]);
    setPending({ field, index, subtype: null, anchor });
  };

  const handleMaterialisePending = (newDirective: StampedDirective) => {
    if (!pending) return;
    const prefixed = buildDescentMemberDirective(
      pending.field,
      pending.index,
      pending.subtype,
      newDirective,
    );
    onSetDirectives(insertAtPendingAnchor(node.directives, prefixed, pending.anchor));
    setPending(null);
  };

  const looseRow = (
    d: StampedDirective,
    flatIndex: number,
    displayFieldPath?: string,
  ): React.ReactNode => (
    <DirectiveRow
      key={d._uiId}
      directive={d}
      flatIndex={flatIndex}
      displayFieldPath={displayFieldPath}
      memberMap={memberMap}
      vanillaFields={vanillaFields}
      reorder={reorder}
      onChange={onUpdateDirective}
      onDelete={onDeleteDirective}
    />
  );

  const renderPending = () =>
    pending && (
      <PendingDescentGroup
        field={pending.field}
        slotIndex={pending.index}
        subtype={pending.subtype}
        outerMember={memberMap.get(pending.field)}
        onChangeIndex={(index) => setPending({ ...pending, index })}
        onChangeSubtype={(subtype) => setPending({ ...pending, subtype })}
        onCancel={() => setPending(null)}
        onAddFirstMember={handleMaterialisePending}
      />
    );

  // Pending placement: render at the position dictated by its anchor.
  // "start" → before any group; "afterIndex N" → after the item whose
  // last flat index equals N; "end" → after every group. Each item only
  // checks one of these, so the pending group is rendered exactly once.
  const pendingAtStart = pending?.anchor.kind === "start";
  const pendingAtEnd = pending?.anchor.kind === "end";
  const pendingAfterFlatIndex =
    pending?.anchor.kind === "afterIndex" ? pending.anchor.flatIndex : null;

  return (
    <>
      {pendingAtStart && renderPending()}
      {groups.map((g, gi) => {
        const startIndex = groupStartIndices[gi] ?? 0;
        const endIndex = startIndex + (g.kind === "loose" ? 1 : g.members.length);
        const renderPendingAfter = pendingAfterFlatIndex === endIndex - 1;
        if (g.kind === "loose") {
          return (
            <React.Fragment key={g.directive._uiId}>
              {looseRow(g.directive, startIndex)}
              {renderPendingAfter && renderPending()}
            </React.Fragment>
          );
        }
        const memberRows = g.members.map((m, mi) =>
          looseRow(m.directive, startIndex + mi, m.suffix),
        );
        const firstMemberId = g.members[0]?.directive._uiId ?? "";
        const groupKey = firstMemberId || `group-${g.field}-${g.index}`;
        const groupHandlers = reorder.buildHandlers(firstMemberId, startIndex, endIndex);
        return (
          <React.Fragment key={groupKey}>
            {reorder.showIndicatorAt(startIndex, firstMemberId) && (
              <div className={styles.dropIndicator} />
            )}
            <DescentGroup
              field={g.field}
              slotIndex={g.index}
              subtype={g.subtype}
              startIndex={startIndex}
              endIndex={endIndex}
              outerMember={memberMap.get(g.field)}
              directives={node.directives}
              onSetDirectives={onSetDirectives}
              onConvertToPending={handleConvertGroupToPending}
              members={g.members}
              memberRows={memberRows}
              isDragging={groupHandlers.isDragging}
              onDragStart={groupHandlers.onDragStart}
              onDragEnd={groupHandlers.onDragEnd}
              onDragOverRow={groupHandlers.onDragOver}
              onDropRow={groupHandlers.onDrop}
            />
            {renderPendingAfter && renderPending()}
          </React.Fragment>
        );
      })}
      {reorder.showIndicatorAt(node.directives.length, null) && (
        <div className={styles.dropIndicator} />
      )}
      {pendingAtEnd && renderPending()}
      <FieldAdder
        members={members}
        membersLoaded={membersLoaded}
        existingFields={node.directives.map((d) => d.fieldPath)}
        targetTemplateType={node.templateType}
        onAdd={onAddDirective}
        onDrop={handleNodeDrop}
        vanillaFields={vanillaFields}
        onStartDescent={handleStartPending}
      />
    </>
  );
}

// --- DescentHeader ---
//
// Shared header chrome for descent group cards (both live and pending).
// Lays out the row: drag grip, "set" badge, field name, "at" + index
// input, subtype affordance (combobox / clickable chip / read-only span),
// spacer, X close button. The subtype affordance switches on mode:
//   - "fixed":     subtype shown read-only (not editable here; usually
//                  monomorphic with a single forced subtype, or no subtype)
//   - "clearable": chip clickable; click fires onChangeSubtype(null) and
//                  the parent decides whether to drop bound state
//   - "picker":    combobox when subtype is null, clearable chip when set
//
// `dragBinding` controls the grip: when provided the grip is draggable
// (live group case); when null the grip is rendered static (pending group
// can't be reordered until materialised).

interface DescentHeaderProps {
  readonly field: string;
  readonly slotIndex: number;
  readonly subtype: string | null;
  readonly subtypeChoices: readonly string[] | null;
  readonly subtypeMode: "fixed" | "clearable" | "picker";
  readonly onChangeIndex: (next: number) => void;
  readonly onChangeSubtype: (subtype: string | null) => void;
  readonly dragBinding: {
    readonly onDragStart: (e: React.DragEvent) => void;
    readonly onDragEnd: () => void;
    readonly payloadId: string;
    readonly title: string;
  } | null;
  readonly onClose: () => void;
  readonly closeTitle: string;
  readonly subtypeChipTitle?: string;
}

function DescentHeader({
  field,
  slotIndex,
  subtype,
  subtypeChoices,
  subtypeMode,
  onChangeIndex,
  onChangeSubtype,
  dragBinding,
  onClose,
  closeTitle,
  subtypeChipTitle,
}: DescentHeaderProps) {
  const fetchSubtypeChoices = useCallback(
    () => Promise.resolve(subtypeChoices ?? []),
    [subtypeChoices],
  );

  return (
    <div className={styles.setRowHeader}>
      {dragBinding !== null ? (
        <span
          className={styles.rowDragGrip}
          draggable
          onDragStart={dragBinding.onDragStart}
          onDragEnd={dragBinding.onDragEnd}
          title={dragBinding.title}
        >
          <GripVertical size={10} />
        </span>
      ) : (
        <span className={styles.rowDragGrip} aria-hidden>
          <GripVertical size={10} />
        </span>
      )}
      <span className={styles.setOpLabel}>set</span>
      <span className={styles.setField} title={field}>
        {field}
      </span>
      <span className={styles.setInsertAt}>at</span>
      <CommitInput
        type="number"
        className={styles.setIndexInput}
        value={slotIndex}
        min={0}
        step={1}
        onCommit={(v) => onChangeIndex(Number(v))}
      />
      {subtypeMode === "picker" && subtype === null ? (
        <SuggestionCombobox
          value=""
          placeholder="Pick subtype…"
          fetchSuggestions={fetchSubtypeChoices}
          onChange={(v) => onChangeSubtype(v === "" ? null : v)}
        />
      ) : subtype !== null && (subtypeMode === "clearable" || subtypeMode === "picker") ? (
        <button
          type="button"
          className={styles.compositeTypeClickable}
          onClick={() => onChangeSubtype(null)}
          title={subtypeChipTitle ?? "Clear subtype"}
        >
          {subtype}
        </button>
      ) : subtype !== null ? (
        <span className={styles.compositeType}>{subtype}</span>
      ) : null}
      <span className={styles.descentGroupSpacer} />
      <button type="button" className={styles.setDelete} onClick={onClose} title={closeTitle}>
        <X size={10} />
      </button>
    </div>
  );
}

// --- DescentGroup ---
//
// Visual wrapper for a contiguous run of descent directives sharing the
// same outer (field, index, subtype) prefix. Renders a header with the
// outer field/slot/subtype, the member SetRows the parent built (so they
// keep their flat-index identity for reorder), an inner FieldAdder that
// queries the subtype's members and inserts new directives at the group's
// boundary, and a delete-group button that strips the whole run from the
// flat directive list.

interface DescentGroupProps {
  field: string;
  slotIndex: number;
  subtype: string | null;
  /** Inclusive flat index of this group's first member directive. */
  startIndex: number;
  /** Exclusive flat index of the end of this group. New members get
   *  inserted at this position so they stay contiguous with the group. */
  endIndex: number;
  /** Outer collection member (for tooltips / element-type discovery). */
  outerMember: TemplateMember | undefined;
  /** Flat directive list owned by the parent node. Used to splice in/out
   *  of the group; the new list is then handed to onSetDirectives. */
  directives: StampedDirective[];
  onSetDirectives: (directives: StampedDirective[]) => void;
  /** Drop the group's member directives and convert to a pending state
   *  with the same outer field+index, ready for a fresh subtype pick.
   *  Mirrors the HandlerConstruction clear-handler-type flow — the
   *  visual structure stays in place, just the inner fields are wiped. */
  onConvertToPending?:
    | ((field: string, index: number, startFlatIndex: number, endFlatIndex: number) => void)
    | undefined;
  /** Members of the group as (directive, suffix) pairs. Used for the
   *  group-level drag handle (grabs the first member's _uiId so the
   *  parent's reorder helper can move all K members together via the
   *  dragRowGroupLength signal). Member rows themselves are pre-
   *  rendered by the parent and passed via `memberRows`. */
  members: { directive: StampedDirective; suffix: string }[];
  /** Pre-rendered member SetRows from the parent. Owned by the parent so
   *  they share its drag state and onUpdateDirective callbacks. */
  memberRows: React.ReactNode[];
  /** Drag-reorder state, mirrors SetRow's. Group is dragged as a unit:
   *  drag-start grabs all K members, drag-over targets the slot above /
   *  below the group's first row, drop fires the parent's reorder
   *  helper which slides the whole range to the new position. */
  isDragging: boolean;
  onDragStart: () => void;
  onDragEnd: () => void;
  onDragOverRow: (e: React.DragEvent) => void;
  onDropRow: () => void;
}

function DescentGroup({
  field,
  slotIndex,
  subtype,
  startIndex,
  endIndex,
  outerMember,
  directives,
  onSetDirectives,
  onConvertToPending,
  members,
  memberRows,
  isDragging,
  onDragStart,
  onDragEnd,
  onDragOverRow,
  onDropRow,
}: DescentGroupProps) {
  const subtypeChoices = outerMember?.elementSubtypes ?? null;
  // Inner-type members for the FieldAdder. Resolved from the subtype hint
  // when the group has one (polymorphic collection); otherwise fall back
  // to the outer member's element type (monomorphic owned-element list,
  // e.g. List<PropertyChange>).
  const innerType = subtype ?? outerMember?.elementTypeName ?? "";
  const { members: innerMembers, loaded: innerMembersLoaded } = useTemplateMembers(
    innerType || undefined,
  );

  // Members that already have a directive in the group — used to dim the
  // FieldAdder's "scalar already set" entries the same way the outer
  // NodeCard does. The directive's fieldPath is the inner-relative name
  // (descent context lives on directive.descent), so a single-segment
  // path with no further descent is a top-level inner member.
  const existingMemberNames = useMemo(() => {
    const names: string[] = [];
    for (let i = startIndex; i < endIndex; i++) {
      const d = directives[i];
      if (!d) continue;
      // Only top-level inner names — deeper composite paths and nested
      // descent live below this level and don't compete for dropdown slots.
      if (!d.fieldPath.includes(".") && (d.descent?.length ?? 0) <= 1) {
        names.push(d.fieldPath);
      }
    }
    return names;
  }, [directives, startIndex, endIndex]);

  const handleAddMember = (newDirective: StampedDirective) => {
    // The FieldAdder built the directive with fieldPath = inner member
    // name and no descent. Prepend this group's outer step so it joins
    // the run.
    const prefixed = buildDescentMemberDirective(field, slotIndex, subtype, newDirective);
    const next = [...directives.slice(0, endIndex), prefixed, ...directives.slice(endIndex)];
    onSetDirectives(next);
  };

  const handleDeleteGroup = () => {
    onSetDirectives([...directives.slice(0, startIndex), ...directives.slice(endIndex)]);
  };

  const handleChangeSlotIndex = (next: number) => {
    onSetDirectives(
      rewriteDescentSlotIndex(directives, startIndex, endIndex, field, slotIndex, next),
    );
  };

  // Clearing the subtype on a polymorphic group keeps the group's
  // outer (field, index) in place but drops every member directive
  // (each is bound to a subtype-specific field that won't survive a
  // type change) and re-shows the subtype picker. Same shape as
  // HandlerConstruction's clear-handler-type flow.
  const handleClearSubtype = () => {
    if (onConvertToPending) onConvertToPending(field, slotIndex, startIndex, endIndex);
  };

  // Polymorphic collection with multi-choice: clearing the chip drops the
  // whole group (subtype-specific member fields wouldn't survive a type
  // change). Single-choice / non-polymorphic: chip is read-only.
  const subtypeMode: "fixed" | "clearable" =
    subtypeChoices !== null && subtypeChoices.length > 1 ? "clearable" : "fixed";

  const firstMemberId = members[0]?.directive._uiId ?? "";

  return (
    <div
      className={`${styles.setRowComposite} ${isDragging ? styles.rowDragging : ""}`}
      onDragOver={onDragOverRow}
      onDrop={(e) => {
        e.preventDefault();
        onDropRow();
      }}
    >
      <DescentHeader
        field={field}
        slotIndex={slotIndex}
        subtype={subtype}
        subtypeChoices={subtypeChoices}
        subtypeMode={subtypeMode}
        onChangeIndex={handleChangeSlotIndex}
        onChangeSubtype={handleClearSubtype}
        dragBinding={{
          onDragStart: (e) => {
            e.stopPropagation();
            e.dataTransfer.effectAllowed = "move";
            // Same payload key per-row grips use; the parent's reorder
            // helper recognises group-head ids and moves all K members.
            e.dataTransfer.setData("application/x-jiangyu-row-reorder", firstMemberId);
            onDragStart();
          },
          onDragEnd,
          payloadId: firstMemberId,
          title: "Drag to reorder group",
        }}
        onClose={handleDeleteGroup}
        closeTitle="Remove descent group (deletes all member directives)"
        subtypeChipTitle="Clear subtype (deletes the group; restart via Edit slot)"
      />
      <div className={styles.compositeBody}>
        {memberRows}
        <FieldAdder
          members={innerMembers}
          membersLoaded={innerMembersLoaded}
          existingFields={existingMemberNames}
          targetTemplateType={innerType}
          onAdd={handleAddMember}
        />
      </div>
    </div>
  );
}

// --- PendingDescentGroup ---
//
// Skeleton for a descent group the modder has started but not yet
// committed to a real directive. Renders the same shell as a real
// DescentGroup but with editable slot/subtype controls in the header
// and an inner FieldAdder whose first add materialises the group's
// first directive (and dismisses the pending state). Empty groups
// can't exist in the data model; this is the holding cell while the
// modder fills out the inputs.

interface PendingDescentGroupProps {
  field: string;
  slotIndex: number;
  subtype: string | null;
  outerMember: TemplateMember | undefined;
  onChangeIndex: (index: number) => void;
  onChangeSubtype: (subtype: string | null) => void;
  onCancel: () => void;
  onAddFirstMember: (directive: StampedDirective) => void;
}

function PendingDescentGroup({
  field,
  slotIndex,
  subtype,
  outerMember,
  onChangeIndex,
  onChangeSubtype,
  onCancel,
  onAddFirstMember,
}: PendingDescentGroupProps) {
  // Inner type resolution mirrors DescentGroup's: subtype hint when set
  // (polymorphic collection), else fall back to the outer's element type.
  // The FieldAdder is gated until innerType resolves so the modder can't
  // add a member without first picking the subtype on a polymorphic list.
  const subtypeChoices = outerMember?.elementSubtypes ?? null;
  const isPolymorphic = subtypeChoices !== null && subtypeChoices.length > 0;
  const innerType = subtype ?? outerMember?.elementTypeName ?? "";

  const { members: innerMembers, loaded: innerMembersLoaded } = useTemplateMembers(
    innerType || undefined,
  );

  return (
    <div className={styles.setRowComposite}>
      <DescentHeader
        field={field}
        slotIndex={slotIndex}
        subtype={subtype}
        subtypeChoices={subtypeChoices}
        subtypeMode={isPolymorphic ? "picker" : "fixed"}
        onChangeIndex={onChangeIndex}
        onChangeSubtype={onChangeSubtype}
        dragBinding={null}
        onClose={onCancel}
        closeTitle="Cancel — descent group not yet committed"
        subtypeChipTitle="Clear subtype"
      />
      <div className={styles.compositeBody}>
        {isPolymorphic && subtype === null ? (
          <div className={styles.descentGroupHint}>Pick a subtype above before adding fields.</div>
        ) : (
          <FieldAdder
            members={innerMembers}
            membersLoaded={innerMembersLoaded}
            existingFields={[]}
            targetTemplateType={innerType}
            onAdd={onAddFirstMember}
          />
        )}
      </div>
    </div>
  );
}

// --- SetRow ---

interface SetRowProps {
  directive: StampedDirective;
  member?: TemplateMember | undefined;
  /** Vanilla template's value tree for this directive's top-level field, when
   *  available. Threaded into nested CompositeEditors so their inner
   *  FieldAdder can pre-fill sub-fields with the same vanilla data. */
  vanillaNode?: InspectedFieldNode | undefined;
  /** Override for the field-name label shown in the row. Used by descent
   *  groups to display the suffix (e.g. "ShowHUDText") instead of the
   *  underlying flat fieldPath ("EventHandlers[0].ShowHUDText"). The wire-
   *  format directive is unchanged; this only affects the row's title text. */
  displayFieldPath?: string | undefined;
  onChange: (directive: StampedDirective) => void;
  onDelete: () => void;
  isDragging: boolean;
  onDragStart: () => void;
  onDragEnd: () => void;
  onDragOverRow: (e: React.DragEvent) => void;
  onDropRow: () => void;
}

const SCALAR_OPS: DirectiveOp[] = ["Set"];
const COLLECTION_OPS: DirectiveOp[] = ["Set", "Append", "Insert", "Remove", "Clear"];
// Named arrays are fixed-size enum-indexed — only per-slot Set makes sense.
const NAMED_ARRAY_OPS: DirectiveOp[] = ["Set"];
const OP_LABELS: Record<DirectiveOp, string> = {
  Set: "set",
  Append: "append",
  Insert: "insert",
  Remove: "remove",
  Clear: "clear",
};

/**
 * Display-only labels for value kinds in the row's right-side chip. The
 * canonical wire-format names (TemplateReference, etc.) are too long /
 * jargon-y for a tiny UI label; this mapping is a single source of truth so
 * any future renames stay in one place. Storage / serialisation are
 * unaffected.
 */
const VALUE_KIND_LABELS: Record<EditorValueKind, string> = {
  Boolean: "Boolean",
  Byte: "Byte",
  Int32: "Int32",
  Single: "Single",
  String: "String",
  Enum: "Enum",
  TemplateReference: "Ref",
  Composite: "Composite",
  HandlerConstruction: "Handler",
};

function SetRow({
  directive,
  member,
  vanillaNode,
  displayFieldPath,
  onChange,
  onDelete,
  isDragging,
  onDragStart,
  onDragEnd,
  onDragOverRow,
  onDropRow,
}: SetRowProps) {
  const labelText = displayFieldPath ?? directive.fieldPath;
  const isCollection = member?.isCollection ?? false;
  const [opOpen, setOpOpen] = useState(false);
  const opRef = useRef<HTMLDivElement>(null);

  useEffect(() => {
    if (!opOpen) return;
    const handler = (e: MouseEvent) => {
      if (opRef.current && !opRef.current.contains(e.target as Node)) setOpOpen(false);
    };
    document.addEventListener("mousedown", handler);
    return () => document.removeEventListener("mousedown", handler);
  }, [opOpen]);

  const namedArrayEnum = member?.namedArrayEnumTypeName;
  const ops = namedArrayEnum ? NAMED_ARRAY_OPS : isCollection ? COLLECTION_OPS : SCALAR_OPS;
  const isRemove = directive.op === "Remove";
  const isClear = directive.op === "Clear";
  const isFieldBag = isFieldBagValue(directive.value);

  // Kind label: hide for remove/clear (no value), hide for field-bag values
  // (Composite / HandlerConstruction render their kind inline in the body).
  const kindLabel =
    isRemove || isClear || isFieldBag
      ? null
      : directive.value
        ? VALUE_KIND_LABELS[directive.value.kind]
        : null;

  return (
    <div
      className={`${isFieldBag ? styles.setRowComposite : styles.setRow} ${isDragging ? styles.rowDragging : ""}`}
      onDragOver={onDragOverRow}
      onDrop={(e) => {
        e.preventDefault();
        onDropRow();
      }}
    >
      <div className={styles.setRowHeader}>
        <span
          className={styles.rowDragGrip}
          draggable
          onDragStart={(e) => {
            e.stopPropagation();
            e.dataTransfer.effectAllowed = "move";
            e.dataTransfer.setData("application/x-jiangyu-row-reorder", directive._uiId);
            onDragStart();
          }}
          onDragEnd={onDragEnd}
          title="Drag to reorder"
        >
          <GripVertical size={10} />
        </span>
        {ops.length > 1 ? (
          <div className={styles.setOpWrap} ref={opRef}>
            <button
              type="button"
              className={styles.setOpBtn}
              onClick={() => setOpOpen((v) => !v)}
              title="Change operation"
            >
              {OP_LABELS[directive.op]}
              <ChevronDown size={10} />
            </button>
            {opOpen && (
              <div className={styles.setOpMenu}>
                {ops.map((op) => (
                  <button
                    key={op}
                    type="button"
                    className={`${styles.setOpMenuItem} ${op === directive.op ? styles.setOpMenuItemActive : ""}`}
                    onClick={() => {
                      // Remove and Clear both drop the value; Clear drops the
                      // index as well. Other ops keep / synthesise a value
                      // and set a default index when the op needs one.
                      const updated: StampedDirective =
                        op === "Clear"
                          ? {
                              op,
                              fieldPath: directive.fieldPath,
                              _uiId: directive._uiId,
                            }
                          : op === "Remove"
                            ? {
                                op,
                                fieldPath: directive.fieldPath,
                                index: directive.index ?? 0,
                                _uiId: directive._uiId,
                              }
                            : {
                                ...directive,
                                op,
                                value:
                                  directive.value ??
                                  (member
                                    ? makeDefaultValue(member)
                                    : { kind: "String", string: "" }),
                              };
                      if (
                        (op === "Insert" || op === "Remove" || op === "Set") &&
                        updated.index === undefined
                      )
                        updated.index = 0;
                      onChange(updated);
                      setOpOpen(false);
                    }}
                  >
                    {OP_LABELS[op]}
                  </button>
                ))}
              </div>
            )}
          </div>
        ) : (
          <span className={styles.setOpLabel}>{OP_LABELS[directive.op]}</span>
        )}
        {isRemove ? (
          <>
            <span
              className={styles.setField}
              title={member?.tooltip ? `${labelText} — ${member.tooltip}` : labelText}
            >
              {labelText}
              {member?.isSoundIdField && <span className={styles.fieldBadge}>sound</span>}
            </span>
            <div className={styles.setValue}>
              <div className={styles.setInsertRow}>
                <span className={styles.setInsertAt}>at</span>
                <CommitInput
                  type="number"
                  className={styles.setIndexInput}
                  value={directive.index ?? 0}
                  min={0}
                  step={1}
                  onCommit={(v) => onChange({ ...directive, index: Number(v) })}
                />
              </div>
            </div>
          </>
        ) : isClear ? (
          <span
            className={styles.setField}
            title={member?.tooltip ? `${labelText} — ${member.tooltip}` : labelText}
          >
            {labelText}
            {member?.isSoundIdField && <span className={styles.fieldBadge}>sound</span>}
          </span>
        ) : (
          <>
            <span
              className={styles.setField}
              title={member?.tooltip ? `${labelText} — ${member.tooltip}` : labelText}
            >
              {labelText}
              {member?.isSoundIdField && <span className={styles.fieldBadge}>sound</span>}
            </span>
            <div className={styles.setValue}>
              {directive.op === "Insert" || (directive.op === "Set" && isCollection) ? (
                <div className={styles.setInsertRow}>
                  <span className={styles.setInsertAt}>at</span>
                  {namedArrayEnum ? (
                    <NamedArrayIndexPicker
                      enumTypeName={namedArrayEnum}
                      index={directive.index ?? 0}
                      onChange={(i) => onChange({ ...directive, index: i })}
                    />
                  ) : (
                    <CommitInput
                      type="number"
                      className={styles.setIndexInput}
                      value={directive.index ?? 0}
                      min={0}
                      step={1}
                      onCommit={(v) => onChange({ ...directive, index: Number(v) })}
                    />
                  )}
                  {directive.value && !isFieldBag ? (
                    <ValueEditor
                      value={directive.value}
                      onChange={(v) => onChange({ ...directive, value: v })}
                      member={member}
                    />
                  ) : null}
                </div>
              ) : (
                <>
                  {directive.value && !isFieldBag ? (
                    <ValueEditor
                      value={directive.value}
                      onChange={(v) => onChange({ ...directive, value: v })}
                      member={member}
                    />
                  ) : null}
                </>
              )}
            </div>
          </>
        )}
        {kindLabel && <span className={styles.setKind}>{kindLabel}</span>}
        <button
          type="button"
          className={styles.setDelete}
          onClick={onDelete}
          title="Remove directive"
        >
          <X size={12} />
        </button>
      </div>
      {isFieldBag && directive.value && (
        <CompositeEditor
          value={directive.value}
          onChange={(v) => onChange({ ...directive, value: v })}
          vanillaNode={vanillaNode}
          elementSubtypes={member?.elementSubtypes ?? null}
        />
      )}
    </div>
  );
}

// --- NamedArrayIndexPicker ---
//
// Dropdown picker for `set "Field" index=N` on a [NamedArray(typeof(T))]
// member. Fetches the paired enum's members via the cached
// templatesEnumMembers RPC and labels each option by name; stores the
// ordinal value on the directive.

interface NamedArrayIndexPickerProps {
  enumTypeName: string;
  index: number;
  onChange: (index: number) => void;
}

function NamedArrayIndexPicker({ enumTypeName, index, onChange }: NamedArrayIndexPickerProps) {
  const [entries, setEntries] = useState<readonly EnumMemberEntry[] | null>(null);
  useEffect(() => {
    let cancelled = false;
    void getCachedEnumEntries(enumTypeName).then((m) => {
      if (!cancelled) setEntries(m);
    });
    return () => {
      cancelled = true;
    };
  }, [enumTypeName]);

  if (entries === null || entries.length === 0) {
    // Fall back to numeric while loading, or when the enum isn't resolvable.
    return (
      <CommitInput
        type="number"
        className={styles.setIndexInput}
        value={index}
        min={0}
        step={1}
        onCommit={(v) => onChange(Number(v))}
      />
    );
  }

  return (
    <select
      className={styles.setIndexSelect}
      value={index}
      onChange={(e) => onChange(Number(e.target.value))}
    >
      {entries.map((entry) => (
        <option key={entry.name} value={entry.value}>
          {entry.name}
        </option>
      ))}
    </select>
  );
}

// Row height for the virtualised suggestion dropdown — matches
// `.fieldAdderItem`'s vertical box (space-1 padding × 2 + a single line of
// monospace text). Kept in sync with the CSS by visual inspection only;
// estimateSize is just a hint, the actual layout is content-driven once
// rendered.
const SUGGESTION_ROW_HEIGHT = 28;

// --- ValueEditor ---

interface ValueEditorProps {
  value: EditorValue;
  onChange: (value: EditorValue) => void;
  member?: TemplateMember | undefined;
}

interface RangeHintProps {
  readonly min: number | null | undefined;
  readonly max: number | null | undefined;
}

function RangeHint({ min, max }: RangeHintProps) {
  if (min == null && max == null) return null;
  let text: string;
  if (min != null && max != null) {
    text = `${min}\u2013${max}`;
  } else if (min != null) {
    text = `\u2265${min}`;
  } else if (max != null) {
    text = `\u2264${max}`;
  } else {
    // Unreachable — the first guard already returned for (null, null).
    return null;
  }
  return <span className={styles.rangeHint}>{text}</span>;
}

function ValueEditor({ value, onChange, member }: ValueEditorProps) {
  switch (value.kind) {
    case "Boolean":
      return (
        <input
          type="checkbox"
          className={styles.setValueCheckbox}
          checked={value.boolean ?? false}
          onChange={(e) => onChange({ kind: "Boolean", boolean: e.target.checked })}
        />
      );

    case "Byte": {
      // [Range]/[Min] tightens the default 0..255 byte range; never widens it.
      const min = Math.max(0, member?.numericMin ?? 0);
      const max = Math.min(255, member?.numericMax ?? 255);
      const num = value.int32 ?? 0;
      const invalid = num < min || num > max || !Number.isInteger(num);
      return (
        <span className={styles.setValueInputWrap}>
          <CommitInput
            type="number"
            className={`${styles.setValueInput} ${invalid ? styles.setValueInvalid : ""}`}
            value={num}
            min={min}
            max={max}
            step={1}
            onCommit={(v) => onChange({ kind: "Byte", int32: Number(v) })}
          />
          <RangeHint min={min} max={max} />
        </span>
      );
    }

    case "Int32": {
      const num = value.int32 ?? 0;
      const min = member?.numericMin ?? undefined;
      const max = member?.numericMax ?? undefined;
      const invalid =
        !Number.isInteger(num) || (min != null && num < min) || (max != null && num > max);
      return (
        <span className={styles.setValueInputWrap}>
          <CommitInput
            type="number"
            className={`${styles.setValueInput} ${invalid ? styles.setValueInvalid : ""}`}
            value={num}
            min={min}
            max={max}
            step={1}
            onCommit={(v) => onChange({ kind: "Int32", int32: Number(v) })}
          />
          <RangeHint min={min} max={max} />
        </span>
      );
    }

    case "Single": {
      const num = value.single ?? 0;
      const min = member?.numericMin ?? undefined;
      const max = member?.numericMax ?? undefined;
      const invalid = (min != null && num < min) || (max != null && num > max);
      return (
        <span className={styles.setValueInputWrap}>
          <CommitInput
            type="number"
            className={`${styles.setValueInput} ${invalid ? styles.setValueInvalid : ""}`}
            value={num}
            min={min}
            max={max}
            step={0.01}
            onCommit={(v) => onChange({ kind: "Single", single: Number(v) })}
          />
          <RangeHint min={min} max={max} />
        </span>
      );
    }

    case "String":
      return (
        <CommitInput
          type="text"
          className={styles.setValueInput}
          value={value.string ?? ""}
          onCommit={(v) => onChange({ kind: "String", string: v })}
        />
      );

    case "Enum":
      return <EnumValueEditor value={value} onChange={onChange} member={member} />;

    case "TemplateReference":
      return <RefValueEditor value={value} onChange={onChange} member={member} />;

    case "Composite":
    case "HandlerConstruction":
      // Field-bag values are rendered by CompositeEditor at SetRow level.
      return null;

    default:
      return <span className={styles.setKind}>?</span>;
  }
}

function EnumValueEditor({ value, onChange, member }: ValueEditorProps) {
  // Mirrors RefValueEditor: when the catalog supplies the declared enum type
  // we hide the "enum"/type chrome entirely and only show the value picker.
  // The placeholder carries the type hint instead. C# enums are sealed so
  // there's no polymorphic case; the type label only appears as a fallback
  // when the catalog couldn't resolve a declared type (rare).
  const declaredEnumType = member?.enumTypeName ?? "";
  const showTypeSpan = declaredEnumType === "";
  const fetchEnumValues = useCallback(
    () => (declaredEnumType ? getCachedEnumMembers(declaredEnumType) : Promise.resolve([])),
    [declaredEnumType],
  );
  return (
    <div className={styles.setRefRow}>
      {showTypeSpan && (
        <>
          <span className={styles.setRefLabel}>enum</span>
          <span className={styles.setRefTypeInput}>{value.enumType ?? "?"}</span>
        </>
      )}
      <SuggestionCombobox
        value={value.enumValue ?? ""}
        placeholder={declaredEnumType ? `${declaredEnumType} value` : "Value"}
        fetchSuggestions={fetchEnumValues}
        onChange={(v) => {
          const next: EditorValue = { ...value, enumValue: v };
          const committedType = resolveEnumCommitType(declaredEnumType, value.enumType);
          if (committedType === undefined) delete next.enumType;
          else next.enumType = committedType;
          onChange(next);
        }}
      />
    </div>
  );
}

function RefValueEditor({ value, onChange, member }: ValueEditorProps) {
  const declaredRefType = member?.referenceTypeName ?? "";
  const isPolymorphic = member?.isReferenceTypePolymorphic === true;
  const explicitRefType = value.referenceType ?? "";
  const showTypeSelector = shouldShowRefTypeSelector(
    declaredRefType,
    isPolymorphic,
    explicitRefType,
  );
  const refType = resolveRefTypeDisplay(declaredRefType, isPolymorphic, explicitRefType);

  const fetchRefInstances = useCallback(async (): Promise<readonly SuggestionItem[]> => {
    if (!refType) return [];
    const [searchResult, projectClones] = await Promise.all([
      templatesSearch(refType),
      getCachedProjectClones(),
    ]);
    const gameItems: SuggestionItem[] = searchResult.instances.map((i) => ({ label: i.name }));
    const gameLabels = new Set(gameItems.map((i) => i.label));
    const cloneItems: SuggestionItem[] = projectClones
      .filter((c) => c.templateType === refType && !gameLabels.has(c.id))
      .map((c) => ({ label: c.id, tag: "clone" }));
    return [...cloneItems, ...gameItems];
  }, [refType]);

  return (
    <div className={styles.setRefRow}>
      {showTypeSelector && (
        <>
          <span className={styles.setRefLabel}>ref</span>
          <SuggestionCombobox
            value={refType}
            placeholder="Type"
            fetchSuggestions={getCachedTemplateTypes}
            onChange={(t) => {
              const next: EditorValue = { ...value };
              // For polymorphic destinations the explicit type IS the
              // disambiguation; never drop it, even when the modder happens
              // to pick a value that matches the (abstract) declared type.
              // For monomorphic destinations clearing or matching the
              // declared type is safe — the loader infers the type from
              // the field, so the explicit type is redundant.
              if (t === "") delete next.referenceType;
              else if (!isPolymorphic && t === declaredRefType) delete next.referenceType;
              else next.referenceType = t;
              onChange(next);
            }}
            className={styles.setRefTypeInput}
          />
        </>
      )}
      <SuggestionCombobox
        value={value.referenceId ?? ""}
        placeholder={refType ? `${refType} id` : "ID"}
        fetchSuggestions={fetchRefInstances}
        onChange={(id) => onChange({ ...value, referenceId: id })}
      />
    </div>
  );
}

// --- CompositeEditor ---

interface CompositeEditorProps {
  value: EditorValue;
  onChange: (value: EditorValue) => void;
  /** Vanilla composite node from the parent template, when available. Its
   *  `fields` (sub-field nodes) drive vanilla pre-fill for newly-added
   *  sub-fields. Sub-fields nested deeper than one composite level fall back
   *  to neutral defaults (we don't propagate further; the structure is
   *  unbounded and the high-fidelity converter wants member shapes we don't
   *  have at that depth). */
  vanillaNode?: InspectedFieldNode | undefined;
  /** For HandlerConstruction values targeting a polymorphic owned-element
   *  collection: the concrete subtypes the modder can pick. Drives the
   *  subtype combobox shown in place of the body until a type is chosen. */
  elementSubtypes?: readonly string[] | null;
}

function CompositeEditor({ value, onChange, vanillaNode, elementSubtypes }: CompositeEditorProps) {
  // Directives flow in from two places: parse (stampNodes recurses through
  // composites and assigns _uiId per directive) and FieldAdder (always builds
  // StampedDirective via uiId()). So everything we render here already has
  // _uiId — the TS surface just doesn't model that. Cast at the boundary.
  // Memoised so the `?? []` fallback doesn't allocate a fresh array each
  // render and ripple into downstream useMemo dependency arrays.
  const directives = useMemo(
    () => (value.compositeDirectives ?? []) as StampedDirective[],
    [value.compositeDirectives],
  );
  const { members, loaded: membersLoaded } = useTemplateMembers(value.compositeType);

  const handleDirectiveChange = (index: number, updated: StampedDirective) => {
    const next = directives.map((d, i) => (i === index ? updated : d));
    onChange({ ...value, compositeDirectives: next });
  };

  const handleDirectiveDelete = (index: number) => {
    onChange({ ...value, compositeDirectives: directives.filter((_, i) => i !== index) });
  };

  const handleAddDirective = (directive: StampedDirective) => {
    onChange({ ...value, compositeDirectives: [...directives, directive] });
  };

  const handleRowReorder = (fromId: string, toSlot: number) => {
    const fromIdx = directives.findIndex((d) => d._uiId === fromId);
    if (fromIdx === -1) return;
    const next = [...directives];
    const moved = next.splice(fromIdx, 1)[0];
    if (moved === undefined) return;
    const insertAt = toSlot > fromIdx ? toSlot - 1 : toSlot;
    next.splice(insertAt, 0, moved);
    onChange({ ...value, compositeDirectives: next });
  };

  const reorder = useDragReorder(handleRowReorder);

  // Vanilla sub-field lookup (composite's vanillaNode.fields → name→node).
  // Stays empty when vanillaNode is missing or doesn't expose object fields,
  // so the inner FieldAdder falls through to neutral defaults transparently.
  const vanillaSubFields = useMemo(() => {
    const map = new Map<string, InspectedFieldNode>();
    if (vanillaNode?.kind === "object" && vanillaNode.fields) {
      for (const f of vanillaNode.fields) {
        if (f.name) map.set(f.name, f);
      }
    }
    return map;
  }, [vanillaNode]);

  const compositeType = value.compositeType ?? "";
  // Track which top-level fields of the composite already have a
  // single-Set directive so the FieldAdder can dim "scalar already set"
  // entries the same way the outer NodeCard does.
  const existingFieldNames = useMemo(
    () =>
      directives
        .filter((d) => d.op === "Set" && !d.fieldPath.includes(".") && !d.fieldPath.includes("["))
        .map((d) => d.fieldPath),
    [directives],
  );

  const handleFieldDrop = (e: React.DragEvent) => {
    const raw = e.dataTransfer.getData("text/plain");
    const member = parseCrossMemberPayload(raw);
    if (!member) return;
    e.preventDefault();
    const toast = useToastStore.getState().push;
    if (compositeType !== "" && member.templateType !== compositeType) {
      toast({
        variant: "error",
        message: `Field "${member.fieldPath}" belongs to ${member.templateType}`,
        detail: `This composite is ${compositeType}.`,
      });
      return;
    }
    const synthMember = synthMemberFromPayload(member);
    if (!allowsMultipleDirectives(synthMember) && existingFieldNames.includes(member.fieldPath)) {
      toast({
        variant: "info",
        message: `"${member.fieldPath}" is already in this composite`,
      });
      return;
    }
    const vanilla = vanillaSubFields.get(member.fieldPath);
    handleAddDirective(makeDefaultDirective(synthMember, vanilla));
  };

  const memberMap = new Map(members.map((m) => [m.name, m]));

  const isHandler = value.kind === "HandlerConstruction";
  const subtypeChoices = elementSubtypes ?? null;
  const needsSubtypePick =
    isHandler && subtypeChoices !== null && subtypeChoices.length > 0 && !value.compositeType;

  if (needsSubtypePick) {
    return (
      <div className={styles.compositeBody}>
        <div className={styles.compositeHeader}>
          <span className={styles.compositeKind}>handler</span>
          <SuggestionCombobox
            value=""
            placeholder="Pick handler type…"
            fetchSuggestions={() => Promise.resolve(subtypeChoices)}
            onChange={(picked) => {
              if (picked === "") return;
              onChange({ ...value, compositeType: picked, compositeDirectives: [] });
            }}
          />
        </div>
      </div>
    );
  }

  const handleClearSubtype = () => {
    onChange({ ...value, compositeType: "", compositeDirectives: [] });
  };

  const canClearSubtype =
    isHandler && subtypeChoices !== null && subtypeChoices.length > 0 && !!value.compositeType;

  return (
    <div className={styles.compositeBody}>
      <div className={styles.compositeHeader}>
        <span className={styles.compositeKind}>{isHandler ? "handler" : "composite"}</span>
        {canClearSubtype ? (
          // Clickable subtype chip — clicking clears compositeType + fields
          // and re-shows the picker. Hover restyles to telegraph the action;
          // a separate X button next to it tested poorly (small target, easy
          // to miss; modders kept clearing the input by accident instead).
          <button
            type="button"
            className={styles.compositeTypeClickable}
            onClick={handleClearSubtype}
            title="Clear handler type (resets fields)"
          >
            {value.compositeType}
          </button>
        ) : (
          <span className={styles.compositeType}>
            {value.compositeType ?? (isHandler ? "handler" : "composite")}
          </span>
        )}
      </div>
      {directives.map((d, di) => (
        <DirectiveRow
          key={d._uiId}
          directive={d}
          flatIndex={di}
          memberMap={memberMap}
          vanillaFields={vanillaSubFields}
          reorder={reorder}
          onChange={handleDirectiveChange}
          onDelete={handleDirectiveDelete}
        />
      ))}
      {reorder.showIndicatorAt(directives.length, null) && <div className={styles.dropIndicator} />}
      <FieldAdder
        members={members}
        membersLoaded={membersLoaded}
        existingFields={existingFieldNames}
        targetTemplateType={compositeType}
        onAdd={handleAddDirective}
        onDrop={handleFieldDrop}
        vanillaFields={vanillaSubFields}
      />
    </div>
  );
}

// --- SuggestionCombobox ---

interface SuggestionItem {
  readonly label: string;
  readonly tag?: string;
}

interface SuggestionComboboxProps {
  value: string;
  placeholder: string;
  fetchSuggestions: () => Promise<readonly (string | SuggestionItem)[]>;
  onChange: (value: string) => void;
  className?: string;
}

function SuggestionCombobox({
  value,
  placeholder,
  fetchSuggestions,
  onChange,
  className,
}: SuggestionComboboxProps) {
  const [open, setOpen] = useState(false);
  const [items, setItems] = useState<readonly SuggestionItem[]>([]);
  const [loaded, setLoaded] = useState(false);
  const wrapRef = useRef<HTMLDivElement>(null);
  const inputRef = useRef<HTMLInputElement>(null);

  // Reset cache when the fetch function changes (e.g. refType changed).
  // React-docs prev-state pattern: synchronous setState in render bails out
  // unless the prop changed, so this doesn't loop. The lint rule is named
  // `set-state-in-effect` but currently misfires on this conditional pattern.
  // https://react.dev/reference/react/useState#storing-information-from-previous-renders
  const [prevFetchSuggestions, setPrevFetchSuggestions] = useState(() => fetchSuggestions);
  if (prevFetchSuggestions !== fetchSuggestions) {
    /* eslint-disable @eslint-react/set-state-in-effect -- prev-state pattern in render, not in an effect; see comment above. */
    setPrevFetchSuggestions(() => fetchSuggestions);
    setLoaded(false);
    setItems([]);
    /* eslint-enable @eslint-react/set-state-in-effect */
  }

  useEffect(() => {
    if (!open || loaded) return;
    void fetchSuggestions()
      .then((result) => {
        setItems(result.map((r) => (typeof r === "string" ? { label: r } : r)));
        setLoaded(true);
      })
      .catch(() => setLoaded(true));
  }, [open, loaded, fetchSuggestions]);

  // Close on outside click
  useEffect(() => {
    if (!open) return;
    const handler = (e: MouseEvent) => {
      if (wrapRef.current && !wrapRef.current.contains(e.target as Node)) setOpen(false);
    };
    document.addEventListener("mousedown", handler);
    return () => document.removeEventListener("mousedown", handler);
  }, [open]);

  const filtered = useMemo(() => {
    const lowerQuery = value.toLowerCase();
    return items.filter((item) => item.label.toLowerCase().includes(lowerQuery));
  }, [items, value]);

  // Virtualise the dropdown so the user gets all matches at every scale —
  // template-instance lists in MENACE run into the thousands. Each row is a
  // single text+badge button at ~28px tall (matches `.fieldAdderItem`
  // padding + a single line of mono text).
  const dropdownRef = useRef<HTMLDivElement>(null);
  // eslint-disable-next-line react-hooks/incompatible-library -- TanStack Virtual returns non-memoisable functions; the only API the library exposes.
  const rowVirtualizer = useVirtualizer({
    count: filtered.length,
    getScrollElement: () => dropdownRef.current,
    estimateSize: () => SUGGESTION_ROW_HEIGHT,
    overscan: 8,
  });

  return (
    <div
      className={`${styles.refCombobox} ${className ?? ""}`}
      ref={wrapRef}
      role="presentation"
      onClick={(e) => e.stopPropagation()}
    >
      <input
        ref={inputRef}
        type="text"
        className={styles.setValueInput}
        value={value}
        placeholder={placeholder}
        onFocus={() => setOpen(true)}
        onChange={(e) => {
          onChange(e.target.value);
          setOpen(true);
        }}
        onKeyDown={(e) => {
          if (e.key === "Escape") {
            setOpen(false);
            inputRef.current?.blur();
          } else if (e.key === "Enter" && filtered.length > 0) {
            const first = filtered[0];
            if (first) {
              onChange(first.label);
              setOpen(false);
            }
          }
        }}
      />
      {open && loaded && filtered.length > 0 && (
        <div className={styles.refComboboxDropdown} ref={dropdownRef}>
          <div
            style={{
              height: `${rowVirtualizer.getTotalSize()}px`,
              position: "relative",
              width: "100%",
            }}
          >
            {rowVirtualizer.getVirtualItems().map((row) => {
              const item = filtered[row.index];
              if (!item) return null;
              return (
                <button
                  key={`${item.label}${item.tag ?? ""}`}
                  type="button"
                  className={`${styles.fieldAdderItem} ${styles.refComboboxRow} ${item.label === value ? styles.setOpMenuItemActive : ""}`}
                  style={{ transform: `translateY(${row.start}px)` }}
                  onClick={() => {
                    onChange(item.label);
                    setOpen(false);
                  }}
                >
                  <span className={styles.fieldAdderItemName}>{item.label}</span>
                  {item.tag && <span className={styles.suggestionTag}>{item.tag}</span>}
                </button>
              );
            })}
          </div>
        </div>
      )}
      {open && loaded && filtered.length === 0 && value.length > 0 && (
        <div className={styles.refComboboxDropdown}>
          <div className={styles.fieldAdderHint}>No matches</div>
        </div>
      )}
    </div>
  );
}

// --- FieldAdder ---

interface FieldAdderProps {
  members: readonly TemplateMember[];
  membersLoaded: boolean;
  existingFields: string[];
  /** Template type (or composite type) this adder belongs to. Used to reject
   *  drags of fields from unrelated templates. Empty string = accept any. */
  targetTemplateType: string;
  onAdd: (directive: StampedDirective) => void;
  onDrop?: (e: React.DragEvent) => void;
  /** Optional vanilla-value lookup (top-level field name → vanilla node). When
   *  provided, picking a field pre-fills the directive with that field's
   *  serialised value from the target template instead of the type's neutral
   *  default. Empty / missing entries fall back to the neutral default. */
  vanillaFields?: ReadonlyMap<string, InspectedFieldNode>;
  /** Optional callback for starting a descent group on a collection field.
   *  Provided by the top-level NodeCard; absent inside CompositeEditor and
   *  DescentGroup (descent groups don't nest from this UI yet — modders
   *  who need that author the inner descent in source mode). */
  onStartDescent?: (
    field: string,
    elementType: string,
    elementSubtypes: readonly string[] | null,
  ) => void;
}

function FieldAdder({
  members,
  membersLoaded,
  existingFields,
  targetTemplateType,
  onAdd,
  onDrop,
  vanillaFields,
  onStartDescent,
}: FieldAdderProps) {
  const [query, setQuery] = useState("");
  const [open, setOpen] = useState(false);
  const [fieldDragOver, setFieldDragOver] = useState<"accept" | "reject" | false>(false);
  const wrapRef = useRef<HTMLDivElement>(null);
  const inputRef = useRef<HTMLInputElement>(null);

  useEffect(() => {
    if (!open) return;
    const handler = (e: MouseEvent) => {
      if (wrapRef.current && !wrapRef.current.contains(e.target as Node)) setOpen(false);
    };
    document.addEventListener("mousedown", handler);
    return () => document.removeEventListener("mousedown", handler);
  }, [open]);

  const existingSet = new Set(existingFields);
  const lowerQuery = query.toLowerCase();
  const filtered = members.filter(
    (m) =>
      (m.isWritable || m.isCollection) &&
      !m.isHiddenInInspector &&
      // Odin-routed members (interface-typed fields, abstract non-Unity types,
      // anything Unity's native serialiser can't handle) live in the Odin
      // serializationData blob. The data-only patching path can't write into
      // that blob, so picking one would just produce an empty composite the
      // modder can't fill — hide them rather than offer a dead end.
      !m.isLikelyOdinOnly &&
      m.name.toLowerCase().includes(lowerQuery),
  );
  // Multi-directive fields (collections, named arrays) stay in `available`
  // even when an entry already exists, because adding another directive is
  // the normal flow rather than a duplicate.
  const available = filtered.filter((m) => !existingSet.has(m.name) || allowsMultipleDirectives(m));
  const alreadyAdded = filtered.filter(
    (m) => existingSet.has(m.name) && !allowsMultipleDirectives(m),
  );

  const handleSelect = (member: TemplateMember) => {
    const vanilla = vanillaFields?.get(member.name);
    onAdd(makeDefaultDirective(member, vanilla));
    setQuery("");
    setOpen(false);
  };

  const handleKeyDown = (e: React.KeyboardEvent) => {
    if (e.key === "Escape") {
      setOpen(false);
      inputRef.current?.blur();
    } else if (e.key === "Enter" && available.length > 0) {
      const first = available[0];
      if (first !== undefined) handleSelect(first);
    }
  };

  return (
    <div
      className={`${styles.fieldAdder} ${fieldDragOver === "accept" ? styles.fieldAdderDragOver : ""} ${fieldDragOver === "reject" ? styles.fieldAdderDragReject : ""}`}
      ref={wrapRef}
      onDragOver={(e) => {
        const types = e.dataTransfer.types;
        if (types.includes(MEMBER_DRAG_TAG)) {
          // Same-window: use the drag context to validate against the target
          // template type and existing fields. Cross-window (active === null)
          // falls through to accept; drop handler re-validates with a toast.
          const active = getActiveTemplateDrag();
          const mismatch =
            active?.kind === "member" &&
            targetTemplateType !== "" &&
            active.templateType !== targetTemplateType;
          // Look up the member in the local catalog so multi-directive fields
          // (collections, named arrays) don't visually reject a re-drop.
          // Unknown fieldPaths (nested drags whose member isn't in this
          // adder's flat list) keep the conservative "duplicate = reject"
          // behaviour; the drop handler always re-validates.
          const activeMember =
            active?.kind === "member" ? members.find((m) => m.name === active.fieldPath) : null;
          const duplicate =
            active?.kind === "member" &&
            existingFields.includes(active.fieldPath) &&
            !(activeMember && allowsMultipleDirectives(activeMember));
          if (mismatch || duplicate) {
            setFieldDragOver("reject");
          } else {
            e.preventDefault();
            e.dataTransfer.dropEffect = "copy";
            setFieldDragOver("accept");
          }
        } else if (types.includes(INSTANCE_DRAG_TAG)) {
          setFieldDragOver("reject");
        } else if (types.includes("text/plain")) {
          e.preventDefault();
          e.dataTransfer.dropEffect = "copy";
          setFieldDragOver("accept");
        }
      }}
      onDragLeave={() => setFieldDragOver(false)}
      onDrop={(e) => {
        setFieldDragOver(false);
        if (onDrop) onDrop(e);
      }}
    >
      <Plus size={12} className={styles.fieldAdderIcon} />
      <input
        ref={inputRef}
        type="text"
        className={styles.fieldAdderInput}
        placeholder="Add field…"
        value={query}
        onFocus={() => setOpen(true)}
        onChange={(e) => {
          setQuery(e.target.value);
          setOpen(true);
        }}
        onKeyDown={handleKeyDown}
      />
      {open && (
        <div className={styles.fieldAdderDropdown}>
          {!membersLoaded && <div className={styles.fieldAdderHint}>Loading fields…</div>}
          {membersLoaded && filtered.length === 0 && query.length > 0 && (
            <div className={styles.fieldAdderHint}>No matching fields</div>
          )}
          {membersLoaded && filtered.length === 0 && query.length === 0 && members.length === 0 && (
            <div className={styles.fieldAdderHint}>No fields available</div>
          )}
          {available.map((m) => (
            <React.Fragment key={m.name}>
              <button
                type="button"
                className={styles.fieldAdderItem}
                onClick={() => handleSelect(m)}
              >
                <span className={styles.fieldAdderItemName}>{m.name}</span>
                <span className={styles.fieldAdderItemType}>{m.typeName}</span>
                {m.isCollection && <span className={styles.fieldAdderItemBadge}>collection</span>}
              </button>
              {onStartDescent && m.isCollection === true && (
                <button
                  type="button"
                  className={`${styles.fieldAdderItem} ${styles.fieldAdderItemDescent}`}
                  onClick={() => {
                    onStartDescent(m.name, m.elementTypeName ?? "", m.elementSubtypes ?? null);
                    setQuery("");
                    setOpen(false);
                  }}
                  title="Edit fields of an existing element instead of appending a new one"
                >
                  <span className={styles.fieldAdderItemName}>↳ Edit slot of {m.name}…</span>
                </button>
              )}
            </React.Fragment>
          ))}
          {alreadyAdded.length > 0 && available.length > 0 && (
            <div className={styles.fieldAdderSep}>Already added</div>
          )}
          {alreadyAdded.map((m) => (
            <button
              key={m.name}
              type="button"
              className={`${styles.fieldAdderItem} ${styles.fieldAdderItemDim}`}
              onClick={() => handleSelect(m)}
              title="Already added — click to add another directive"
            >
              <span className={styles.fieldAdderItemName}>{m.name}</span>
              <span className={styles.fieldAdderItemType}>{m.typeName}</span>
            </button>
          ))}
        </div>
      )}
    </div>
  );
}

// --- Helpers ---

function makeDefaultDirective(
  member: TemplateMember,
  vanillaNode?: InspectedFieldNode,
): StampedDirective {
  // NamedArray fields are fixed-size enum-indexed lookups — they only accept
  // `set "Field" index=N <value>`, so default to Set-at-0 and let the picker
  // drive the ordinal. Plain collections default to Append; scalars to Set.
  if (member.namedArrayEnumTypeName) {
    let value = makeDefaultValue(member);
    if (vanillaNode?.kind === "array" && vanillaNode.elements && vanillaNode.elements.length > 0) {
      const vanillaFirst = inspectedFieldToEditorValue(vanillaNode.elements[0], member);
      if (vanillaFirst) value = vanillaFirst;
    }
    return { op: "Set", fieldPath: member.name, index: 0, value, _uiId: uiId() };
  }
  // Plain collections stay on neutral defaults — Append takes one new element,
  // and pre-filling it from a vanilla peer would imply the modder wants a
  // duplicate of an existing entry, which is rarely true.
  const useVanilla = !member.isCollection;
  let value = makeDefaultValue(member);
  if (useVanilla) {
    const vanillaValue = inspectedFieldToEditorValue(vanillaNode, member);
    if (vanillaValue) value = vanillaValue;
  }
  const op: DirectiveOp = member.isCollection ? "Append" : "Set";
  return { op, fieldPath: member.name, value, _uiId: uiId() };
}

// Build a TemplateMember from a cross-member drag payload. Strips undefined
// keys so the resulting object satisfies exactOptionalPropertyTypes — which
// would otherwise reject `{ patchScalarKind: undefined }` even though the
// field is optional on the target type.
function synthMemberFromPayload(payload: CrossMemberPayload): TemplateMember {
  return {
    name: payload.fieldPath,
    typeName: payload.typeName ?? "",
    isWritable: true,
    isInherited: false,
    ...(payload.patchScalarKind !== undefined ? { patchScalarKind: payload.patchScalarKind } : {}),
    ...(payload.elementTypeName !== undefined ? { elementTypeName: payload.elementTypeName } : {}),
    ...(payload.enumTypeName !== undefined ? { enumTypeName: payload.enumTypeName } : {}),
    ...(payload.referenceTypeName !== undefined
      ? { referenceTypeName: payload.referenceTypeName }
      : {}),
    ...(payload.isCollection !== undefined ? { isCollection: payload.isCollection } : {}),
    ...(payload.isScalar !== undefined ? { isScalar: payload.isScalar } : {}),
    ...(payload.isTemplateReference !== undefined
      ? { isTemplateReference: payload.isTemplateReference }
      : {}),
    ...(payload.namedArrayEnumTypeName !== undefined
      ? { namedArrayEnumTypeName: payload.namedArrayEnumTypeName }
      : {}),
  };
}
