import React, { useCallback, useEffect, useMemo, useRef, useState } from "react";
import { createPortal } from "react-dom";
import { ChevronDown, GripVertical, X } from "lucide-react";
import { parseCrossMemberPayload } from "@features/templates/crossMember";
import { useToastStore } from "@shared/toast";
import { onKeyActivate } from "@shared/utils/a11y";
import { useDismissOnOutsideClick } from "@shared/utils/useDismissOnOutsideClick";
import type { InspectedFieldNode, TemplateMember } from "@shared/rpc";
import type { EditorValue } from "../types";
import { useAnchorPosition } from "../shared/useAnchorPosition";
import {
  SCALAR_OPS,
  OBJECT_OPS,
  COLLECTION_OPS,
  HASHSET_OPS,
  NAMED_ARRAY_OPS,
  OP_LABELS,
  VALUE_KIND_LABELS,
} from "./constants";
import {
  allowsMultipleDirectives,
  directiveForOpChange,
  isFieldBagValue,
  reorderByUiId,
  type StampedDirective,
} from "../helpers";
import { ROW_REORDER_MIME, useDragReorder } from "../hooks";
import { useTemplateMembers } from "../hooks";
import { useCompositeCollapse } from "../store";
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
  // An inline object/struct field (not a primitive scalar, collection, or PPtr
  // reference): Set + Clear both apply (Clear resets to a fresh default).
  const isInlineObject =
    member != null &&
    !member.isScalar &&
    !member.isCollection &&
    !member.isTemplateReference &&
    !member.isAssetReference;
  const [opOpen, setOpOpen] = useState(false);
  const opRef = useRef<HTMLDivElement>(null);
  const opMenuRef = useRef<HTMLDivElement>(null);
  // Portalled so the menu can escape the card's compositor-promoted
  // stacking context; otherwise it gets trapped under the next card.
  const opMenuPosition = useAnchorPosition(opRef, opOpen);

  // The menu lives in a portal so it's NOT a descendant of `opRef`; treat
  // clicks inside it as "inside" too.
  useDismissOnOutsideClick([opRef, opMenuRef], () => setOpOpen(false), { enabled: opOpen });

  const namedArrayEnum = member?.namedArrayEnumTypeName;
  const isHashSet = member?.isOdinHashSet === true;
  const ops = namedArrayEnum
    ? NAMED_ARRAY_OPS
    : isHashSet
      ? HASHSET_OPS
      : isCollection
        ? COLLECTION_OPS
        : isInlineObject
          ? OBJECT_OPS
          : SCALAR_OPS;
  const isRemove = directive.op === "Remove";
  const isClear = directive.op === "Clear";
  const isFieldBag = isFieldBagValue(directive.value);

  // Kind label: hide for remove/clear (no value), hide for field-bag values
  // (Composite / TypeConstruction render their kind inline in the body).
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
            e.dataTransfer.setData(ROW_REORDER_MIME, directive._uiId);
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
                        onChange(
                          directiveForOpChange(directive, op, member, isHashSet, isCollection),
                        );
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
          // _uiId identifies this composite's directive across renders.
          // CompositeEditor passes it to the CompositeCollapseContext so
          // its persisted collapse state survives parses (`_uiId` itself
          // regenerates every parse; the context maps it to a stable
          // positional key).
          directiveUiId={directive._uiId}
          // Three shapes of polymorphic destination route through the same
          // picker UX:
          //  - collection-element subtypes (elementSubtypes, type= flow)
          //  - scalar-polymorphic subtypes (scalarSubtypes, Odin construction)
          //  - tagged-string discriminators (taggedDiscriminators, e.g. SAY
          //    against m_SerializedNodes). For tagged-string, the picker
          //    surfaces vanilla-sampled discriminators and the resolved
          //    inner type is fetched via templatesQuery with the base hint.
          elementSubtypes={member?.elementSubtypes ?? member?.scalarSubtypes ?? null}
          elementType={member?.elementTypeName ?? member?.typeName ?? undefined}
          taggedDiscriminators={member?.taggedDiscriminators ?? null}
          taggedPolymorphicBase={member?.taggedPolymorphicBase ?? undefined}
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
  /** UI identifier of the directive whose value this composite is. Used
   *  to resolve and toggle persisted collapse state via the editor's
   *  CompositeCollapseContext. Omit (e.g. in tests that mount
   *  CompositeEditor directly) and the component falls back to local
   *  React state with no persistence. */
  directiveUiId?: string;
  /** Vanilla composite node from the parent template, when available. Its
   *  `fields` (sub-field nodes) drive vanilla pre-fill for newly-added
   *  sub-fields. Sub-fields nested deeper than one composite level fall back
   *  to neutral defaults (we don't propagate further; the structure is
   *  unbounded and the high-fidelity converter wants member shapes we don't
   *  have at that depth). */
  vanillaNode?: InspectedFieldNode | undefined;
  /** For TypeConstruction values targeting a polymorphic owned-element
   *  collection: the concrete subtypes the modder can pick. Drives the
   *  subtype combobox shown in place of the body until a type is chosen. */
  elementSubtypes?: readonly string[] | null;
  /** Outer collection element-type when this composite sits in a
   *  polymorphic owned-element list (e.g. SkillEventHandlerTemplate for an
   *  EventHandlers slot). Threaded into useTemplateMembers so the resolver
   *  can disambiguate value.compositeType when its short name collides
   *  with an unrelated class outside the subtype family. */
  elementType?: string | undefined;
  /** Tagged-string discriminator candidates from the parent member's
   *  TaggedDiscriminators (vanilla-sampled). Surfaces the same picker UX as
   *  elementSubtypes for the m_Ser*-field authoring shape. */
  taggedDiscriminators?: readonly string[] | null;
  /** Polymorphic base FQN (matches MemberShape.TaggedPolymorphicBase).
   *  Passed to templatesQuery as discriminatorBase so the resolved inner
   *  type's members come back for body rendering. */
  taggedPolymorphicBase?: string | undefined;
}

