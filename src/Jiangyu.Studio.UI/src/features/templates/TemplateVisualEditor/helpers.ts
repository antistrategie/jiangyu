// Pure helpers extracted from TemplateVisualEditor.tsx so the JSX module
// only exports React components — keeps Vite fast-refresh working and
// gives the unit tests a stable, side-effect-free import surface.

import type { InspectedFieldNode, TemplateMember } from "@shared/rpc";
import type {
  DescentStep,
  EditorDirective,
  EditorDocument,
  EditorNode,
  EditorValue,
} from "./types";

/** A directive with its UI identity stamped — narrows `_uiId` from optional
 *  to required. Stamping happens on parse / on add; stripping happens on
 *  serialise. Tests construct these directly via the helpers below. */
export type StampedDirective = Omit<EditorDirective, "_uiId"> & { _uiId: string };
export type StampedNode = Omit<EditorNode, "_uiId" | "directives"> & {
  _uiId: string;
  directives: StampedDirective[];
};

/**
 * Converts a vanilla InspectedFieldNode into the editor's EditorValue shape.
 * The `member` carries the declared kind so we honour `Byte` vs `Int32` and
 * emit the correct enum/reference shape; the inspected node alone wouldn't
 * be enough (its scalar `kind` is just `int`/`string`/etc).
 *
 * Returns undefined for shapes that don't map to a single EditorValue
 * (collections appended one element at a time, null/missing values), so the
 * caller falls back to the existing neutral default.
 */
export function inspectedFieldToEditorValue(
  node: InspectedFieldNode | undefined,
  member: TemplateMember,
): EditorValue | undefined {
  if (!node || node.null === true) return undefined;

  const scalarKind = member.patchScalarKind;
  switch (scalarKind) {
    case "Boolean":
      return typeof node.value === "boolean" ? { kind: "Boolean", boolean: node.value } : undefined;
    case "Byte":
      return typeof node.value === "number"
        ? { kind: "Byte", int32: Math.trunc(node.value) }
        : undefined;
    case "Int32":
      return typeof node.value === "number"
        ? { kind: "Int32", int32: Math.trunc(node.value) }
        : undefined;
    case "Single":
      return typeof node.value === "number" ? { kind: "Single", single: node.value } : undefined;
    case "String":
      return typeof node.value === "string" ? { kind: "String", string: node.value } : undefined;
    case "Enum": {
      if (typeof node.value !== "string" || node.value === "") return undefined;
      const enumType = member.elementTypeName ?? member.typeName;
      return { kind: "Enum", enumType, enumValue: node.value };
    }
    case "TemplateReference": {
      const refName = node.reference?.name;
      if (!refName) return undefined;
      const value: EditorValue = { kind: "TemplateReference", referenceId: refName };
      // Only attach referenceType for polymorphic destinations; otherwise the
      // catalog/loader resolves the field's declared element type and the
      // explicit setter would be redundant noise.
      if (member.isReferenceTypePolymorphic === true && node.reference?.className) {
        value.referenceType = node.reference.className;
      }
      return value;
    }
    case "AssetReference": {
      // Vanilla asset-typed fields surface in the inspector as a "reference"
      // node carrying the asset's Unity name. Reuse the name as the asset
      // reference value so a clone of an existing template prefills with
      // the same asset.
      const refName = node.reference?.name;
      if (!refName) return undefined;
      return { kind: "AssetReference", assetName: refName };
    }
    case null:
    case undefined: {
      // Member is non-scalar non-ref, so the inspected node should be a
      // composite. Recurse into sub-fields with low-fidelity mapping;
      // sub-field member shapes aren't loaded here, so the byKind helper
      // emits scalars/refs without their declared types.
      if (node.kind !== "object" || !node.fields) return undefined;
      const compositeType = member.elementTypeName ?? member.typeName;
      if (!compositeType) return undefined;
      const compositeDirectives: EditorDirective[] = [];
      for (const sub of node.fields) {
        if (!sub.name) continue;
        const subValue = inspectedFieldToEditorValueByKind(sub);
        if (subValue) compositeDirectives.push({ op: "Set", fieldPath: sub.name, value: subValue });
      }
      return { kind: "Composite", compositeType, compositeDirectives };
    }
    default:
      // Unknown patchScalarKind (host added a kind the frontend hasn't
      // caught up to): bail to neutral default rather than guessing.
      return undefined;
  }
}

