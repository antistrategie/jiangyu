import { useEffect, useMemo, useRef, useState } from "react";
import { X } from "lucide-react";
import type { EnumMemberEntry, InspectedFieldNode, TemplateMember } from "@lib/rpc";
import { rpcCall } from "@lib/rpc";
import { useEditorDispatch, useNodeIndex } from "../store";
import { isCellAddressedSet, type StampedDirective } from "../helpers";
import { uiId } from "../shared/uiId";
import type { EditorValue } from "../types";
import styles from "../TemplateVisualEditor.module.css";
import { formatFlagsLabel, formatFlagsTitle, isFlagsEnum, stripArraySuffix } from "./matrixHelpers";

/**
 * Per-field grid editor for Odin-routed multi-dim arrays. The Odin
 * decoder reshapes the wire format into <c>kind: "matrix"</c> with
 * <c>dimensions: [rows, cols]</c> and a flat <c>elements</c> list in
 * row-major order; this component renders that as a 2D toggle/value grid
 * and emits cell-addressed Set directives back to the editor.
 *
 * Vanilla cells provide the current state; pending directives override
 * specific cells. Bool cells toggle on click. Flags-enum cells (e.g.
 * <c>ChunkTileFlags[,]</c>) open a checkbox popover that mutates the
 * cell's bitmask. Other element types remain source-mode-only.
 */
export interface MatrixFieldEditorProps {
  fieldName: string;
  /** Vanilla state for the field; null when the underlying template has
   *  the field defaulted (Sirenix Odin omits default values, so a
   *  template with a null AOETiles carries no shape). When null, the
   *  grid sizes itself from the catalog member's representative
   *  dimensions, then the directive list's max coordinates. */
  matrix: InspectedFieldNode | null;
  /** Catalog metadata for the field. For Odin multi-dim members the
   *  backend surfaces representative dimensions, element type, and
   *  element kind so the grid can render even with no vanilla state. */
  member: TemplateMember | null;
  /** All current directives on the parent node; we filter to ours by name. */
  directives: readonly StampedDirective[];
  /** Click handler for the X button — clears the parent's matrix opt-in
   *  state and deletes any cell directives the modder has authored. */
  onRemove: () => void;
}

// Module-level cache so multiple matrix editors targeting the same enum
// share one RPC. Lifetime is the editor session.
const enumMembersCache = new Map<string, Promise<readonly EnumMemberEntry[]>>();

function fetchEnumMembers(typeName: string): Promise<readonly EnumMemberEntry[]> {
  let cached = enumMembersCache.get(typeName);
  if (!cached) {
    cached = rpcCall<{ members: readonly EnumMemberEntry[] }>("templatesEnumMembers", { typeName })
      .then((r) => r.members)
      .catch(() => []);
    enumMembersCache.set(typeName, cached);
  }
  return cached;
}

function useEnumMembers(typeName: string | undefined): readonly EnumMemberEntry[] {
  const [resolved, setResolved] = useState<{
    readonly type: string;
    readonly members: readonly EnumMemberEntry[];
  }>({ type: "", members: [] });

  useEffect(() => {
    if (!typeName) return;
    let cancelled = false;
    void fetchEnumMembers(typeName).then((members) => {
      if (!cancelled) setResolved({ type: typeName, members });
    });
    return () => {
      cancelled = true;
    };
  }, [typeName]);

  if (!typeName) return [];
  return resolved.type === typeName ? resolved.members : [];
}