export function CompositeEditor({
  value,
  onChange,
  directiveUiId,
  vanillaNode,
  elementSubtypes,
  elementType,
  taggedDiscriminators,
  taggedPolymorphicBase,
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
  // Lazy-mount the body: parse-time composites with content default
  // collapsed so a 200-sound SoundBank clone doesn't mount 200
  // FieldAdders + useTemplateMembers hooks + prototype-types effects
  // on initial render. Freshly-added composites (from FieldAdder /
  // HandlerSubtypePicker) start with zero directives and default to
  // expanded so the modder can fill them in without a click.
  //
  // Render state lives locally so a toggle re-renders just this composite
  // (the editor's CompositeCollapseContext value is identity-stable and
  // never re-renders consumers). The control, when present, seeds the
  // initial value from the persisted map and records toggles so the
  // choice survives unmounts (node collapse, virtualised scroll-out,
  // parse reloads — each remount re-seeds because the directive gets a
  // fresh `_uiId` and therefore a fresh component instance). Outside the
  // editor (tests, stand-alone usage) the state is purely local.
  const compositeCollapse = useCompositeCollapse();
  const defaultCollapsed = directives.length > 0;
  const [collapsed, setCollapsed] = useState(() => {
    const persisted =
      compositeCollapse !== null && directiveUiId !== undefined
        ? compositeCollapse.resolveState(directiveUiId)
        : undefined;
    return persisted ?? defaultCollapsed;
  });
  const toggleCollapsed = () => {
    const next = !collapsed;
    setCollapsed(next);
    if (compositeCollapse !== null && directiveUiId !== undefined) {
      compositeCollapse.toggle(directiveUiId, next);
    }
  };
  const isTaggedString = taggedPolymorphicBase != null && taggedPolymorphicBase !== "";
  const { members, loaded: membersLoaded } = useTemplateMembers(
    value.compositeType,
    !collapsed,
    elementType,
    // Tagged-string composites: the modder's compositeType is a
    // discriminator (e.g. "SAY"), not a CLR type name. Pass the
    // polymorphic base so the server-side query resolves it to the
    // concrete CLR type before walking members.
    isTaggedString ? taggedPolymorphicBase : undefined,
  );

  // Whether this composite type has a working `from=` prototype lookup
  // on the server. Server-driven: types absent from the registry get no
  // from= input. The set is fetched once and cached for the editor session.
  // Gated on !collapsed because the from= affordance only renders inside
  // the expanded body, and the cached promise still wakes one setState
  // per CompositeEditor instance otherwise.
  const fromLookupType = value.compositeType ?? elementType ?? "";
  const [supportedSet, setSupportedSet] = useState<ReadonlySet<string> | null>(null);
  useEffect(() => {
    if (collapsed) return;
    if (!fromLookupType) return;
    let cancelled = false;
    void templatesPrototypeSupportedTypes().then((s) => {
      if (!cancelled) setSupportedSet(s);
    });
    return () => {
      cancelled = true;
    };
  }, [collapsed, fromLookupType]);
  const prototypesSupported =
    fromLookupType !== "" &&
    supportedSet?.has(normaliseCompositeTypeShortName(fromLookupType)) === true;

  // Track whether the modder has manually opened the from= picker. Open
  // implicitly when compositeFrom is already set (parsed from KDL), so
  // an existing from= clause stays visible without an extra click.
  const [fromInputOpen, setFromInputOpen] = useState(false);
  const fromInputVisible =
    fromInputOpen || (value.compositeFrom != null && value.compositeFrom !== "");

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
    const next = reorderByUiId(directives, fromId, toSlot);
    if (next === directives) return;
    onChange({ ...value, compositeDirectives: next });
  };

  const reorder = useDragReorder(handleRowReorder, ROW_REORDER_MIME);

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

  // Memoised so the inner DirectiveRows' member lookups keep a stable map
  // identity across re-renders that don't change the member list.
  const memberMap = useMemo(() => new Map(members.map((m) => [m.name, m])), [members]);

  // Memoised so SuggestionCombobox's "fetchSuggestions changed → drop
  // cache and refetch" reset doesn't fire on every parent re-render.
  const fetchFromCandidates = useCallback(
    () => templatesPrototypeCandidates(fromLookupType),
    [fromLookupType],
  );

  const isConstruction = value.kind === "TypeConstruction";
  // Tagged-string composites surface their discriminators through the
  // same picker UX as construction subtypes. Modder picks once; the chip
  // round-trips and clearing re-opens the picker.
  const subtypeChoices = isTaggedString
    ? (taggedDiscriminators ?? null)
    : (elementSubtypes ?? null);
  const needsSubtypePick =
    (isConstruction || isTaggedString) &&
    subtypeChoices !== null &&
    subtypeChoices.length > 0 &&
    !value.compositeType;

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
    (isConstruction || isTaggedString) &&
    subtypeChoices !== null &&
    subtypeChoices.length > 0 &&
    !!value.compositeType;

  return (
    <div className={styles.compositeBody}>
      <div
        className={styles.compositeHeader}
        role="button"
        tabIndex={0}
        aria-expanded={!collapsed}
        onClick={toggleCollapsed}
        onKeyDown={onKeyActivate(toggleCollapsed)}
        title={collapsed ? "Expand composite" : "Collapse composite"}
      >
        <span
          className={`${styles.compositeExpander} ${collapsed ? "" : styles.compositeExpanderOpen}`}
          aria-hidden
        >
          <ChevronDown size={10} />
        </span>
        <span className={styles.compositeKind}>{isConstruction ? "construct" : "composite"}</span>
        {canClearSubtype ? (
          // Clickable subtype chip — clicking clears compositeType + fields
          // and re-shows the picker. Hover restyles to telegraph the action;
          // a separate X button next to it tested poorly (small target, easy
          // to miss; modders kept clearing the input by accident instead).
          // stopPropagation so the parent header's collapse-toggle handler
          // doesn't fire on the same click.
          <button
            type="button"
            className={styles.compositeTypeClickable}
            onClick={(e) => {
              e.stopPropagation();
              handleClearSubtype();
            }}
            title="Clear type (resets fields)"
          >
            {value.compositeType}
          </button>
        ) : (
          <span className={styles.compositeType}>
            {value.compositeType ?? (isConstruction ? "construct" : "composite")}
          </span>
        )}
        {collapsed && directives.length > 0 && (
          <span className={styles.compositeCount}>
            {directives.length} field{directives.length === 1 ? "" : "s"}
          </span>
        )}
        {!collapsed &&
          prototypesSupported &&
          (fromInputVisible ? (
            // stopPropagation on the wrapping div so picker interactions
            // (typing, clicking suggestions, the × clear button) don't
            // bubble up to the compositeHeader's collapse toggle.
            <div
              className={styles.setValue}
              role="presentation"
              onClick={(e) => e.stopPropagation()}
              onKeyDown={(e) => e.stopPropagation()}
            >
              <div className={styles.setInsertRow}>
                <span className={styles.setInsertAt}>From</span>
                <div className={styles.compositeFromCombo}>
                  <SuggestionCombobox
                    value={value.compositeFrom ?? ""}
                    placeholder=""
                    fetchSuggestions={fetchFromCandidates}
                    onChange={(next) => {
                      const { compositeFrom: _drop, ...rest } = value;
                      onChange(next === "" ? rest : { ...rest, compositeFrom: next });
                    }}
                  />
                </div>
                <button
                  type="button"
                  className={styles.compositeFromAdd}
                  onClick={(e) => {
                    e.stopPropagation();
                    const { compositeFrom: _drop, ...rest } = value;
                    onChange(rest);
                    setFromInputOpen(false);
                  }}
                  title="Remove from= (this composite will construct fresh defaults)"
                >
                  ×
                </button>
              </div>
            </div>
          ) : (
            // Collapsed affordance: avoids the visual noise of an
            // always-present empty input on every composite whose type
            // happens to support prototype-source lookup. Clicking
            // expands the picker without committing a value.
            <button
              type="button"
              className={styles.compositeFromAdd}
              onClick={(e) => {
                e.stopPropagation();
                setFromInputOpen(true);
              }}
              title="Inherit Inspector-baked defaults from an existing element"
            >
              + from
            </button>
          ))}
      </div>
      {!collapsed && (
        <>
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
          {reorder.showIndicatorAt(directives.length, null) && (
            <div className={styles.dropIndicator} />
          )}
          <FieldAdder
            members={members}
            membersLoaded={membersLoaded}
            existingFields={existingFieldNames}
            targetTemplateType={compositeType}
            onAdd={handleAddDirective}
            onDrop={handleFieldDrop}
            vanillaFields={vanillaSubFields}
          />
        </>
      )}
    </div>
  );
}
