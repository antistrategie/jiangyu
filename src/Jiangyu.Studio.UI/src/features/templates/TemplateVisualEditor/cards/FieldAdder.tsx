import React, { useEffect, useRef, useState } from "react";
import { createPortal } from "react-dom";
import { Plus } from "lucide-react";
import type { TemplateMember } from "@shared/rpc";
import {
  INSTANCE_DRAG_TAG,
  MEMBER_DRAG_TAG,
  getActiveTemplateDrag,
} from "@features/templates/crossInstance";
import type { InspectedFieldNode } from "@shared/rpc";
import { allowsMultipleDirectives } from "../helpers";
import type { StampedDirective } from "../helpers";
import { makeDefaultDirective } from "../shared/rpcHelpers";
import { useAnchorPosition } from "../shared/useAnchorPosition";
import styles from "../TemplateVisualEditor.module.css";

export interface FieldAdderProps {
  members: readonly TemplateMember[];
  membersLoaded: boolean;
  existingFields: string[];
  /** Field names that already have a matrix grid open on this card.
   *  Picking a matrix member that's already open is a no-op (no
   *  duplicate grids). Optional — defaults to empty for non-card
   *  callers like CompositeEditor where matrix members aren't
   *  supported. */
  existingMatrixFields?: ReadonlySet<string>;
  /** Template type (or composite type) this adder belongs to. Used to reject
   *  drags of fields from unrelated templates. Empty string = accept any. */
  targetTemplateType: string;
  onAdd: (directive: StampedDirective) => void;
  /** Picking an Odin multi-dim member (AOETiles, ChunkTileFlags) opts
   *  the field into a matrix grid editor on the card instead of
   *  dispatching a directive. Optional — when absent, matrix members
   *  are filtered out of the dropdown so the modder isn't offered
   *  something we can't wire. */
  onAddMatrix?: (name: string) => void;
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

export function FieldAdder({
  members,
  membersLoaded,
  existingFields,
  existingMatrixFields,
  targetTemplateType,
  onAdd,
  onAddMatrix,
  onDrop,
  vanillaFields,
  onStartDescent,
}: FieldAdderProps) {
  const [query, setQuery] = useState("");
  const [open, setOpen] = useState(false);
  const [fieldDragOver, setFieldDragOver] = useState<"accept" | "reject" | false>(false);
  const wrapRef = useRef<HTMLDivElement>(null);
  const inputRef = useRef<HTMLInputElement>(null);
  const dropdownRef = useRef<HTMLDivElement>(null);
  // Portalled to document.body so it escapes the card's compositor-promoted
  // stacking context; without this, the dropdown is trapped under the next
  // card. Position glues to the wrapper's bounding rect via useAnchorPosition.
  const position = useAnchorPosition(wrapRef, open);

  useEffect(() => {
    if (!open) return;
    const handler = (e: MouseEvent) => {
      const target = e.target as Node;
      if (wrapRef.current?.contains(target)) return;
      if (dropdownRef.current?.contains(target)) return;
      setOpen(false);
    };
    document.addEventListener("mousedown", handler);
    return () => document.removeEventListener("mousedown", handler);
  }, [open]);

  const existingSet = new Set(existingFields);
  const matrixSet = existingMatrixFields ?? new Set<string>();
  const lowerQuery = query.toLowerCase();
  const filtered = members.filter(
    (m) =>
      (m.isWritable || m.isCollection === true || m.isOdinMultiDimArray === true) &&
      m.isHiddenInInspector !== true &&
      // Odin-routed members live in the serializationData blob. Surface
      // them in the dropdown when (a) we have an opt-in handler and the
      // member is a multi-dim primitive array (AOETiles, ChunkTileFlags;
      // picked, grid opens), (b) the member is a HashSet<T> (picked,
      // modder gets append/remove rows; same UX as List<T>), or (c) they
      // are polymorphic-constructible (scalarSubtypes populated; picked,
      // modder fills inner fields, loader applier constructs the value).
      // Hide everything else because there's no sensible default editor
      // for it yet.
      (m.isLikelyOdinOnly !== true ||
        (m.isOdinMultiDimArray === true && onAddMatrix !== undefined) ||
        m.isOdinHashSet === true ||
        (m.scalarSubtypes != null && m.scalarSubtypes.length > 0)) &&
      m.name.toLowerCase().includes(lowerQuery),
  );
  // Multi-directive fields (collections, named arrays) stay in `available`
  // even when an entry already exists, because adding another directive is
  // the normal flow rather than a duplicate. Matrix fields use a separate
  // opt-in set because the dropdown shouldn't offer to "add" a grid
  // that's already open on this card.
  const available = filtered.filter((m) => {
    if (m.isOdinMultiDimArray) return !matrixSet.has(m.name);
    return !existingSet.has(m.name) || allowsMultipleDirectives(m);
  });
  const alreadyAdded = filtered.filter((m) => {
    if (m.isOdinMultiDimArray) return matrixSet.has(m.name);
    return existingSet.has(m.name) && !allowsMultipleDirectives(m);
  });

  const handleSelect = (member: TemplateMember) => {
    if (member.isOdinMultiDimArray && onAddMatrix) {
      onAddMatrix(member.name);
    } else {
      const vanilla = vanillaFields?.get(member.name);
      onAdd(makeDefaultDirective(member, vanilla));
    }
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
      {open &&
        position &&
        createPortal(
          <div
            className={styles.fieldAdderDropdown}
            ref={dropdownRef}
            style={{
              position: "fixed",
              top: position.top,
              left: position.left,
              right: "auto",
              width: position.width,
              zIndex: 9999,
            }}
          >
            {!membersLoaded && <div className={styles.fieldAdderHint}>Loading fields…</div>}
            {membersLoaded && filtered.length === 0 && query.length > 0 && (
              <div className={styles.fieldAdderHint}>No matching fields</div>
            )}
            {membersLoaded &&
              filtered.length === 0 &&
              query.length === 0 &&
              members.length === 0 && (
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
                title="Already added; click to add another directive"
              >
                <span className={styles.fieldAdderItemName}>{m.name}</span>
                <span className={styles.fieldAdderItemType}>{m.typeName}</span>
              </button>
            ))}
          </div>,
          document.body,
        )}
    </div>
  );
}