// Lower-fidelity converter used inside composite recursion where the parent
// hasn't loaded sub-field member shapes yet. Maps the inspected node's
// raw kind to the closest EditorValue kind. Numeric ints default to Int32
// (Byte distinction is lost without a member shape; the visual editor will
// still serialise correctly via the parent composite's catalog validation).
function inspectedFieldToEditorValueByKind(node: InspectedFieldNode): EditorValue | undefined {
  if (node.null === true) return undefined;
  switch (node.kind) {
    case "bool":
      return typeof node.value === "boolean" ? { kind: "Boolean", boolean: node.value } : undefined;
    case "int":
      return typeof node.value === "number"
        ? { kind: "Int32", int32: Math.trunc(node.value) }
        : undefined;
    case "float":
      return typeof node.value === "number" ? { kind: "Single", single: node.value } : undefined;
    case "string":
      return typeof node.value === "string" ? { kind: "String", string: node.value } : undefined;
    case "enum": {
      // Inspected sub-fields carry their enum type via fieldTypeName (the
      // backend enricher tags it). Without that we have no enumType to emit.
      if (typeof node.value !== "string" || node.value === "") return undefined;
      const enumType = node.fieldTypeName;
      if (!enumType) return undefined;
      return { kind: "Enum", enumType, enumValue: node.value };
    }
    case "reference": {
      const refName = node.reference?.name;
      return refName ? { kind: "TemplateReference", referenceId: refName } : undefined;
    }
    case "object": {
      const compositeType = node.fieldTypeName ?? "";
      if (!compositeType || !node.fields) return undefined;
      const compositeDirectives: EditorDirective[] = [];
      for (const sub of node.fields) {
        if (!sub.name) continue;
        const subValue = inspectedFieldToEditorValueByKind(sub);
        if (subValue) compositeDirectives.push({ op: "Set", fieldPath: sub.name, value: subValue });
      }
      return { kind: "Composite", compositeType, compositeDirectives };
    }
    default:
      return undefined;
  }
}

/**
 * Composite and HandlerConstruction share the same field-bag shape and render
 * through the same CompositeEditor; this predicate centralises the call sites
 * so a future kind with a third distinct shape doesn't accidentally inherit
 * the field-bag rendering path.
 */
export function isFieldBagValue(value: EditorValue | undefined): boolean {
  return value?.kind === "Composite" || value?.kind === "HandlerConstruction";
}

/**
 * C# enums are sealed by language: every enum field has exactly one valid
 * enum type, taken from the catalog. There's no polymorphic case (unlike
 * template references). On commit, we realign the serialised enumType to
 * the declared type so saved KDL is canonical (enum="ItemSlot" "..."). When
 * the catalog can't supply a type (rare; would mean the field's enum type
 * isn't loadable), fall back to whatever the value already carried so we
 * don't drop information.
 */
export function resolveEnumCommitType(
  declaredEnumType: string | undefined,
  fallback: string | undefined,
): string | undefined {
  if (declaredEnumType !== undefined && declaredEnumType !== "") return declaredEnumType;
  return fallback;
}

/**
 * Decides whether the ref-type combobox should be shown. Hidden for
 * monomorphic destinations (catalog supplies a concrete type and modder
 * can't pick anything else); visible when:
 *  - the catalog couldn't supply a declared type (rare; fallback authoring)
 *  - the declared type is an abstract base with multiple concrete subtypes
 *    (e.g. RewardTableTemplate.Items → BaseItemTemplate, where the modder
 *    must pick ModularVehicleWeaponTemplate / ArmorTemplate / …)
 *  - the value carries an explicit ref type that doesn't match the declared
 *    type (data inconsistency the modder needs to see and fix)
 */
export function shouldShowRefTypeSelector(
  declaredRefType: string,
  isPolymorphic: boolean,
  explicitRefType: string,
): boolean {
  if (declaredRefType === "") return true;
  if (isPolymorphic) return true;
  // Monomorphic destination with a redundant explicit type (e.g. KDL written
  // as `ref="SkillTemplate"` for a field already declared as
  // ReferenceArray<SkillTemplate>) hides the selector: the modder has no
  // meaningful alternative to pick. A mismatched explicit type still shows
  // so the inconsistency is visible.
  return explicitRefType !== "" && explicitRefType !== declaredRefType;
}

