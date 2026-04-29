import React, { useCallback, useEffect, useMemo, useRef, useState } from "react";
import { ChevronDown, GripVertical, Plus, X } from "lucide-react";
import { useVirtualizer } from "@tanstack/react-virtual";
import type { CrossMemberPayload } from "@lib/drag/crossMember";
import type {
  EditorNode,
  EditorDirective,
  DirectiveOp,
  EditorValue,
  EditorValueKind,
  EditorDocument,
  EditorError,
} from "./types";
import type {
  EnumMemberEntry,
  EnumMembersResult,
  TemplateMember,
  TemplateQueryResult,
  TemplateSearchResult,
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

// --- Stable UI IDs ---
// Assigned on parse, stripped before serialise. Used for React keys,
// collapse state, and drag payloads. Stamped variants narrow `_uiId` to
// required so the editor body doesn't have to assert non-null at every use.

let _nextId = 0;
function uiId(): string {
  return `_ui_${++_nextId}`;
}

type StampedDirective = Omit<EditorDirective, "_uiId"> & { _uiId: string };
type StampedNode = Omit<EditorNode, "_uiId" | "directives"> & {
  _uiId: string;
  directives: StampedDirective[];
};

function stampNodes(nodes: EditorNode[]): StampedNode[] {
  return nodes.map((n) => ({
    ...n,
    _uiId: n._uiId ?? uiId(),
    directives: n.directives.map((d) => ({ ...d, _uiId: d._uiId ?? uiId() })),
  }));
}

function stripUiIds(doc: EditorDocument): EditorDocument {
  return {
    ...doc,
    nodes: doc.nodes.map((n) => {
      const { _uiId: _nId, ...rest } = n;
      return {
        ...rest,
        directives: n.directives.map((d) => {
          const { _uiId: _dId, ...dr } = d;
          return dr;
        }),
      };
    }),
  };
}

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

function templatesQuery(typeName: string): Promise<TemplateQueryResult> {
  return rpcCall<TemplateQueryResult>("templatesQuery", { typeName });
}

function templatesSearch(className?: string): Promise<TemplateSearchResult> {
  return rpcCall<TemplateSearchResult>("templatesSearch", className ? { className } : undefined);
}

function templatesEnumMembers(typeName: string): Promise<EnumMembersResult> {
  return rpcCall<EnumMembersResult>("templatesEnumMembers", { typeName });
}

// Simple in-memory cache for enum members and template type lists to avoid repeated RPCs
const enumMembersCache = new Map<string, readonly EnumMemberEntry[]>();
const templateTypesCache = { types: null as readonly string[] | null };

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

  // Parse via RPC when content changes externally
  useEffect(() => {
    if (content === lastSerialisedRef.current) return;

    void templatesParse(content)
      .then((doc) => {
        setNodes(stampNodes(doc.nodes));
        setParseErrors(doc.errors);
        setRpcError(null);
        // External change resets undo history
        undoStackRef.current = [];
        redoStackRef.current = [];
      })
      .catch((err: unknown) => {
        setRpcError(err instanceof Error ? err.message : "Parse RPC failed");
      });
  }, [content]);

  // Debounced serialise via RPC after local edits
  const serialiseTimerRef = useRef<ReturnType<typeof setTimeout> | undefined>(undefined);

  const serialiseNodes = useCallback(
    (updated: StampedNode[]) => {
      setNodes(updated);
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

  const serialiseAndEmit = useCallback(
    (updated: StampedNode[]) => {
      undoStackRef.current = [...undoStackRef.current, nodesRef.current];
      redoStackRef.current = [];
      invalidateProjectClonesCache();
      serialiseNodes(updated);
    },
    [serialiseNodes],
  );

  // Undo/redo keyboard handler
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
        serialiseNodes(next);
      } else {
        const prev = undoStackRef.current.pop();
        if (prev === undefined) return;
        redoStackRef.current = [...redoStackRef.current, nodesRef.current];
        serialiseNodes(prev);
      }
    };
    document.addEventListener("keydown", handler, true);
    return () => document.removeEventListener("keydown", handler, true);
  }, [serialiseNodes]);

  const handleDeleteNode = useCallback(
    (index: number) => {
      serialiseAndEmit(nodes.filter((_, i) => i !== index));
    },
    [nodes, serialiseAndEmit],
  );

  const handleUpdateNode = useCallback(
    (index: number, updated: StampedNode) => {
      serialiseAndEmit(nodes.map((node, i) => (i === index ? updated : node)));
    },
    [nodes, serialiseAndEmit],
  );

  const handleUpdateDirective = useCallback(
    (nodeIndex: number, dirIndex: number, directive: StampedDirective) => {
      const updated = nodes.map((node, ni) => {
        if (ni !== nodeIndex) return node;
        return {
          ...node,
          directives: node.directives.map((d, di) => (di === dirIndex ? directive : d)),
        };
      });
      serialiseAndEmit(updated);
    },
    [nodes, serialiseAndEmit],
  );

  const handleDeleteDirective = useCallback(
    (nodeIndex: number, dirIndex: number) => {
      const updated = nodes.map((node, ni) => {
        if (ni !== nodeIndex) return node;
        return { ...node, directives: node.directives.filter((_, di) => di !== dirIndex) };
      });
      serialiseAndEmit(updated);
    },
    [nodes, serialiseAndEmit],
  );

  const handleAddDirective = useCallback(
    (nodeIndex: number, directive: StampedDirective) => {
      const updated = nodes.map((node, ni) => {
        if (ni !== nodeIndex) return node;
        return { ...node, directives: [...node.directives, directive] };
      });
      serialiseAndEmit(updated);
    },
    [nodes, serialiseAndEmit],
  );

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

  // Card-level drop: accept member drags to add set directives. Drop-time
  // checks cover the cross-window case where the dragOver-time context isn't
  // available; same-window drops already got rejected visually by the
  // FieldAdder's own dragOver gate.
  const handleCardDrop = useCallback(
    (nodeIndex: number, e: React.DragEvent) => {
      const raw = e.dataTransfer.getData("text/plain");
      const member = parseCrossMemberPayload(raw);
      if (!member) return;
      e.preventDefault();
      const node = nodes[nodeIndex];
      if (!node) return;
      const toast = useToastStore.getState().push;
      if (node.templateType !== "" && member.templateType !== node.templateType) {
        toast({
          variant: "error",
          message: `Field does not belong to ${node.templateType}`,
          detail: `"${member.fieldPath}" belongs to ${member.templateType}`,
        });
        return;
      }
      if (node.directives.some((d) => d.fieldPath === member.fieldPath)) {
        toast({
          variant: "info",
          message: `"${member.fieldPath}" is already on this node`,
        });
        return;
      }
      // Build the directive from the SAME helper the field-adder dropdown
      // uses, so a dragged ref field drops in as a TemplateReference (not a
      // raw string), an enum field gets the right enumType, a NamedArray
      // field defaults to Set+index=0, etc. The drag payload now carries the
      // full schema needed to construct the synthetic TemplateMember.
      const synthMember = synthMemberFromPayload(member);
      handleAddDirective(nodeIndex, makeDefaultDirective(synthMember));
    },
    [handleAddDirective, nodes],
  );

  // Add empty node
  const handleAddNode = useCallback(
    (kind: "Patch" | "Clone") => {
      const newNode: StampedNode = {
        kind,
        templateType: "",
        directives: [],
        _uiId: uiId(),
      };
      serialiseAndEmit([...nodes, newNode]);
    },
    [nodes, serialiseAndEmit],
  );

  // Card reorder state
  const [dragCardId, setDragCardId] = useState<string | null>(null);
  const [dragCardSlot, setDragCardSlot] = useState<number | null>(null);

  const handleCardReorder = useCallback(
    (fromId: string, toSlot: number) => {
      const fromIndex = nodes.findIndex((n) => n._uiId === fromId);
      if (fromIndex === -1) return;
      const updated = [...nodes];
      const moved = updated.splice(fromIndex, 1)[0];
      if (moved === undefined) return;
      const insertAt = toSlot > fromIndex ? toSlot - 1 : toSlot;
      updated.splice(insertAt, 0, moved);
      serialiseAndEmit(updated);
    },
    [nodes, serialiseAndEmit],
  );

  // Row reorder within a card
  const handleRowReorder = useCallback(
    (nodeIndex: number, fromId: string, toSlot: number) => {
      const updated = nodes.map((node, ni) => {
        if (ni !== nodeIndex) return node;
        const dirs = [...node.directives];
        const fromIdx = dirs.findIndex((d) => d._uiId === fromId);
        if (fromIdx === -1) return node;
        const moved = dirs.splice(fromIdx, 1)[0];
        if (moved === undefined) return node;
        const insertAt = toSlot > fromIdx ? toSlot - 1 : toSlot;
        dirs.splice(insertAt, 0, moved);
        return { ...node, directives: dirs };
      });
      serialiseAndEmit(updated);
    },
    [nodes, serialiseAndEmit],
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
      serialiseAndEmit([...nodes, newNode]);
    },
    [nodes, serialiseAndEmit],
  );

  return (
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

      {nodes.map((node, ni) => (
        <React.Fragment key={node._uiId}>
          {dragCardSlot === ni && dragCardId !== node._uiId && (
            <div className={styles.dropIndicator} />
          )}
          <NodeCard
            node={node}
            collapsed={collapsed.has(node._uiId)}
            onToggleCollapse={() => handleToggleCollapse(node._uiId)}
            onDelete={() => handleDeleteNode(ni)}
            onUpdateNode={(updated) => handleUpdateNode(ni, updated)}
            onUpdateDirective={(di, d) => handleUpdateDirective(ni, di, d)}
            onDeleteDirective={(di) => handleDeleteDirective(ni, di)}
            onAddDirective={(d) => handleAddDirective(ni, d)}
            onDrop={(e) => handleCardDrop(ni, e)}
            isDragging={dragCardId === node._uiId}
            onDragStart={() => setDragCardId(node._uiId)}
            onDragEnd={() => {
              setDragCardId(null);
              setDragCardSlot(null);
            }}
            onDragOverCard={(e) => {
              if (!dragCardId || dragCardId === node._uiId) return;
              e.preventDefault();
              e.dataTransfer.dropEffect = "move";
              const rect = e.currentTarget.getBoundingClientRect();
              const y = e.clientY - rect.top;
              setDragCardSlot(y < rect.height / 2 ? ni : ni + 1);
            }}
            onDropCard={() => {
              if (dragCardId && dragCardSlot !== null) {
                handleCardReorder(dragCardId, dragCardSlot);
              }
              setDragCardId(null);
              setDragCardSlot(null);
            }}
            onRowReorder={(fromId, toSlot) => handleRowReorder(ni, fromId, toSlot)}
          />
        </React.Fragment>
      ))}
      {dragCardSlot === nodes.length && <div className={styles.dropIndicator} />}

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
            <button type="button" className={styles.addNodeBtn} onClick={() => handleAddNode(kind)}>
              <Plus size={12} />
              Add {kind}
            </button>
          </div>
        ))}
      </div>
    </div>
  );
}

