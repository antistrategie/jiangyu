import React, { useCallback, useMemo, useRef, useState } from "react";
import { ChevronDown, GripVertical, X } from "lucide-react";
import { parseCrossMemberPayload } from "@lib/drag/crossMember";
import { useToastStore } from "@lib/toast";
import { onKeyActivate } from "@lib/ui/a11y";
import { allowsMultipleDirectives, isCellAddressedSet, type StampedNode } from "../helpers";
import { useTemplateMembers } from "../hooks";
import { useEditorDispatch, useNodeIndex } from "../store";
import { CommitInput } from "../shared/CommitInput";
import { SuggestionCombobox, type SuggestionItem } from "../shared/SuggestionCombobox";
import {
  getCachedTemplateTypes,
  getCachedProjectClones,
  templatesSearch,
  useVanillaFields,
  makeDefaultDirective,
  synthMemberFromPayload,
} from "../shared/rpcHelpers";
import { DirectiveBody } from "./DirectiveBody";
import { MatrixFieldEditor } from "./MatrixEditor";
import styles from "../TemplateVisualEditor.module.css";

export interface NodeCardProps {
  node: StampedNode;
  collapsed: boolean;
  onToggleCollapse: () => void;
  isDragging: boolean;
  onDragStart: () => void;
  onDragEnd: () => void;
  onDragOverCard: (e: React.DragEvent) => void;
  onDropCard: () => void;
}

export function NodeCard({
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
  const vanillaTargetId = isPatch ? node.templateId : node.sourceId;
  const vanillaFields = useVanillaFields(node.templateType, vanillaTargetId);

  const memberMap = useMemo(() => new Map(members.map((m) => [m.name, m])), [members]);

  // UI-only opt-in for matrix grids. Picking an Odin multi-dim member
  // from FieldAdder or dropping one onto the card adds it here so the
  // grid surfaces immediately, even before the modder has clicked any
  // cells. Cleared by the matrix grid's X button along with any cell
  // directives. Not serialised — a saved patch with no cell directives
  // intentionally has no grid on reload.
  const [addedMatrixFields, setAddedMatrixFields] = useState<ReadonlySet<string>>(
    () => new Set<string>(),
  );

  // Field names rendered as matrix grids: only fields that have at least
  // one cell-addressed Set directive OR were explicitly opted in. We
  // deliberately do NOT auto-surface every cataloged Odin multi-dim
  // member — empty grids on every PerkTemplate would be visual noise.
  const matrixFieldNames = useMemo(() => {
    const set = new Set<string>(addedMatrixFields);
    for (const d of node.directives) {
      if (isCellAddressedSet(d)) set.add(d.fieldPath);
    }
    return set;
  }, [addedMatrixFields, node.directives]);

  const addMatrixField = useCallback((name: string) => {
    setAddedMatrixFields((prev) => {
      if (prev.has(name)) return prev;
      const next = new Set(prev);
      next.add(name);
      return next;
    });
  }, []);

  const removeMatrixField = useCallback(
    (name: string) => {
      setAddedMatrixFields((prev) => {
        if (!prev.has(name)) return prev;
        const next = new Set(prev);
        next.delete(name);
        return next;
      });
      const filtered = node.directives.filter(
        (d) => !(isCellAddressedSet(d) && d.fieldPath === name),
      );
      if (filtered.length !== node.directives.length) {
        dispatch({ type: "setDirectives", nodeIndex, directives: filtered });
      }
    },
    [node.directives, dispatch, nodeIndex],
  );

  // Card-level drop: accept member drags to add set directives.
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
      // Odin multi-dim members surface as a matrix grid, not a regular
      // SetRow. Drop one onto the card and we opt-in instead of
      // dispatching a directive (the modder still has to click cells).
      const catalogMember = memberMap.get(member.fieldPath);
      if (catalogMember?.isOdinMultiDimArray) {
        addMatrixField(member.fieldPath);
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
    [node, vanillaFields, memberMap, addMatrixField, dispatch, nodeIndex],
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
          {/* Matrix grid editors for Odin-routed multi-dim array fields
              (AOETiles, ChunkTileFlags, …). Opt-in via FieldAdder /
              card-drop / an existing cell directive — empty grids on
              every card would be visual noise. Each cell click adds,
              updates, or removes a Set directive with cell="r,c"
              addressing. */}
          {Array.from(matrixFieldNames).map((fieldName) => {
            const vanilla = vanillaFields.get(fieldName);
            const matrix = vanilla?.kind === "matrix" ? vanilla : null;
            return (
              <MatrixFieldEditor
                key={fieldName}
                fieldName={fieldName}
                matrix={matrix}
                member={memberMap.get(fieldName) ?? null}
                directives={node.directives}
                onRemove={() => removeMatrixField(fieldName)}
              />
            );
          })}
          <DirectiveBody
            node={node}
            members={members}
            membersLoaded={membersLoaded}
            memberMap={memberMap}
            vanillaFields={vanillaFields}
            matrixFieldNames={matrixFieldNames}
            onAddMatrix={addMatrixField}
            handleNodeDrop={handleNodeDrop}
          />
        </div>
      )}
    </div>
  );
}
