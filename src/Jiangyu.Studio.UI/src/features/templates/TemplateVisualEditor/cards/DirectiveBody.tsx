import React, { useCallback, useMemo, useState } from "react";
import { GripVertical, X } from "lucide-react";
import type { InspectedFieldNode, TemplateMember } from "@shared/rpc";
import {
  buildDescentMemberDirective,
  groupDirectives,
  insertAtPendingAnchor,
  isCellAddressedSet,
  rewriteDescentSlotIndex,
  type PendingAnchor,
  type StampedDirective,
  type StampedNode,
} from "../helpers";
import { useDragReorder, useTemplateMembers } from "../hooks";
import { useEditorDispatch, useNodeIndex } from "../store";
import { CommitInput } from "../shared/CommitInput";
import { SuggestionCombobox } from "../shared/SuggestionCombobox";
import { DirectiveRow } from "../rows/SetRow";
import { FieldAdder } from "./FieldAdder";
import styles from "../TemplateVisualEditor.module.css";

// --- DirectiveBody ---
//
// Renders a node's directive list with descent grouping. Walks
// `groupDirectives(node.directives)` and emits a SetRow per loose entry or
// a DescentGroup per consecutive descent run. The group component owns the
// inner FieldAdder and member-edit operations; member directives still live
// in the flat node.directives list, so reorder, parse / serialise round-
// trip, and validation see them exactly as before.

export interface DirectiveBodyProps {
  node: StampedNode;
  members: readonly TemplateMember[];
  membersLoaded: boolean;
  memberMap: Map<string, TemplateMember>;
  vanillaFields: ReadonlyMap<string, InspectedFieldNode>;
  /** Field names whose cell-addressed Set directives are owned by a
   *  matrix grid editor higher in the card. Loose-row rendering skips
   *  those directives so they don't duplicate. */
  matrixFieldNames: ReadonlySet<string>;
  /** Picking an Odin multi-dim member from the FieldAdder dropdown
   *  routes here instead of dispatching an addDirective — the matrix
   *  grid is opt-in UI state, not a serialised directive. */
  onAddMatrix: (name: string) => void;
  handleNodeDrop: (e: React.DragEvent) => void;
}