// --- NodeCard ---

interface NodeCardProps {
  node: StampedNode;
  collapsed: boolean;
  onToggleCollapse: () => void;
  onDelete: () => void;
  onUpdateNode: (updated: StampedNode) => void;
  onUpdateDirective: (dirIndex: number, directive: StampedDirective) => void;
  onDeleteDirective: (dirIndex: number) => void;
  onAddDirective: (directive: StampedDirective) => void;
  onDrop: (e: React.DragEvent) => void;
  isDragging: boolean;
  onDragStart: () => void;
  onDragEnd: () => void;
  onDragOverCard: (e: React.DragEvent) => void;
  onDropCard: () => void;
  onRowReorder: (fromId: string, toSlot: number) => void;
}

function NodeCard({
  node,
  collapsed,
  onToggleCollapse,
  onDelete,
  onUpdateNode,
  onUpdateDirective,
  onDeleteDirective,
  onAddDirective,
  onDrop,
  isDragging,
  onDragStart,
  onDragEnd,
  onDragOverCard,
  onDropCard,
  onRowReorder,
}: NodeCardProps) {
  const isPatch = node.kind === "Patch";
  const [members, setMembers] = useState<readonly TemplateMember[]>([]);
  const [membersLoaded, setMembersLoaded] = useState(false);
  const justDraggedRef = useRef(false);

  useEffect(() => {
    if (collapsed || membersLoaded) return;
    void templatesQuery(node.templateType)
      .then((result) => {
        setMembers(result.members ?? []);
        setMembersLoaded(true);
      })
      .catch(() => setMembersLoaded(true));
  }, [collapsed, membersLoaded, node.templateType]);

  const memberMap = new Map(members.map((m) => [m.name, m]));

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

  // Row reorder state
  const [dragRowId, setDragRowId] = useState<string | null>(null);
  const [dragRowSlot, setDragRowSlot] = useState<number | null>(null);

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
          onChange={(t) => onUpdateNode({ ...node, templateType: t })}
          className={styles.cardTypeInput}
        />
        {isPatch ? (
          <SuggestionCombobox
            value={node.templateId ?? ""}
            placeholder="ID"
            fetchSuggestions={fetchInstances}
            onChange={(v) => onUpdateNode({ ...node, templateId: v })}
            className={styles.cardIdInput}
          />
        ) : (
          <>
            <span className={styles.cardProp}>from</span>
            <SuggestionCombobox
              value={node.sourceId ?? ""}
              placeholder="Source ID"
              fetchSuggestions={fetchInstances}
              onChange={(v) => onUpdateNode({ ...node, sourceId: v })}
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
                onCommit={(v) => onUpdateNode({ ...node, cloneId: v })}
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
            onDelete();
          }}
          title="Remove node"
        >
          <X size={14} />
        </button>
      </div>

      {!collapsed && (
        <div className={styles.cardBody}>
          {node.directives.map((d, di) => {
            const baseName = d.fieldPath.replace(/\[.*\]$/, "");
            return (
              <React.Fragment key={d._uiId}>
                {dragRowSlot === di && dragRowId !== d._uiId && (
                  <div className={styles.dropIndicator} />
                )}
                <SetRow
                  directive={d}
                  member={memberMap.get(baseName)}
                  onChange={(updated) => onUpdateDirective(di, updated)}
                  onDelete={() => onDeleteDirective(di)}
                  isDragging={dragRowId === d._uiId}
                  onDragStart={() => setDragRowId(d._uiId)}
                  onDragEnd={() => {
                    setDragRowId(null);
                    setDragRowSlot(null);
                  }}
                  onDragOverRow={(e) => {
                    if (!dragRowId || dragRowId === d._uiId) return;
                    e.preventDefault();
                    e.dataTransfer.dropEffect = "move";
                    const rect = e.currentTarget.getBoundingClientRect();
                    const y = e.clientY - rect.top;
                    setDragRowSlot(y < rect.height / 2 ? di : di + 1);
                  }}
                  onDropRow={() => {
                    if (dragRowId && dragRowSlot !== null) {
                      onRowReorder(dragRowId, dragRowSlot);
                    }
                    setDragRowId(null);
                    setDragRowSlot(null);
                  }}
                />
              </React.Fragment>
            );
          })}
          {dragRowSlot === node.directives.length && <div className={styles.dropIndicator} />}
          <FieldAdder
            members={members}
            membersLoaded={membersLoaded}
            existingFields={node.directives.map((d) => d.fieldPath)}
            targetTemplateType={node.templateType}
            onAdd={onAddDirective}
            onDrop={onDrop}
          />
        </div>
      )}
    </div>
  );
}