/**
 * What text the ref-type combobox should display.
 *
 * Monomorphic case: fall back to the declared concrete type. The selector is
 * usually hidden anyway; if shown (because the value already carried an
 * explicit type) the declared type is the right idle value.
 *
 * Polymorphic case: track ONLY the explicit type. Falling back to the
 * declared (abstract) type re-fills the combobox after the modder clears it,
 * looking like the delete didn't take. The modder must pick a concrete type;
 * the abstract base is never a valid display value here.
 */
export function resolveRefTypeDisplay(
  declaredRefType: string,
  isPolymorphic: boolean,
  explicitRefType: string,
): string {
  if (isPolymorphic) return explicitRefType;
  return explicitRefType !== "" ? explicitRefType : declaredRefType;
}

/**
 * Collections accept multiple directives on the same fieldPath: Append/
 * Insert/Remove for plain collections, Set at distinct enum indices for
 * named arrays. Odin multi-dim arrays accept one Set per cell address.
 * Scalars, references, and plain composites map 1:1 to a single Set, so
 * duplicates would clobber each other.
 */
export function allowsMultipleDirectives(member: {
  isCollection?: boolean | null;
  isOdinMultiDimArray?: boolean | null;
}): boolean {
  return member.isCollection === true || member.isOdinMultiDimArray === true;
}

/**
 * Type predicate for directives that address a single cell of an Odin
 * multi-dim array — Set ops with a populated indexPath of rank ≥ 2.
 * Used by NodeCard, DirectiveBody, and MatrixFieldEditor to decide
 * which directives belong to a matrix grid vs the flat directive list.
 * Narrowing the indexPath to `number[]` lets callers index into it
 * without further null guards.
 */
export function isCellAddressedSet<T extends { op: string; indexPath?: number[] | null }>(
  d: T,
): d is T & { op: "Set"; indexPath: number[] } {
  return d.op === "Set" && d.indexPath != null && d.indexPath.length >= 2;
}

/**
 * Stamp a directive with a stable UI identifier, recursing into composite /
 * handler-construction values so their inner directives also get keys.
 * Identity already on `_uiId` is preserved (re-stamping a stamped doc is a
 * no-op); only freshly-parsed directives without `_uiId` allocate new ones.
 *
 * The ID generator is injected so callers (the editor + tests) can pick
 * either a process-wide monotonically-increasing counter or a deterministic
 * per-test sequence. The default in TVE.tsx is the module-state counter.
 */
export function stampDirective(d: EditorDirective, newId: () => string): StampedDirective {
  const stamped: StampedDirective = { ...d, _uiId: d._uiId ?? newId() };
  if (d.value && (d.value.kind === "Composite" || d.value.kind === "HandlerConstruction")) {
    const inner = d.value.compositeDirectives;
    if (inner) {
      stamped.value = {
        ...d.value,
        compositeDirectives: inner.map((nested) => stampDirective(nested, newId)),
      };
    }
  }
  return stamped;
}

export function stampNodes(nodes: EditorNode[], newId: () => string): StampedNode[] {
  return nodes.map((n) => ({
    ...n,
    _uiId: n._uiId ?? newId(),
    directives: n.directives.map((d) => stampDirective(d, newId)),
  }));
}

/**
 * Strip UI identifiers from a directive (and its nested composite /
 * handler-construction values), so the doc can be handed to the host RPC
 * without UI-only state leaking into the wire format.
 */
export function stripDirectiveUiIds(d: EditorDirective): EditorDirective {
  const { _uiId: _id, ...rest } = d;
  if (
    rest.value &&
    (rest.value.kind === "Composite" || rest.value.kind === "HandlerConstruction")
  ) {
    const inner = rest.value.compositeDirectives;
    if (inner) {
      return {
        ...rest,
        value: { ...rest.value, compositeDirectives: inner.map(stripDirectiveUiIds) },
      };
    }
  }
  return rest;
}

export function stripUiIds(doc: EditorDocument): EditorDocument {
  return {
    ...doc,
    nodes: doc.nodes.map((n) => {
      const { _uiId: _nId, ...rest } = n;
      return {
        ...rest,
        directives: n.directives.map(stripDirectiveUiIds),
      };
    }),
  };
}

