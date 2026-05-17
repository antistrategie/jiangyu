import React, { useCallback, useEffect, useMemo, useRef, useState } from "react";
import { createPortal } from "react-dom";
import { ChevronDown, GripVertical, X } from "lucide-react";
import { parseCrossMemberPayload } from "@features/templates/crossMember";
import { useToastStore } from "@shared/toast";
import type { InspectedFieldNode, TemplateMember } from "@shared/rpc";
import type { EditorValue } from "../types";
import { useAnchorPosition } from "../shared/useAnchorPosition";
import {
  SCALAR_OPS,
  COLLECTION_OPS,
  HASHSET_OPS,
  NAMED_ARRAY_OPS,
  OP_LABELS,
  VALUE_KIND_LABELS,
} from "./constants";
import {
  allowsMultipleDirectives,
  isFieldBagValue,
  makeDefaultValue,
  type StampedDirective,
} from "../helpers";
import { useDragReorder } from "../hooks";
import { useTemplateMembers } from "../hooks";
import { CommitInput } from "../shared/CommitInput";
import { SuggestionCombobox } from "../shared/SuggestionCombobox";
import { ValueEditor, NamedArrayIndexPicker } from "./ValueEditor";
import { FieldAdder } from "../cards/FieldAdder";
import {
  makeDefaultDirective,
  normaliseCompositeTypeShortName,
  synthMemberFromPayload,
  templatesPrototypeCandidates,
  templatesPrototypeSupportedTypes,
} from "../shared/rpcHelpers";
import styles from "../TemplateVisualEditor.module.css";

// --- DirectiveRow ---
//
// Renders one stamped directive as a SetRow with its drag indicator and
// reorder handlers wired through `reorder`. Used by DirectiveBody (loose
// rows + group members), CompositeEditor (composite body rows), and any
// future directive-list site so the rendering and drag wiring stays in
// one place. Caller threads onChange/onDelete in by flatIndex so the
// owning list can splice in / out at the right slot.