export function DirectiveBody({
  node,
  members,
  membersLoaded,
  memberMap,
  vanillaFields,
  matrixFieldNames,
  onAddMatrix,
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
          // Cell-addressed Sets are owned by the matrix grid for fields
          // listed in matrixFieldNames; skip them here. The directive
          // stays in node.directives — index arithmetic for groups,
          // pending insertion, and reorder all key off node.directives,
          // so swallowing the row at render time is safe.
          const d = g.directive;
          const isMatrixCellRow = isCellAddressedSet(d) && matrixFieldNames.has(d.fieldPath);
          if (isMatrixCellRow) {
            return renderPendingAfter ? (
              <React.Fragment key={g.directive._uiId}>{renderPending()}</React.Fragment>
            ) : null;
          }
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
        existingMatrixFields={matrixFieldNames}
        targetTemplateType={node.templateType}
        onAdd={onAddDirective}
        onAddMatrix={onAddMatrix}
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

export interface DescentHeaderProps {
  readonly field: string;
  /** Null when the descent step is scalar polymorphic (no collection
   *  index); a number for collection-element descent. The header hides
   *  the "at N" affordance when null. */
  readonly slotIndex: number | null;
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

/**
 * Local-state input for the subtype picker. SuggestionCombobox is controlled,
 * but our DescentHeader caller has no place to hold per-keystroke text: any
 * non-null subtype it observes immediately replaces the picker with a chip,
 * which would commit each character as a (bogus) subtype name. This wrapper
 * holds the typed text in its own state and only invokes onChangeSubtype
 * when the user picks a real entry from the dropdown (via onCommit).
 */
function SubtypePicker({
  fetchSubtypeChoices,
  onChangeSubtype,
}: {
  fetchSubtypeChoices: () => Promise<readonly string[]>;
  onChangeSubtype: (subtype: string | null) => void;
}) {
  const [query, setQuery] = useState("");
  return (
    <SuggestionCombobox
      value={query}
      placeholder="Pick subtype…"
      fetchSuggestions={fetchSubtypeChoices}
      onChange={setQuery}
      onCommit={(v) => onChangeSubtype(v === "" ? null : v)}
    />
  );
}

export function DescentHeader({
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
      {slotIndex !== null && (
        <>
          <span className={styles.setInsertAt}>at</span>
          <CommitInput
            type="number"
            className={styles.setIndexInput}
            value={slotIndex}
            min={0}
            step={1}
            onCommit={(v) => onChangeIndex(Number(v))}
          />
        </>
      )}
      {subtypeMode === "picker" && subtype === null ? (
        <SubtypePicker
          fetchSubtypeChoices={fetchSubtypeChoices}
          onChangeSubtype={onChangeSubtype}
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

export interface DescentGroupProps {
  field: string;
  /** Null = scalar polymorphic descent; number = collection-element descent. */
  slotIndex: number | null;
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
   *  with the same outer field+index, ready for a fresh subtype pick. */
  onConvertToPending?:
    | ((field: string, index: number, startFlatIndex: number, endFlatIndex: number) => void)
    | undefined;
  /** Members of the group as (directive, suffix) pairs. */
  members: { directive: StampedDirective; suffix: string }[];
  /** Pre-rendered member SetRows from the parent. */
  memberRows: React.ReactNode[];
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
  // When innerType is a polymorphic subtype short name (e.g. "Attack" within
  // a SkillEventHandlerTemplate family), pass the outer element type so the
  // resolver can disambiguate against unrelated short-name twins. Suppress
  // for the monomorphic fallback path (innerType === elementTypeName) so we
  // don't waste RPC keys on a no-op context.
  const subtypeElementContext =
    subtype !== null ? (outerMember?.elementTypeName ?? undefined) : undefined;
  const { members: innerMembers, loaded: innerMembersLoaded } = useTemplateMembers(
    innerType || undefined,
    true,
    subtypeElementContext,
  );

  // Members that already have a directive in the group — used to dim the
  // FieldAdder's "scalar already set" entries the same way the outer
  // NodeCard does.
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
    const prefixed = buildDescentMemberDirective(field, slotIndex, subtype, newDirective);
    const next = [...directives.slice(0, endIndex), prefixed, ...directives.slice(endIndex)];
    onSetDirectives(next);
  };

  const handleDeleteGroup = () => {
    onSetDirectives([...directives.slice(0, startIndex), ...directives.slice(endIndex)]);
  };

  const handleChangeSlotIndex = (next: number) => {
    // Scalar polymorphic descent has no index to rewrite; the header
    // hides the affordance, so this should never be called for the
    // null branch. Defensive no-op.
    if (slotIndex === null) return;
    onSetDirectives(
      rewriteDescentSlotIndex(directives, startIndex, endIndex, field, slotIndex, next),
    );
  };

  // Clearing the subtype on a polymorphic group keeps the group's
  // outer (field, index) in place but drops every member directive
  // (each is bound to a subtype-specific field that won't survive a
  // type change) and re-shows the subtype picker.
  const handleClearSubtype = () => {
    // onConvertToPending currently expects a non-null index (its only
    // call site is collection-element descent groups). Scalar descent
    // groups don't expose the clear-subtype affordance, so this
    // doesn't fire for them — defensive guard for the type system.
    if (onConvertToPending && slotIndex !== null) {
      onConvertToPending(field, slotIndex, startIndex, endIndex);
    }
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

export interface PendingDescentGroupProps {
  field: string;
  /** Null = scalar polymorphic descent; number = collection-element descent. */
  slotIndex: number | null;
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
  const subtypeElementContext =
    subtype !== null ? (outerMember?.elementTypeName ?? undefined) : undefined;

  const { members: innerMembers, loaded: innerMembersLoaded } = useTemplateMembers(
    innerType || undefined,
    true,
    subtypeElementContext,
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