/**
 * Item in the directive-list render plan. NodeCard / CompositeEditor walk
 * the produced array and render each entry with the appropriate component.
 * Groups own a contiguous run of directives that share the same outermost
 * descent step (field + index + subtype). A loose entry is anything else —
 * a top-level scalar set, an append, a non-descent set with index, etc.
 *
 * Member descent steps deeper than segment 0 stay on the inner directive's
 * `descent` list intact; rendering recurses for nested groups.
 */
export type DirectiveGroup<T> =
  | { kind: "loose"; directive: T }
  | {
      kind: "group";
      field: string;
      /** Null when the descent step is scalar polymorphic descent (no
       *  collection index); a number for collection-element descent. */
      index: number | null;
      subtype: string | null;
      members: { directive: T; suffix: string }[];
    };

/**
 * Walk the directive list once and partition it into groups + loose
 * entries, preserving order. Two consecutive descent directives merge into
 * the same group only when they share the same outer descent step (field,
 * index, subtype) at position 0 — distinct outer steps can't be folded
 * together because the serialiser deliberately emits them as separate
 * outer blocks. The member's `suffix` is the inner-relative fieldPath used
 * for the row label.
 */
export function groupDirectives<
  T extends {
    fieldPath: string;
    descent?: DescentStep[] | null;
  },
>(directives: readonly T[]): DirectiveGroup<T>[] {
  const result: DirectiveGroup<T>[] = [];
  let active: Extract<DirectiveGroup<T>, { kind: "group" }> | null = null;

  for (const d of directives) {
    const outerStep = d.descent && d.descent.length > 0 ? d.descent[0] : null;
    if (!outerStep) {
      active = null;
      result.push({ kind: "loose", directive: d });
      continue;
    }
    const subtype = outerStep.subtype ?? null;
    if (
      active !== null &&
      active.field === outerStep.field &&
      active.index === outerStep.index &&
      active.subtype === subtype
    ) {
      active.members.push({ directive: d, suffix: d.fieldPath });
      continue;
    }
    const group: Extract<DirectiveGroup<T>, { kind: "group" }> = {
      kind: "group",
      field: outerStep.field,
      index: outerStep.index ?? null,
      subtype,
      members: [{ directive: d, suffix: d.fieldPath }],
    };
    result.push(group);
    active = group;
  }

  return result;
}

/**
 * Reorder a flat directive list, with awareness of descent-group spans.
 * If the source id heads a descent group, all K consecutive members of
 * that group move together; standalone rows behave as a length-1 move.
 *
 * `toSlot` is the target insertion index in the pre-move list (0..len).
 * Returns the new list; callers hand it back to the parent node via the
 * setDirectives callback. Pure — no DOM, no React state.
 */
export function reorderDirectives<T extends StampedDirective>(
  directives: T[],
  fromId: string,
  toSlot: number,
): T[] {
  const fromIdx = directives.findIndex((d) => d._uiId === fromId);
  if (fromIdx === -1) return directives;
  // Descent groups drag as a unit: walk the group plan and find the
  // contiguous run that starts at fromIdx and is headed by fromId. If
  // it's a loose row, span = 1 and the move is identical to a single-
  // directive splice.
  const groups = groupDirectives(directives);
  let span = 1;
  let cursor = 0;
  for (const g of groups) {
    if (g.kind === "loose") {
      if (cursor === fromIdx) {
        span = 1;
        break;
      }
      cursor += 1;
    } else {
      if (cursor === fromIdx && g.members[0]?.directive._uiId === fromId) {
        span = g.members.length;
        break;
      }
      cursor += g.members.length;
    }
  }
  const next = [...directives];
  const moved = next.splice(fromIdx, span);
  const insertAt = toSlot > fromIdx ? toSlot - span : toSlot;
  next.splice(insertAt, 0, ...moved);
  return next;
}

/**
 * Insert a directive into a list at a position derived from a pending
 * descent group's anchor. Keeps the materialised directive at the visual
 * location the pending skeleton was rendered.
 *
 * Anchor semantics:
 * - `{kind: "end"}` → push to the end of the list.
 * - `{kind: "start"}` → unshift to the beginning.
 * - `{kind: "afterIndex", flatIndex: N}` → insert at position N+1, i.e.
 *   directly after the directive currently at index N.
 */
export type PendingAnchor =
  | { kind: "end" }
  | { kind: "start" }
  | { kind: "afterIndex"; flatIndex: number };