export interface DirectiveRowProps {
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

export function DirectiveRow({
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

// --- SetRow ---

export interface SetRowProps {
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

export function SetRow({
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
  const opMenuRef = useRef<HTMLDivElement>(null);
  // Portalled so the menu can escape the card's compositor-promoted
  // stacking context; otherwise it gets trapped under the next card.
  const opMenuPosition = useAnchorPosition(opRef, opOpen);

  useEffect(() => {
    if (!opOpen) return;
    const handler = (e: MouseEvent) => {
      const target = e.target as Node;
      if (opRef.current?.contains(target)) return;
      if (opMenuRef.current?.contains(target)) return;
      setOpOpen(false);
    };
    document.addEventListener("mousedown", handler);
    return () => document.removeEventListener("mousedown", handler);
  }, [opOpen]);

  const namedArrayEnum = member?.namedArrayEnumTypeName;
  const isHashSet = member?.isOdinHashSet === true;
  const ops = namedArrayEnum
    ? NAMED_ARRAY_OPS
    : isHashSet
      ? HASHSET_OPS
      : isCollection
        ? COLLECTION_OPS
        : SCALAR_OPS;
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
            {opOpen &&
              opMenuPosition &&
              createPortal(
                <div
                  className={styles.setOpMenu}
                  ref={opMenuRef}
                  style={{
                    position: "fixed",
                    top: opMenuPosition.top,
                    left: opMenuPosition.left,
                    zIndex: "var(--z-portal)",
                  }}
                >
                  {ops.map((op) => (
                    <button
                      key={op}
                      type="button"
                      className={`${styles.setOpMenuItem} ${op === directive.op ? styles.setOpMenuItemActive : ""}`}
                      onClick={() => {
                        // Clear drops both index and value. Remove drops the
                        // value on List<T> destinations (index-based) but
                        // KEEPS the value on HashSet destinations (by-value
                        // removal). Other ops keep / synthesise a value and
                        // set a default index when the op needs one.
                        const updated: StampedDirective =
                          op === "Clear"
                            ? {
                                op,
                                fieldPath: directive.fieldPath,
                                _uiId: directive._uiId,
                              }
                            : op === "Remove" && isHashSet
                              ? {
                                  op,
                                  fieldPath: directive.fieldPath,
                                  value: directive.value ?? makeDefaultValue(member),
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
                        // Index defaulting only applies when the op
                        // genuinely uses an index (List Insert / Remove /
                        // Set-with-index). HashSet Remove is value-based
                        // and must not get a phantom index=0.
                        const opNeedsIndex =
                          op === "Insert" || op === "Set" || (op === "Remove" && !isHashSet);
                        if (opNeedsIndex && updated.index === undefined) updated.index = 0;
                        onChange(updated);
                        setOpOpen(false);
                      }}
                    >
                      {OP_LABELS[op]}
                    </button>
                  ))}
                </div>,
                document.body,
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
              {isHashSet ? (
                // HashSet Remove is by-value; show the value editor and
                // skip the "at" + index input the List path uses.
                directive.value ? (
                  <ValueEditor
                    value={directive.value}
                    onChange={(v) => onChange({ ...directive, value: v })}
                    member={member}
                  />
                ) : null
              ) : (
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
              )}
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
                      entries={member.enumMembers}
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
          // Both shapes of polymorphic destination route through the same
          // picker UX: collection-element subtypes (elementSubtypes) and
          // scalar-polymorphic subtypes (scalarSubtypes, the Phase 2b
          // Odin-construction case).
          elementSubtypes={member?.elementSubtypes ?? member?.scalarSubtypes ?? null}
          elementType={member?.elementTypeName ?? member?.typeName ?? undefined}
        />
      )}
    </div>
  );
}

// --- HandlerSubtypePicker ---
//
// Dedicated "must pick from list" combobox for the polymorphic handler
// subtype. The shared SuggestionCombobox is a free-form-with-suggestions
// input by default (every keystroke fires onChange) which is right for
// callers binding to a real value field. Here typed text is transient and
// only selecting a real subtype should commit, so we own the typed-text
// state locally and pull commits via onCommit.

export interface HandlerSubtypePickerProps {
  readonly subtypeChoices: readonly string[];
  readonly onPick: (picked: string) => void;
}

export function HandlerSubtypePicker({ subtypeChoices, onPick }: HandlerSubtypePickerProps) {
  const [typed, setTyped] = useState("");
  // Memoise the fetcher so SuggestionCombobox's "fetchSuggestions changed →
  // drop cache and refetch" branch doesn't fire on every keystroke.
  const fetchSuggestions = useCallback(() => Promise.resolve(subtypeChoices), [subtypeChoices]);
  return (
    <div className={styles.compositeBody}>
      <div className={styles.compositeHeader}>
        <span className={styles.compositeKind}>handler</span>
        <SuggestionCombobox
          value={typed}
          placeholder="Pick handler type…"
          fetchSuggestions={fetchSuggestions}
          onChange={setTyped}
          onCommit={(picked) => {
            setTyped("");
            onPick(picked);
          }}
        />
      </div>
    </div>
  );
}

// --- CompositeEditor ---

export interface CompositeEditorProps {
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
  /** Outer collection element-type when this composite sits in a
   *  polymorphic owned-element list (e.g. SkillEventHandlerTemplate for an
   *  EventHandlers slot). Threaded into useTemplateMembers so the resolver
   *  can disambiguate value.compositeType when its short name collides
   *  with an unrelated class outside the subtype family. */
  elementType?: string | undefined;
}

export function CompositeEditor({
  value,
  onChange,
  vanillaNode,
  elementSubtypes,
  elementType,
}: CompositeEditorProps) {
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
  const { members, loaded: membersLoaded } = useTemplateMembers(
    value.compositeType,
    true,
    elementType,
  );

  // Whether this composite type has a working `from=` prototype lookup
  // on the server. Server-driven: types absent from the registry get no
  // from= input. The set is fetched once and cached for the editor session.
  const fromLookupType = value.compositeType ?? elementType ?? "";
  const [supportedSet, setSupportedSet] = useState<ReadonlySet<string> | null>(null);
  useEffect(() => {
    if (!fromLookupType) return;
    let cancelled = false;
    void templatesPrototypeSupportedTypes().then((s) => {
      if (!cancelled) setSupportedSet(s);
    });
    return () => {
      cancelled = true;
    };
  }, [fromLookupType]);
  const prototypesSupported =
    fromLookupType !== "" &&
    supportedSet?.has(normaliseCompositeTypeShortName(fromLookupType)) === true;

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
      <HandlerSubtypePicker
        subtypeChoices={subtypeChoices}
        onPick={(picked) => onChange({ ...value, compositeType: picked, compositeDirectives: [] })}
      />
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
        {!isHandler && prototypesSupported && (
          <div className={styles.setValue}>
            <div className={styles.setInsertRow}>
              <span className={styles.setInsertAt}>From</span>
              <div className={styles.compositeFromCombo}>
                <SuggestionCombobox
                  value={value.compositeFrom ?? ""}
                  placeholder=""
                  fetchSuggestions={() => templatesPrototypeCandidates(fromLookupType)}
                  onChange={(next) => {
                    const { compositeFrom: _drop, ...rest } = value;
                    onChange(next === "" ? rest : { ...rest, compositeFrom: next });
                  }}
                />
              </div>
            </div>
          </div>
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
