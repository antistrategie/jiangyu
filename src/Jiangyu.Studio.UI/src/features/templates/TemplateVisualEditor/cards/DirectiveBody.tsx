import React, { useCallback, useLayoutEffect, useMemo, useRef, useState } from "react";
import { GripVertical, X } from "lucide-react";
import { useVirtualizer } from "@tanstack/react-virtual";
import type { InspectedFieldNode, TemplateMember } from "@shared/rpc";
import {
  buildDescentMemberDirective,
  groupDirectives,
  insertAtPendingAnchor,
  isCellAddressedSet,
  resolveInspectedSlotType,
  rewriteDescentSlotIndex,
  type PendingAnchor,
  type StampedDirective,
  type StampedNode,
} from "../helpers";
import { ROW_REORDER_MIME, useDragReorder, useTemplateMembers } from "../hooks";
import { useEditorDispatch, useEditorScrollContainer, useNodeIndex } from "../store";
import { CommitInput } from "../shared/CommitInput";
import { SuggestionCombobox } from "../shared/SuggestionCombobox";
import { DirectiveRow } from "../rows/SetRow";
import { FieldAdder } from "./FieldAdder";
import styles from "../TemplateVisualEditor.module.css";

// Virtualisation kicks in when a node has at least this many directive
// groups. Below that, the cost of measureElement + absolute positioning
// outweighs the saving on mounting full rows. Threshold picked to keep
// typical hand-authored patches (a handful of fields) on the simple
// flow-layout path while auto-generated clones (voicelines.kdl: 374
// directives) use the windowed path.
const VIRTUALISE_THRESHOLD = 50;

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
  const reorder = useDragReorder(
    (fromId, toSlot) => dispatch({ type: "reorderRows", nodeIndex, fromId, toSlot }),
    ROW_REORDER_MIME,
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

  // Pending descent group: UI-only state for an "Edit slot…" pick from the
  // top-level FieldAdder, before its first member directive is committed.
  // Empty groups can't exist in the data model, so this state holds the
  // field+index (plus a view-only subtype that drives the field picker)
  // until the first member is added, then we materialise the directive and
  // dismiss pending. Pending renders at the end of the list (anchor = "end").
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

  const handleMaterialisePending = (newDirective: StampedDirective) => {
    if (!pending) return;
    const prefixed = buildDescentMemberDirective(pending.field, pending.index, newDirective);
    onSetDirectives(insertAtPendingAnchor(node.directives, prefixed, pending.anchor));
    setPending(null);
  };

  const looseRow = (
    d: StampedDirective,
    flatIndex: number,
    displayFieldPath?: string,
    overrideMemberMap?: Map<string, TemplateMember>,
  ): React.ReactNode => (
    <DirectiveRow
      key={d._uiId}
      directive={d}
      flatIndex={flatIndex}
      displayFieldPath={displayFieldPath}
      memberMap={overrideMemberMap ?? memberMap}
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
        inspectedSlotType={resolveInspectedSlotType(vanillaFields, pending.field, pending.index)}
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

  // Render one group (loose row or descent group) by index. Used by both
  // the flow-layout path and the virtualised path so the per-group
  // rendering stays in one place. `embedPendingAfter` lets the flow-
  // layout caller inline `renderPending()` directly after a group when
  // its anchor points at that group; the virtualised path ignores it
  // (it bails out entirely when pending is non-null).
  const renderGroupAt = (gi: number, embedPendingAfter: boolean): React.ReactNode => {
    const g = groups[gi];
    if (!g) return null;
    const startIndex = groupStartIndices[gi] ?? 0;
    const endIndex = startIndex + (g.kind === "loose" ? 1 : g.members.length);
    if (g.kind === "loose") {
      const d = g.directive;
      const isMatrixCellRow = isCellAddressedSet(d) && matrixFieldNames.has(d.fieldPath);
      if (isMatrixCellRow) {
        return embedPendingAfter ? (
          <React.Fragment key={g.directive._uiId}>{renderPending()}</React.Fragment>
        ) : null;
      }
      return (
        <React.Fragment key={g.directive._uiId}>
          {looseRow(g.directive, startIndex)}
          {embedPendingAfter && renderPending()}
        </React.Fragment>
      );
    }
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
          inspectedSlotType={resolveInspectedSlotType(vanillaFields, g.field, g.index)}
          startIndex={startIndex}
          endIndex={endIndex}
          outerMember={memberMap.get(g.field)}
          directives={node.directives}
          onSetDirectives={onSetDirectives}
          members={g.members}
          renderMemberRow={looseRow}
          isDragging={groupHandlers.isDragging}
          onDragStart={groupHandlers.onDragStart}
          onDragEnd={groupHandlers.onDragEnd}
          onDragOverRow={groupHandlers.onDragOver}
          onDropRow={groupHandlers.onDrop}
        />
        {embedPendingAfter && renderPending()}
      </React.Fragment>
    );
  };

  const fieldAdder = (
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
  );

  // --- Virtualised path ---
  //
  // For huge clones (auto-generated voicelines.kdl: 374 directives), the
  // simple flow render mounts every SetRow + FieldAdder + useTemplateMembers
  // hook synchronously and freezes WebKitGTK on initial parse. Windowing
  // the group list keeps the per-render cost bounded to the visible region
  // plus overscan. The path bails out when pending is non-null because the
  // "pending-after-flat-index" placement can land between virtual rows that
  // aren't even mounted; the pending lifecycle is short and uncommon, so
  // taking the flow path during it is fine. The path also bails when the
  // scroll container ref isn't available (e.g. tests, no editor wrapping).
  const scrollContainerRef = useEditorScrollContainer();
  const listRef = useRef<HTMLDivElement>(null);
  const [scrollMargin, setScrollMargin] = useState(0);
  const useVirtualised =
    pending === null && scrollContainerRef !== null && groups.length >= VIRTUALISE_THRESHOLD;

  // Re-measure the offset between the scroll container's top and our
  // virtual list's top on every render. Cards above us can expand /
  // collapse / be added / removed without firing a ResizeObserver entry
  // on either the scroll container or our own list, but each of those
  // mutations originates from a state update in this editor's reducer,
  // which re-renders every node card including this one. Per-render
  // remeasure is the conservative thing. One getBoundingClientRect call
  // is cheap relative to the cost we save by virtualising. The
  // setScrollMargin call is no-op (returns prev) when the position
  // hasn't shifted, so this doesn't drive an infinite render loop.
  // eslint-disable-next-line react-hooks/exhaustive-deps, @eslint-react/exhaustive-deps -- intentional per-render remeasure; see comment above.
  useLayoutEffect(() => {
    if (!useVirtualised) return;
    // useVirtualised already includes scrollContainerRef !== null, so TS
    // narrows it to RefObject<...> in this branch.
    const scrollEl = scrollContainerRef.current;
    const listEl = listRef.current;
    if (!scrollEl || !listEl) return;
    const next = Math.max(
      0,
      listEl.getBoundingClientRect().top -
        scrollEl.getBoundingClientRect().top +
        scrollEl.scrollTop,
    );
    setScrollMargin((prev) => (Math.abs(prev - next) < 0.5 ? prev : next));
  });

  // eslint-disable-next-line react-hooks/incompatible-library -- TanStack Virtual returns non-memoisable functions; the only API the library exposes.
  const virtualizer = useVirtualizer({
    count: groups.length,
    getScrollElement: () => scrollContainerRef?.current ?? null,
    estimateSize: () => 40,
    overscan: 10,
    scrollMargin,
  });

  if (useVirtualised) {
    const virtualItems = virtualizer.getVirtualItems();
    return (
      <>
        <div
          ref={listRef}
          style={{ height: `${virtualizer.getTotalSize()}px`, position: "relative" }}
        >
          {virtualItems.map((vRow) => {
            const g = groups[vRow.index];
            if (!g) return null;
            // Same keying as the flow path: groups key by their first
            // member's uiId, so two non-adjacent groups on one field
            // can't collide and crossing the windowing threshold keeps
            // each group's identity.
            const key =
              g.kind === "loose"
                ? g.directive._uiId
                : (g.members[0]?.directive._uiId ?? `group-${g.field}-${g.index}`);
            return (
              <div
                key={key}
                ref={virtualizer.measureElement}
                data-index={vRow.index}
                style={{
                  position: "absolute",
                  top: 0,
                  left: 0,
                  right: 0,
                  transform: `translateY(${vRow.start - scrollMargin}px)`,
                }}
              >
                {renderGroupAt(vRow.index, false)}
              </div>
            );
          })}
        </div>
        {reorder.showIndicatorAt(node.directives.length, null) && (
          <div className={styles.dropIndicator} />
        )}
        {fieldAdder}
      </>
    );
  }

  return (
    <>
      {pendingAtStart && renderPending()}
      {groups.map((g, gi) => {
        const start = groupStartIndices[gi] ?? 0;
        const end = start + (g.kind === "loose" ? 1 : g.members.length);
        return renderGroupAt(gi, pendingAfterFlatIndex === end - 1);
      })}
      {reorder.showIndicatorAt(node.directives.length, null) && (
        <div className={styles.dropIndicator} />
      )}
      {pendingAtEnd && renderPending()}
      {fieldAdder}
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
  /** Collection-element index; null for an object-field edit (hides "at N"). */
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
  /** Collection-element index this group edits; null for an object-field edit. */
  slotIndex: number | null;
  /** Concrete type of the live element/object at this slot, read from the
   *  inspected source template. Drives the field list (a descent carries no
   *  type=) and shows read-only as the edited target's type. */
  inspectedSlotType: string | null;
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
  /** Members of the group as (directive, suffix) pairs. */
  members: { directive: StampedDirective; suffix: string }[];
  /** Row renderer from the parent. Accepts an override memberMap so the
   *  inner descent rows resolve their member info against the descent
   *  target's members rather than the outer template's. */
  renderMemberRow: (
    directive: StampedDirective,
    flatIndex: number,
    displayFieldPath?: string,
    overrideMemberMap?: Map<string, TemplateMember>,
  ) => React.ReactNode;
  isDragging: boolean;
  onDragStart: () => void;
  onDragEnd: () => void;
  onDragOverRow: (e: React.DragEvent) => void;
  onDropRow: () => void;
}

function DescentGroup({
  field,
  slotIndex,
  inspectedSlotType,
  startIndex,
  endIndex,
  outerMember,
  directives,
  onSetDirectives,
  members,
  renderMemberRow,
  isDragging,
  onDragStart,
  onDragEnd,
  onDragOverRow,
  onDropRow,
}: DescentGroupProps) {
  // Inner-type members for the FieldAdder. A descent is an edit and carries no
  // type=, so resolve the live slot's concrete type from inspection to offer
  // that subtype's fields. Fall back to the outer member's element type for a
  // monomorphic owned-element list (e.g. List<PropertyChange>) or when the
  // slot can't be inspected.
  const innerType = inspectedSlotType ?? outerMember?.elementTypeName ?? "";
  // When innerType is a polymorphic subtype name (e.g. "Attack" within a
  // SkillEventHandlerTemplate family), pass the outer element type so the
  // resolver can disambiguate against unrelated short-name twins. Suppress for
  // the monomorphic fallback path so we don't waste RPC keys on a no-op
  // context.
  const subtypeElementContext =
    inspectedSlotType !== null ? (outerMember?.elementTypeName ?? undefined) : undefined;
  const { members: innerMembers, loaded: innerMembersLoaded } = useTemplateMembers(
    innerType || undefined,
    true,
    subtypeElementContext,
  );

  // Member-by-name lookup over the descent target's own members. Inner
  // SetRows resolve their per-field schema (referenceTypeName, enumTypeName,
  // …) against this map instead of the outer template's, so a monomorphic
  // ref field like Perk.Skill correctly hides the type selector.
  const innerMemberMap = useMemo(() => {
    const map = new Map<string, TemplateMember>();
    for (const m of innerMembers) map.set(m.name, m);
    return map;
  }, [innerMembers]);

  const memberRows = members.map((m, mi) =>
    renderMemberRow(m.directive, startIndex + mi, m.suffix, innerMemberMap),
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
    const prefixed = buildDescentMemberDirective(field, slotIndex, newDirective);
    const next = [...directives.slice(0, endIndex), prefixed, ...directives.slice(endIndex)];
    onSetDirectives(next);
  };

  const handleDeleteGroup = () => {
    onSetDirectives([...directives.slice(0, startIndex), ...directives.slice(endIndex)]);
  };

  const handleChangeSlotIndex = (next: number) => {
    // Object-field edits have no index to rewrite; the header hides the
    // affordance, so this only fires for collection-element groups.
    if (slotIndex === null) return;
    onSetDirectives(
      rewriteDescentSlotIndex(directives, startIndex, endIndex, field, slotIndex, next),
    );
  };

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
        subtype={inspectedSlotType}
        subtypeChoices={null}
        subtypeMode="fixed"
        onChangeIndex={handleChangeSlotIndex}
        onChangeSubtype={() => {}}
        dragBinding={{
          onDragStart: (e) => {
            e.stopPropagation();
            e.dataTransfer.effectAllowed = "move";
            // Same payload key per-row grips use; the parent's reorder
            // helper recognises group-head ids and moves all K members.
            e.dataTransfer.setData(ROW_REORDER_MIME, firstMemberId);
            onDragStart();
          },
          onDragEnd,
          payloadId: firstMemberId,
          title: "Drag to reorder group",
        }}
        onClose={handleDeleteGroup}
        closeTitle="Remove descent group (deletes all member directives)"
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
  /** Zero-based element index this pending group will edit. */
  slotIndex: number;
  subtype: string | null;
  /** Concrete type of the live element at this slot, read from inspection.
   *  Auto-fills the field list so editing a polymorphic slot needs no pick. */
  inspectedSlotType: string | null;
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
  inspectedSlotType,
  outerMember,
  onChangeIndex,
  onChangeSubtype,
  onCancel,
  onAddFirstMember,
}: PendingDescentGroupProps) {
  // Inner type resolution mirrors DescentGroup's: the live slot's inspected
  // type auto-fills the field list, a manual subtype pick overrides it, and
  // the outer element type is the monomorphic fallback. The FieldAdder is
  // gated only when none of these resolve a type on a polymorphic list.
  const subtypeChoices = outerMember?.elementSubtypes ?? null;
  const isPolymorphic = subtypeChoices !== null && subtypeChoices.length > 0;
  const resolvedInner = subtype ?? inspectedSlotType;
  const innerType = resolvedInner ?? outerMember?.elementTypeName ?? "";
  const subtypeElementContext =
    resolvedInner !== null ? (outerMember?.elementTypeName ?? undefined) : undefined;

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
        subtype={subtype ?? inspectedSlotType}
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
        {isPolymorphic && resolvedInner === null ? (
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