export function insertAtPendingAnchor<T>(
  directives: T[],
  newDirective: T,
  anchor: PendingAnchor,
): T[] {
  let insertAt: number;
  if (anchor.kind === "end") insertAt = directives.length;
  else if (anchor.kind === "start") insertAt = 0;
  else insertAt = anchor.flatIndex + 1;
  return [...directives.slice(0, insertAt), newDirective, ...directives.slice(insertAt)];
}

/**
 * Build the directive that materialises a pending descent group's first
 * member. Mirrors what the parser produces for an authored
 * `set "<field>" index=<N> type="<subtype>" { set "<inner>" v }` block:
 * the outer (field, index, subtype) is prepended to the inner directive's
 * descent step list. The inner fieldPath stays inner-relative.
 */
export function buildDescentMemberDirective<T extends EditorDirective>(
  field: string,
  /** Null = scalar polymorphic descent; number = collection-element descent. */
  slotIndex: number | null,
  subtype: string | null,
  innerDirective: T,
): T {
  // Scalar polymorphic descent omits index; collection descent carries it.
  const outerStep: DescentStep = (() => {
    if (slotIndex === null) {
      return subtype !== null ? { field, subtype } : { field };
    }
    return subtype !== null ? { field, index: slotIndex, subtype } : { field, index: slotIndex };
  })();
  const combined: DescentStep[] = [outerStep, ...(innerDirective.descent ?? [])];
  return {
    ...innerDirective,
    descent: combined,
  };
}

/**
 * Rewrite the slot index on every member directive of a descent group.
 * Handles only the [startIndex, endIndex) slice so adjacent groups / loose
 * rows stay untouched. Member descent[0] is mutated in place (cloned per
 * directive). Directives without a descent[0] match (defensive) pass
 * through unchanged.
 */
export function rewriteDescentSlotIndex<T extends EditorDirective>(
  directives: T[],
  startIndex: number,
  endIndex: number,
  field: string,
  oldSlot: number,
  newSlot: number,
): T[] {
  if (newSlot === oldSlot || newSlot < 0) return directives;
  return directives.map((d, i) => {
    if (i < startIndex || i >= endIndex) return d;
    const descent = d.descent ?? [];
    const outer = descent[0];
    if (outer?.field !== field || outer.index !== oldSlot) return d;
    const updatedOuter: DescentStep = { ...outer, index: newSlot };
    return { ...d, descent: [updatedOuter, ...descent.slice(1)] };
  });
}

/**
 * Neutral default value for a freshly-added directive on `member`. Drives
 * the FieldAdder's "click to add" path and any drag-drop fallback. Falls
 * through tiers: declared scalar → ref/enum → polymorphic owned-element
 * collection (HandlerConstruction with picker) → plain composite → string.
 */
export function makeDefaultValue(member: TemplateMember): EditorValue {
  const kind = member.patchScalarKind;
  const elementType = member.elementTypeName;
  // Polymorphic subtype choices come from one of two sources:
  //  - elementSubtypes: collection element family (existing handler= flow)
  //  - scalarSubtypes: scalar polymorphic field (Phase 2b: Odin construction)
  // Both produce a HandlerConstruction value; the modder UX is identical.
  const subtypes = member.elementSubtypes ?? member.scalarSubtypes;

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
    case "AssetReference":
      return { kind: "AssetReference", assetName: "" };
    default:
      // Polymorphic owned-element collection (e.g. EventHandlers): the
      // element is a freshly-constructed ScriptableObject subordinate to the
      // parent template, so emit HandlerConstruction. compositeType stays
      // empty until the modder picks a concrete subtype; CompositeEditor
      // renders the picker against `elementSubtypes`. When exactly one
      // subtype is available there's no real choice — pre-fill it.
      if (subtypes && subtypes.length > 0) {
        const first = subtypes[0];
        const compositeType = subtypes.length === 1 && first ? first : "";
        return { kind: "HandlerConstruction", compositeType, compositeDirectives: [] };
      }
      if (elementType) {
        return { kind: "Composite", compositeType: elementType, compositeDirectives: [] };
      }
      if (!member.isScalar && !member.isTemplateReference && member.typeName) {
        return { kind: "Composite", compositeType: member.typeName, compositeDirectives: [] };
      }
      return { kind: "String", string: "" };
  }
}