// --- SetRow ---

interface SetRowProps {
  directive: StampedDirective;
  member?: TemplateMember | undefined;
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
};

function SetRow({
  directive,
  member,
  onChange,
  onDelete,
  isDragging,
  onDragStart,
  onDragEnd,
  onDragOverRow,
  onDropRow,
}: SetRowProps) {
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
  const isComposite = directive.value?.kind === "Composite";

  // Kind label: hide for remove/clear (no value), hide for composite (shown inline)
  const kindLabel =
    isRemove || isClear || isComposite
      ? null
      : directive.value
        ? VALUE_KIND_LABELS[directive.value.kind]
        : null;

  return (
    <div
      className={`${isComposite ? styles.setRowComposite : styles.setRow} ${isDragging ? styles.rowDragging : ""}`}
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
              title={
                member?.tooltip ? `${directive.fieldPath} — ${member.tooltip}` : directive.fieldPath
              }
            >
              {directive.fieldPath}
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
            title={
              member?.tooltip ? `${directive.fieldPath} — ${member.tooltip}` : directive.fieldPath
            }
          >
            {directive.fieldPath}
            {member?.isSoundIdField && <span className={styles.fieldBadge}>sound</span>}
          </span>
        ) : (
          <>
            <span
              className={styles.setField}
              title={
                member?.tooltip ? `${directive.fieldPath} — ${member.tooltip}` : directive.fieldPath
              }
            >
              {directive.fieldPath}
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
                  {directive.value && !isComposite ? (
                    <ValueEditor
                      value={directive.value}
                      onChange={(v) => onChange({ ...directive, value: v })}
                      member={member}
                    />
                  ) : null}
                </div>
              ) : (
                <>
                  {directive.value && !isComposite ? (
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
      {isComposite && directive.value && (
        <CompositeEditor
          value={directive.value}
          onChange={(v) => onChange({ ...directive, value: v })}
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
      // Rendered by CompositeEditor at SetRow level, not inline
      return null;

    default:
      return <span className={styles.setKind}>?</span>;
  }
}

// C# enums are sealed by language: every enum field has exactly one valid
// enum type, taken from the catalog. There's no polymorphic case (unlike
// template references). On commit, we realign the serialised enumType to
// the declared type so saved KDL is canonical (enum="ItemSlot" "..."). When
// the catalog can't supply a type (rare; would mean the field's enum type
// isn't loadable), fall back to whatever the value already carried so we
// don't drop information.
export function resolveEnumCommitType(
  declaredEnumType: string | undefined,
  fallback: string | undefined,
): string | undefined {
  if (declaredEnumType !== undefined && declaredEnumType !== "") return declaredEnumType;
  return fallback;
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

// Decides whether the ref-type combobox should be shown. Hidden for
// monomorphic destinations (catalog supplies a concrete type and modder
// can't pick anything else); visible when:
//  - the catalog couldn't supply a declared type (rare; fallback authoring)
//  - the declared type is an abstract base with multiple concrete subtypes
//    (e.g. RewardTableTemplate.Items → BaseItemTemplate, where the modder
//    must pick ModularVehicleWeaponTemplate / ArmorTemplate / …)
//  - the value already carries an explicit ref type (preserve user intent)
export function shouldShowRefTypeSelector(
  declaredRefType: string,
  isPolymorphic: boolean,
  explicitRefType: string,
): boolean {
  return declaredRefType === "" || isPolymorphic || explicitRefType !== "";
}

// What text the ref-type combobox should display.
//
// Monomorphic case: fall back to the declared concrete type. The selector is
// usually hidden anyway; if shown (because the value already carried an
// explicit type) the declared type is the right idle value.
//
// Polymorphic case: track ONLY the explicit type. Falling back to the
// declared (abstract) type re-fills the combobox after the modder clears it,
// looking like the delete didn't take. The modder must pick a concrete type;
// the abstract base is never a valid display value here.
export function resolveRefTypeDisplay(
  declaredRefType: string,
  isPolymorphic: boolean,
  explicitRefType: string,
): string {
  if (isPolymorphic) return explicitRefType;
  return explicitRefType !== "" ? explicitRefType : declaredRefType;
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
}

function CompositeEditor({ value, onChange }: CompositeEditorProps) {
  const fields = value.compositeFields ?? {};
  const entries = Object.entries(fields);
  const [members, setMembers] = useState<readonly TemplateMember[]>([]);
  const [membersLoaded, setMembersLoaded] = useState(false);

  useEffect(() => {
    if (membersLoaded || !value.compositeType) return;
    void templatesQuery(value.compositeType)
      .then((result) => {
        setMembers(result.members ?? []);
        setMembersLoaded(true);
      })
      .catch(() => setMembersLoaded(true));
  }, [membersLoaded, value.compositeType]);

  const handleFieldChange = (fieldName: string, fieldValue: EditorValue) => {
    onChange({ ...value, compositeFields: { ...fields, [fieldName]: fieldValue } });
  };

  const handleFieldRemove = (fieldName: string) => {
    const { [fieldName]: _removed, ...next } = fields;
    onChange({ ...value, compositeFields: next });
  };

  const handleAddField = (directive: StampedDirective) => {
    // For composite sub-fields, we only use the fieldPath and value
    if (directive.value) {
      onChange({
        ...value,
        compositeFields: { ...fields, [directive.fieldPath]: directive.value },
      });
    }
  };

  const compositeType = value.compositeType ?? "";
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
    if (Object.prototype.hasOwnProperty.call(fields, member.fieldPath)) {
      toast({
        variant: "info",
        message: `"${member.fieldPath}" is already in this composite`,
      });
      return;
    }
    // Same as handleCardDrop — build through the kind-aware helper so the
    // dropped composite sub-field gets the right value shape (ref/enum/etc).
    handleAddField(makeDefaultDirective(synthMemberFromPayload(member)));
  };

  const memberMap = new Map(members.map((m) => [m.name, m]));

  return (
    <div className={styles.compositeBody}>
      <div className={styles.compositeHeader}>
        <span className={styles.compositeType}>{value.compositeType ?? "composite"}</span>
      </div>
      {entries.map(([name, fieldValue]) => (
        <div key={name} className={styles.compositeRow}>
          <span className={styles.compositeFieldName}>{name}</span>
          <div className={styles.compositeFieldValue}>
            <ValueEditor
              value={fieldValue}
              onChange={(v) => handleFieldChange(name, v)}
              member={memberMap.get(name)}
            />
          </div>
          <span className={styles.setKind}>{VALUE_KIND_LABELS[fieldValue.kind]}</span>
          <button
            type="button"
            className={styles.compositeFieldDelete}
            onClick={() => handleFieldRemove(name)}
            title="Remove field"
          >
            <X size={10} />
          </button>
        </div>
      ))}
      <FieldAdder
        members={members}
        membersLoaded={membersLoaded}
        existingFields={Object.keys(fields)}
        targetTemplateType={compositeType}
        onAdd={handleAddField}
        onDrop={handleFieldDrop}
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
}

function FieldAdder({
  members,
  membersLoaded,
  existingFields,
  targetTemplateType,
  onAdd,
  onDrop,
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
      m.name.toLowerCase().includes(lowerQuery),
  );
  const available = filtered.filter((m) => !existingSet.has(m.name));
  const alreadyAdded = filtered.filter((m) => existingSet.has(m.name));

  const handleSelect = (member: TemplateMember) => {
    onAdd(makeDefaultDirective(member));
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
          const duplicate = active?.kind === "member" && existingFields.includes(active.fieldPath);
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
            <button
              key={m.name}
              type="button"
              className={styles.fieldAdderItem}
              onClick={() => handleSelect(m)}
            >
              <span className={styles.fieldAdderItemName}>{m.name}</span>
              <span className={styles.fieldAdderItemType}>{m.typeName}</span>
              {m.isCollection && <span className={styles.fieldAdderItemBadge}>collection</span>}
            </button>
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

function makeDefaultValue(member: TemplateMember): EditorValue {
  const kind = member.patchScalarKind;
  const elementType = member.elementTypeName;

  switch (kind) {
    case "Boolean":
      return { kind: "Boolean", boolean: false };
    case "Byte":
      return { kind: "Byte", int32: 0 };
    case "Int32":
      return { kind: "Int32", int32: 0 };
    case "Single":
      return { kind: "Single", single: 0.0 };
    case "String":
      return { kind: "String", string: "" };
    case "Enum":
      return { kind: "Enum", enumType: elementType ?? member.typeName, enumValue: "" };
    case "TemplateReference":
      // Leave referenceType undefined — the lookup type is implicit and the
      // catalog/loader derive it from the declared field. The modder only
      // sets it explicitly for polymorphic destinations.
      return { kind: "TemplateReference", referenceId: "" };
    default:
      // Non-scalar, non-ref → composite
      if (elementType) {
        return { kind: "Composite", compositeType: elementType, compositeFields: {} };
      }
      if (!member.isScalar && !member.isTemplateReference && member.typeName) {
        return { kind: "Composite", compositeType: member.typeName, compositeFields: {} };
      }
      return { kind: "String", string: "" };
  }
}

function makeDefaultDirective(member: TemplateMember): StampedDirective {
  // NamedArray fields are fixed-size enum-indexed lookups — they only accept
  // `set "Field" index=N <value>`, so default to Set-at-0 and let the picker
  // drive the ordinal. Plain collections default to Append; scalars to Set.
  if (member.namedArrayEnumTypeName) {
    return {
      op: "Set",
      fieldPath: member.name,
      index: 0,
      value: makeScalarDefault(member.patchScalarKind ?? undefined),
      _uiId: uiId(),
    };
  }
  const value = makeDefaultValue(member);
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

function makeScalarDefault(scalarKind?: string): EditorValue {
  switch (scalarKind) {
    case "Boolean":
      return { kind: "Boolean", boolean: false };
    case "Byte":
      return { kind: "Byte", int32: 0 };
    case "Int32":
      return { kind: "Int32", int32: 0 };
    case "Single":
      return { kind: "Single", single: 0.0 };
    case "String":
      return { kind: "String", string: "" };
    default:
      return { kind: "String", string: "" };
  }
}