export function MatrixFieldEditor({
  fieldName,
  matrix,
  member,
  directives,
  onRemove,
}: MatrixFieldEditorProps) {
  const dispatch = useEditorDispatch();
  const nodeIndex = useNodeIndex();

  const cells = matrix?.elements ?? [];

  // Index pending edits by `r,c` so the renderer can overlay them on the
  // vanilla cells in O(1) per cell rather than re-scanning the directive
  // list per cell.
  const pendingByCoord = useMemo(() => {
    const map = new Map<string, { directive: StampedDirective; idx: number }>();
    directives.forEach((d, idx) => {
      if (!isCellAddressedSet(d) || d.fieldPath !== fieldName) return;
      if (d.indexPath.length !== 2) return;
      const [r, c] = d.indexPath;
      map.set(`${r},${c}`, { directive: d, idx });
    });
    return map;
  }, [directives, fieldName]);

  // Dimensions: vanilla matrix shape wins; otherwise the catalog's
  // representative dimensions; otherwise infer from the directive list's
  // max coordinates. Falls all the way through when the modder is
  // authoring against a brand-new field with no instance to learn from.
  const [rows, cols] = useMemo(() => {
    if (matrix?.dimensions && matrix.dimensions.length >= 2) {
      return [matrix.dimensions[0] ?? 0, matrix.dimensions[1] ?? 0];
    }
    if (member?.multiDimDimensions && member.multiDimDimensions.length >= 2) {
      return [member.multiDimDimensions[0] ?? 0, member.multiDimDimensions[1] ?? 0];
    }
    let maxR = -1;
    let maxC = -1;
    pendingByCoord.forEach((_, key) => {
      const [r, c] = key.split(",").map(Number);
      if ((r ?? 0) > maxR) maxR = r ?? 0;
      if ((c ?? 0) > maxC) maxC = c ?? 0;
    });
    return [maxR + 1, maxC + 1];
  }, [matrix, member, pendingByCoord]);

  // Element type: prefer the catalog (set when the backend matched the
  // field via the Odin matrix registry), fall back to parsing the
  // vanilla matrix's declared field type. The catalog accepts short
  // names, so we don't need the full namespace either way. Property
  // reads are hoisted out of useMemo so its dep array matches what the
  // closure actually reads (React Compiler rejects compound-path
  // deps because the chain implicitly reads the parent object too).
  const memberElementType = member?.multiDimElementType;
  const matrixFieldTypeName = matrix?.fieldTypeName;
  const elementTypeName = useMemo(() => {
    if (memberElementType) return memberElementType;
    if (!matrixFieldTypeName) return undefined;
    const stripped = stripArraySuffix(matrixFieldTypeName);
    const lastDot = stripped.lastIndexOf(".");
    return lastDot >= 0 ? stripped.slice(lastDot + 1) : stripped;
  }, [memberElementType, matrixFieldTypeName]);

  // Cell-kind hint from the catalog (mirrors InspectedFieldNode.Kind on
  // a representative cell), used when there's no vanilla matrix to peek
  // at directly.
  const cellKindHint = cells[0]?.kind ?? member?.multiDimElementKind ?? undefined;

  // Only fetch enum members when the cells look like enum values (kind:
  // "int"); avoids a wasted RPC for bool[,] or string[,] matrices.
  const enumQueryType = cellKindHint === "int" ? elementTypeName : undefined;
  const enumMembers = useEnumMembers(enumQueryType);
  const flagsCapable = enumMembers.length > 0 && isFlagsEnum(enumMembers);

  // Element-kind detection. Vanilla cells are the source of truth when
  // present; otherwise the catalog hint; otherwise we sniff the cell
  // directive value kinds so a grid built purely from directives still
  // picks bool / scalar.
  const elementKind = useMemo<"bool" | "flags" | "scalar">(() => {
    if (cellKindHint === "bool") return "bool";
    if (flagsCapable) return "flags";
    if (cells.length === 0) {
      const sample = directives.find(
        (d) => isCellAddressedSet(d) && d.fieldPath === fieldName,
      )?.value;
      if (sample?.kind === "Boolean") return "bool";
    }
    return "scalar";
  }, [cellKindHint, flagsCapable, cells.length, directives, fieldName]);

  const [openCellKey, setOpenCellKey] = useState<string | null>(null);
  const popoverRef = useRef<HTMLDivElement | null>(null);

  useEffect(() => {
    if (!openCellKey) return;
    const onMouseDown = (e: MouseEvent) => {
      if (popoverRef.current?.contains(e.target as Node) === true) return;
      setOpenCellKey(null);
    };
    const onKey = (e: KeyboardEvent) => {
      if (e.key === "Escape") setOpenCellKey(null);
    };
    document.addEventListener("mousedown", onMouseDown);
    document.addEventListener("keydown", onKey);
    return () => {
      document.removeEventListener("mousedown", onMouseDown);
      document.removeEventListener("keydown", onKey);
    };
  }, [openCellKey]);

  if (rows <= 0 || cols <= 0) return null;
  // When vanilla is present, its cells must match the declared shape.
  // When absent, cells is empty by construction; the grid renders with
  // null vanilla per cell and pending directives are the only state.
  if (matrix && cells.length !== rows * cols) return null;

  function vanillaCellValue(r: number, c: number): EditorValue | null {
    if (!matrix) return null;
    const cell = cells[r * cols + c];
    if (!cell) return null;
    if (cell.kind === "bool") return { kind: "Boolean", boolean: cell.value === true };
    if (typeof cell.value === "number") return { kind: "Int32", int32: cell.value };
    if (typeof cell.value === "string") return { kind: "String", string: cell.value };
    return null;
  }

  function effectiveCellValue(r: number, c: number): EditorValue | null {
    const pending = pendingByCoord.get(`${r},${c}`);
    return pending?.directive.value ?? vanillaCellValue(r, c);
  }

  function valuesEqual(a: EditorValue | null, b: EditorValue | null): boolean {
    if (a === null || b === null) return a === b;
    if (a.kind !== b.kind) return false;
    if (a.kind === "Boolean") return a.boolean === b.boolean;
    if (a.kind === "Int32") return a.int32 === b.int32;
    if (a.kind === "String") return a.string === b.string;
    return false;
  }

  // Add / update / delete the pending Set directive for cell (r,c) so it
  // ends up at `newValue`. Drops the directive when the new value matches
  // vanilla, keeping the directive list minimal.
  function applyCellValue(r: number, c: number, newValue: EditorValue) {
    const vanilla = vanillaCellValue(r, c);
    const pending = pendingByCoord.get(`${r},${c}`);
    if (pending) {
      if (valuesEqual(newValue, vanilla)) {
        dispatch({ type: "deleteDirective", nodeIndex, dirIndex: pending.idx });
      } else {
        dispatch({
          type: "updateDirective",
          nodeIndex,
          dirIndex: pending.idx,
          directive: { ...pending.directive, value: newValue },
        });
      }
      return;
    }
    if (valuesEqual(newValue, vanilla)) return;
    const directive: StampedDirective = {
      op: "Set",
      fieldPath: fieldName,
      indexPath: [r, c],
      value: newValue,
      _uiId: uiId(),
    };
    dispatch({ type: "addDirective", nodeIndex, directive });
  }

  function onBoolCellClick(r: number, c: number) {
    const effective = effectiveCellValue(r, c);
    const toggled: EditorValue = {
      kind: "Boolean",
      boolean: !(effective?.kind === "Boolean" && effective.boolean === true),
    };
    applyCellValue(r, c, toggled);
  }

  function onFlagBitToggle(r: number, c: number, bit: number) {
    const effective = effectiveCellValue(r, c);
    const currentMask = effective?.kind === "Int32" ? (effective.int32 ?? 0) : 0;
    const newMask = (currentMask & bit) === bit ? currentMask & ~bit : currentMask | bit;
    applyCellValue(r, c, { kind: "Int32", int32: newMask });
  }

  function onFlagsClear(r: number, c: number) {
    applyCellValue(r, c, { kind: "Int32", int32: 0 });
  }

  return (
    <div className={styles.matrixField}>
      <div className={styles.matrixFieldHeader}>
        {/* Spacer that occupies the same column as setRow's drag grip,
            so the "matrix" badge below lines up with the op label on
            adjacent SetRows. */}
        <span className={styles.matrixHeaderRail} aria-hidden />
        <span className={styles.matrixFieldKind}>matrix</span>
        <span className={styles.matrixFieldName}>{fieldName}</span>
        <span className={styles.matrixFieldChip}>
          {rows}×{cols}
        </span>
        {elementKind === "flags" && elementTypeName && (
          <span className={styles.matrixFieldChip}>{elementTypeName}</span>
        )}
        {!matrix && <span className={styles.matrixFieldChip}>no vanilla</span>}
        <span className={styles.matrixFieldSpacer} aria-hidden />
        {pendingByCoord.size > 0 && (
          <span className={styles.matrixFieldDirty}>
            {pendingByCoord.size} cell{pendingByCoord.size === 1 ? "" : "s"} edited
          </span>
        )}
        <button
          type="button"
          className={styles.matrixFieldDelete}
          onClick={onRemove}
          title="Remove matrix field"
        >
          <X size={12} />
        </button>
      </div>
      <div className={styles.matrixGridWrap}>
        <div className={styles.matrixGrid}>
          {Array.from({ length: rows }, (_, r) => (
            <div key={r} className={styles.matrixRow}>
              {Array.from({ length: cols }, (_, c) => {
                const eff = effectiveCellValue(r, c);
                const isPending = pendingByCoord.has(`${r},${c}`);
                const dirty = isPending ? styles.matrixCellDirty : "";
                const cellKey = `${r},${c}`;

                if (elementKind === "bool") {
                  const isTrue = eff?.kind === "Boolean" && eff.boolean === true;
                  const tone = isTrue ? styles.matrixCellTrue : styles.matrixCellFalse;
                  return (
                    <button
                      type="button"
                      key={c}
                      className={`${styles.matrixCell} ${tone} ${dirty}`}
                      title={`[${r},${c}]${isPending ? " (edited)" : ""}`}
                      onClick={() => onBoolCellClick(r, c)}
                    >
                      {isTrue ? "■" : "·"}
                    </button>
                  );
                }

                if (elementKind === "flags") {
                  const mask = eff?.kind === "Int32" ? (eff.int32 ?? 0) : 0;
                  const tone = mask !== 0 ? styles.matrixCellTrue : styles.matrixCellFalse;
                  const isOpen = openCellKey === cellKey;
                  return (
                    <span key={c} className={styles.matrixCellWrap}>
                      <button
                        type="button"
                        className={`${styles.matrixCell} ${styles.matrixCellFlags} ${tone} ${dirty}`}
                        title={formatFlagsTitle(mask, enumMembers, r, c, isPending)}
                        aria-haspopup="dialog"
                        aria-expanded={isOpen}
                        onClick={() => setOpenCellKey(isOpen ? null : cellKey)}
                      >
                        {formatFlagsLabel(mask)}
                      </button>
                      {isOpen && (
                        <div ref={popoverRef} className={styles.matrixCellPopover} role="dialog">
                          <div className={styles.matrixFlagPopoverHeader}>
                            <span className={styles.matrixFlagPopoverCoord}>
                              [{r},{c}]
                            </span>
                            <button
                              type="button"
                              className={styles.matrixFlagClear}
                              onClick={() => onFlagsClear(r, c)}
                              title="Clear all flags"
                            >
                              clear
                            </button>
                          </div>
                          {enumMembers
                            .filter((m) => m.value !== 0)
                            .map((m) => {
                              const checked = (mask & m.value) === m.value;
                              return (
                                <label key={m.name} className={styles.matrixFlagOption}>
                                  <input
                                    type="checkbox"
                                    checked={checked}
                                    onChange={() => onFlagBitToggle(r, c, m.value)}
                                  />
                                  <span className={styles.matrixFlagName}>{m.name}</span>
                                  <span className={styles.matrixFlagBit}>
                                    0x{m.value.toString(16).toUpperCase()}
                                  </span>
                                </label>
                              );
                            })}
                        </div>
                      )}
                    </span>
                  );
                }

                // Unsupported scalar element type (e.g. string[,]). Render a
                // disabled cell so the grid still shows shape; editing falls
                // back to source mode.
                const numeric = eff?.kind === "Int32" ? (eff.int32 ?? 0) : 0;
                const stringy = eff?.kind === "String" ? (eff.string ?? "") : "";
                const display = eff?.kind === "Int32" ? String(numeric) : stringy || "·";
                return (
                  <button
                    type="button"
                    key={c}
                    className={`${styles.matrixCell} ${styles.matrixCellFalse} ${dirty}`}
                    title={`[${r},${c}]${isPending ? " (edited)" : ""}`}
                    disabled
                  >
                    {display}
                  </button>
                );
              })}
            </div>
          ))}
        </div>
      </div>
      {elementKind === "scalar" && (
        <div className={styles.matrixFieldHint}>
          Cell editing for this element type is source-mode-only. Use{" "}
          <code>set "{fieldName}" cell="r,c" &lt;value&gt;</code>.
        </div>
      )}
    </div>
  );
}
