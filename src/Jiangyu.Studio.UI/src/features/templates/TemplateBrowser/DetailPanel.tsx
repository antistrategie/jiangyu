// Detail panel rendering for TemplateBrowser. Owns everything that draws once
// a single template instance is focused: the header, scaffold menu, reference
// tree, and the recursive field/value rows.

import { GripVertical } from "lucide-react";
import { Fragment, useCallback, useEffect, useRef, useState } from "react";
import type {
  InspectedFieldNode,
  InspectedReference,
  TemplateEdge,
  TemplateInstanceEntry,
  TemplateMember,
  TemplateQueryResult,
  TemplateReferenceEntry,
} from "@shared/rpc";
import { attachDragChip } from "@shared/drag/chip";
import {
  beginTemplateDrag,
  endTemplateDrag,
  MEMBER_DRAG_TAG,
  TEMPLATE_DRAG_TAG,
} from "@features/templates/crossInstance";
import { encodeCrossMemberPayload } from "@features/templates/crossMember";
import { onKeyActivate } from "@shared/utils/a11y";
import { Spinner } from "@shared/ui/Spinner/Spinner";
import { DetailTitle, MetaBlock, MetaRow, SectionHeader } from "@shared/ui/DetailPanel/DetailPanel";
import {
  MenuItem,
  MenuItemLabel,
  MenuList,
  MenuListBody,
  MenuSeparator,
} from "@shared/ui/MenuList/MenuList";
import styles from "./TemplateBrowser.module.css";
import {
  buildNamedArrayLabelMap,
  formatMatrixCell,
  formatValue,
  resolveEnumLeafLabel,
  valueNodeKindIsScalar,
} from "./helpers";

interface TemplateDetailProps {
  instance: TemplateInstanceEntry;
  memberData: TemplateQueryResult | null;
  membersLoading: boolean;
  inspectionValues: readonly InspectedFieldNode[] | null;
  inspectionLoading: boolean;
  onCreatePatch: (inst?: TemplateInstanceEntry) => void;
  onCreateClone: (inst?: TemplateInstanceEntry) => void;
  onPatchToFile: (inst?: TemplateInstanceEntry) => void;
  onCloneToFile: (inst?: TemplateInstanceEntry) => void;
  onNavigate: (key: string) => void;
  onGoBack: () => void;
  onGoForward: () => void;
  canGoBack: boolean;
  canGoForward: boolean;
  projectPath: string;
  referencedBy: readonly TemplateReferenceEntry[];
  instanceLookup: ReadonlyMap<string, TemplateInstanceEntry>;
  allReferencedBy: Readonly<Record<string, readonly TemplateReferenceEntry[]>>;
}

export function TemplateDetail({
  instance,
  memberData,
  membersLoading,
  inspectionValues,
  inspectionLoading,
  onCreatePatch,
  onCreateClone,
  onPatchToFile,
  onCloneToFile,
  onNavigate,
  onGoBack,
  onGoForward,
  canGoBack,
  canGoForward,
  referencedBy: refs,
  instanceLookup,
  allReferencedBy,
}: TemplateDetailProps) {
  const [menuOpen, setMenuOpen] = useState(false);
  const menuRef = useRef<HTMLDivElement>(null);

  useEffect(() => {
    if (!menuOpen) return;
    const handleClick = (e: MouseEvent) => {
      if (menuRef.current && !menuRef.current.contains(e.target as Node)) {
        setMenuOpen(false);
      }
    };
    document.addEventListener("mousedown", handleClick);
    return () => document.removeEventListener("mousedown", handleClick);
  }, [menuOpen]);

  return (
    <div className={styles.detail}>
      <div className={styles.detailNav}>
        <button
          type="button"
          className={styles.navBtn}
          disabled={!canGoBack}
          onClick={onGoBack}
          title="Back"
        >
          ←
        </button>
        <button
          type="button"
          className={styles.navBtn}
          disabled={!canGoForward}
          onClick={onGoForward}
          title="Forward"
        >
          →
        </button>
        <div className={styles.detailActions}>
          <div className={styles.scaffoldMenu} ref={menuRef}>
            <button
              type="button"
              className={styles.actionBtn}
              onClick={() => setMenuOpen((v) => !v)}
              aria-label="Scaffold"
            >
              <span>Scaffold</span>
              <span className={styles.actionBtnChevron} aria-hidden>
                ▾
              </span>
            </button>
            {menuOpen && (
              <MenuList className={styles.scaffoldMenuPos}>
                <MenuListBody>
                  <MenuItem
                    onClick={() => {
                      setMenuOpen(false);
                      onCreatePatch();
                    }}
                  >
                    <MenuItemLabel>Create Patch</MenuItemLabel>
                  </MenuItem>
                  <MenuItem
                    onClick={() => {
                      setMenuOpen(false);
                      onCreateClone();
                    }}
                  >
                    <MenuItemLabel>Create Clone</MenuItemLabel>
                  </MenuItem>
                  <MenuSeparator />
                  <MenuItem
                    onClick={() => {
                      setMenuOpen(false);
                      onPatchToFile();
                    }}
                  >
                    <MenuItemLabel>Add patch to file…</MenuItemLabel>
                  </MenuItem>
                  <MenuItem
                    onClick={() => {
                      setMenuOpen(false);
                      onCloneToFile();
                    }}
                  >
                    <MenuItemLabel>Add clone to file…</MenuItemLabel>
                  </MenuItem>
                </MenuListBody>
              </MenuList>
            )}
          </div>
        </div>
      </div>
      <div className={styles.detailHeader}>
        <DetailTitle>{instance.name}</DetailTitle>
        <MetaBlock>
          <MetaRow label="Type" value={instance.className} />
          <MetaRow label="Collection" value={instance.identity.collection} />
          <MetaRow label="PathId" value={String(instance.identity.pathId)} />
          {refs.length > 0 && (
            <MetaRow
              label="Referenced by"
              value={refs.map((r, i) => {
                const srcKey = `${r.source.collection}:${r.source.pathId}`;
                const src = instanceLookup.get(srcKey);
                const isLast = i === refs.length - 1;
                return (
                  <Fragment key={`${r.fieldName}:${srcKey}`}>
                    <span className={styles.refEntry}>
                      <button
                        type="button"
                        className={styles.refLink}
                        onClick={() => onNavigate(srcKey)}
                      >
                        {src ? `${src.name} (${src.className})` : srcKey}
                      </button>
                      {!isLast && ","}
                    </span>
                    {!isLast && " "}
                  </Fragment>
                );
              })}
            />
          )}
        </MetaBlock>
      </div>

      {/* --- Reference tree --- */}
      {instance.references && instance.references.length > 0 && (
        <div className={styles.refSection}>
          <SectionHeader>References</SectionHeader>
          <div className={styles.refTree}>
            {instance.references.map((edge) => (
              <ReferenceNode
                key={`${edge.fieldName}:${edge.target.collection}:${edge.target.pathId}`}
                edge={edge}
                depth={0}
                instanceLookup={instanceLookup}
                allReferencedBy={allReferencedBy}
                onNavigate={onNavigate}
              />
            ))}
          </div>
        </div>
      )}

      {(membersLoading ||
        inspectionLoading ||
        (memberData?.members && memberData.members.length > 0)) && (
        <div className={styles.memberSection}>
          <SectionHeader>Fields</SectionHeader>
          {(membersLoading || inspectionLoading) && !memberData?.members ? (
            <div className={styles.memberLoading}>
              <Spinner size={12} />
              <span>Loading fields…</span>
            </div>
          ) : memberData?.members ? (
            <div className={styles.memberList}>
              {memberData.members.map((m) => {
                const valueNode = inspectionValues?.find((v) => v.name === m.name) ?? null;
                return (
                  <MemberRow
                    key={m.name}
                    member={m}
                    valueNode={valueNode}
                    parentTypeName={instance.className}
                    fieldPath={m.name}
                    instanceLookup={instanceLookup}
                    onNavigate={onNavigate}
                  />
                );
              })}
            </div>
          ) : null}
        </div>
      )}
    </div>
  );
}

// --- Reference tree node ---

interface ReferenceNodeProps {
  edge: TemplateEdge;
  depth: number;
  instanceLookup: ReadonlyMap<string, TemplateInstanceEntry>;
  allReferencedBy: Readonly<Record<string, readonly TemplateReferenceEntry[]>>;
  onNavigate: (key: string) => void;
}

function ReferenceNode({
  edge,
  depth,
  instanceLookup,
  allReferencedBy,
  onNavigate,
}: ReferenceNodeProps) {
  const [expanded, setExpanded] = useState(false);
  const key = `${edge.target.collection}:${edge.target.pathId}`;
  const target = instanceLookup.get(key);
  const hasChildren = target?.references && target.references.length > 0;

  return (
    <div className={styles.refNode}>
      <div
        className={`${styles.refRow} ${hasChildren ? styles.refRowExpandable : ""}`}
        {...(hasChildren && {
          role: "button",
          tabIndex: 0,
          "aria-expanded": expanded,
          onClick: () => {
            setExpanded(!expanded);
          },
          onKeyDown: onKeyActivate(() => {
            setExpanded(!expanded);
          }),
        })}
      >
        {hasChildren ? (
          <span className={`${styles.refExpander} ${expanded ? styles.refExpanderOpen : ""}`}>
            ▸
          </span>
        ) : (
          <span className={styles.refExpanderSpacer} />
        )}
        <span className={styles.refField}>{edge.fieldName}</span>
        <button
          type="button"
          className={styles.refLink}
          onClick={(e) => {
            e.stopPropagation();
            onNavigate(key);
          }}
        >
          {target ? target.name : key}
        </button>
        {target && <span className={styles.refType}>{target.className}</span>}
      </div>
      {expanded && hasChildren && (
        <div className={styles.refChildren}>
          {(target.references ?? []).map((childEdge) => (
            <ReferenceNode
              key={`${childEdge.fieldName}:${childEdge.target.collection}:${childEdge.target.pathId}`}
              edge={childEdge}
              depth={depth + 1}
              instanceLookup={instanceLookup}
              allReferencedBy={allReferencedBy}
              onNavigate={onNavigate}
            />
          ))}
        </div>
      )}
    </div>
  );
}

// --- Member row ---

interface MemberRowProps {
  member: TemplateMember;
  valueNode: InspectedFieldNode | null;
  parentTypeName: string;
  fieldPath: string;
  instanceLookup: ReadonlyMap<string, TemplateInstanceEntry>;
  onNavigate: (key: string) => void;
}

function MemberRow({
  member,
  valueNode,
  parentTypeName,
  fieldPath,
  instanceLookup,
  onNavigate,
}: MemberRowProps) {
  const [expanded, setExpanded] = useState(false);
  const [dragging, setDragging] = useState(false);

  const isExpandable =
    member.isScalar !== true ||
    member.isCollection === true ||
    member.isTemplateReference === true ||
    valueNode?.kind === "object" ||
    valueNode?.kind === "array" ||
    valueNode?.kind === "matrix";

  const handleToggle = useCallback(() => setExpanded((prev) => !prev), []);

  const tags: string[] = [];
  if (!member.isWritable) tags.push("read-only");
  if (member.isLikelyOdinOnly) tags.push("odin");
  if (member.isCollection) tags.push("collection");
  if (member.isTemplateReference) tags.push("ref");

  const isDraggable = member.isWritable || member.isCollection === true;
  const isNamedArray = member.namedArrayEnumTypeName !== undefined && valueNode?.kind === "array";
  // Both label maps come from the schema query's inlined enumMembers.
  const namedArrayLabelMap = isNamedArray ? buildNamedArrayLabelMap(member.enumMembers) : null;
  // For enum-leaf scalars (member.enumTypeName set, value is the numeric
  // index), provide the value→name lookup so renderValueLine can surface the
  // enum member name instead of the bare integer.
  const enumLabelMap = member.enumTypeName ? buildNamedArrayLabelMap(member.enumMembers) : null;

  return (
    <div className={styles.memberBlock}>
      {/* --- Header row --- */}
      <div
        className={`${styles.memberHeader} ${isExpandable ? styles.memberHeaderExpandable : ""} ${dragging ? styles.rowDragging : ""}`}
        {...(isExpandable && {
          role: "button",
          tabIndex: 0,
          "aria-expanded": expanded,
          onClick: handleToggle,
          onKeyDown: onKeyActivate(handleToggle),
        })}
        {...(isDraggable && {
          draggable: true,
          onDragStart: (e: React.DragEvent) => {
            e.dataTransfer.effectAllowed = "copy";
            const payload: Record<string, unknown> = {
              templateType: parentTypeName,
              fieldPath,
              typeName: member.typeName,
            };
            if (member.patchScalarKind !== undefined)
              payload.patchScalarKind = member.patchScalarKind;
            if (member.elementTypeName !== undefined)
              payload.elementTypeName = member.elementTypeName;
            if (member.enumTypeName !== undefined) payload.enumTypeName = member.enumTypeName;
            if (member.referenceTypeName !== undefined)
              payload.referenceTypeName = member.referenceTypeName;
            if (member.isCollection !== undefined) payload.isCollection = member.isCollection;
            if (member.isScalar !== undefined) payload.isScalar = member.isScalar;
            if (member.isTemplateReference !== undefined)
              payload.isTemplateReference = member.isTemplateReference;
            if (member.namedArrayEnumTypeName !== undefined)
              payload.namedArrayEnumTypeName = member.namedArrayEnumTypeName;
            e.dataTransfer.setData(
              "text/plain",
              encodeCrossMemberPayload(payload as Parameters<typeof encodeCrossMemberPayload>[0]),
            );
            e.dataTransfer.setData(TEMPLATE_DRAG_TAG, "1");
            e.dataTransfer.setData(MEMBER_DRAG_TAG, "1");
            attachDragChip(e, fieldPath);
            beginTemplateDrag({ kind: "member", templateType: parentTypeName, fieldPath });
            setDragging(true);
          },
          onDragEnd: () => {
            setDragging(false);
            endTemplateDrag();
          },
        })}
      >
        {isDraggable ? (
          <span className={styles.rowDragGrip} aria-hidden>
            <GripVertical size={10} />
          </span>
        ) : (
          <span className={styles.rowDragGripSpacer} aria-hidden />
        )}
        {isExpandable ? (
          <button
            type="button"
            className={`${styles.memberExpander} ${expanded ? styles.memberExpanderOpen : ""}`}
            aria-label={expanded ? "Collapse" : "Expand"}
          >
            ▸
          </button>
        ) : null}
        <span className={styles.memberName}>{member.name}</span>
        <span className={styles.memberType}>{member.typeName}</span>
        {tags.length > 0 && (
          <span className={styles.memberTags}>
            {tags.map((t) => (
              <span key={t} className={styles.memberTag}>
                {t}
              </span>
            ))}
          </span>
        )}
      </div>

      {/* --- Value row(s) --- */}
      {valueNode && !expanded && (
        <div className={styles.memberValue}>
          {renderValueLine(valueNode, member, namedArrayLabelMap, enumLabelMap)}
        </div>
      )}

      {/* --- Expanded detail --- */}
      {expanded && (
        <div className={styles.memberDetail}>
          {/* Named array: show labelled rows with collapsible nested fields */}
          {isNamedArray && valueNode.elements && valueNode.elements.length > 0 && (
            <div className={styles.memberNestedList}>
              {valueNode.elements.map((elem, idx) => {
                const label = namedArrayLabelMap?.[idx] ?? `[${idx}]`;
                const elemType = member.elementTypeName ?? "byte";
                const elemSubFields: readonly InspectedFieldNode[] | null =
                  elem.kind === "object" && elem.fields && elem.fields.length > 0
                    ? elem.fields
                    : null;
                return (
                  <ExpandableElementRow
                    // eslint-disable-next-line @eslint-react/no-array-index-key -- idx is the named-array attribute index, not iteration order; stable across renders.
                    key={idx}
                    elem={elem}
                    label={label}
                    typeName={elemType}
                    subFields={elemSubFields}
                    instanceLookup={instanceLookup}
                    onNavigate={onNavigate}
                  />
                );
              })}
            </div>
          )}

          {/* Referenced template: show link */}
          {valueNode?.kind === "reference" && valueNode.reference && (
            <div className={styles.memberFieldInfo}>
              <span className={styles.fieldInfoItem}>
                Target:{" "}
                <strong>
                  {renderReferenceLink(valueNode.reference, instanceLookup, onNavigate)}
                </strong>
              </span>
            </div>
          )}

          {/* Object: show nested fields with values */}
          {valueNode?.kind === "object" && valueNode.fields && valueNode.fields.length > 0 && (
            <div className={styles.memberNestedList}>
              {valueNode.fields.map((subVal, i) => (
                <div key={subVal.name ?? `unnamed-${i}`} className={styles.memberNestedRow}>
                  <span className={styles.chevronSpacer} />
                  <div className={styles.memberNestedScroll}>
                    <span className={styles.memberNestedLabel}>{subVal.name ?? "<unnamed>"}</span>
                    <span className={styles.memberNestedType}>{subVal.fieldTypeName ?? ""}</span>
                    <span className={styles.memberNestedValue}>{formatValue(subVal)}</span>
                  </div>
                </div>
              ))}
            </div>
          )}

          {/* Array of complex elements: show expandable rows */}
          {valueNode?.kind === "array" &&
            !isNamedArray &&
            valueNode.elements &&
            valueNode.elements.length > 0 && (
              <div className={styles.memberNestedList}>
                {valueNode.elements.map((elem, idx) => {
                  const arrSubFields: readonly InspectedFieldNode[] | null =
                    elem.kind === "object" && elem.fields && elem.fields.length > 0
                      ? elem.fields
                      : null;
                  return (
                    <ExpandableElementRow
                      // eslint-disable-next-line @eslint-react/no-array-index-key -- idx is the array's semantic position in a fixed-shape inspection result, not iteration order.
                      key={idx}
                      elem={elem}
                      label={`[${idx}]`}
                      typeName={elem.fieldTypeName ?? ""}
                      subFields={arrSubFields}
                      instanceLookup={instanceLookup}
                      onNavigate={onNavigate}
                    />
                  );
                })}
                {valueNode.truncated && (
                  <div className={styles.memberNestedTruncated}>
                    <span>… truncated (total {valueNode.count ?? "?"})</span>
                  </div>
                )}
              </div>
            )}

          {/* Multi-dim matrix: render as a 2D grid (3D+ falls back to flat). */}
          {valueNode?.kind === "matrix" &&
            valueNode.dimensions?.length === 2 &&
            valueNode.elements && (
              <MatrixGrid
                rows={valueNode.dimensions[0] ?? 0}
                cols={valueNode.dimensions[1] ?? 0}
                cells={valueNode.elements}
              />
            )}

          {/* Scalar value with depth, when already at leaf */}
          {valueNode && valueNodeKindIsScalar(valueNode) && (
            <div className={styles.memberFieldInfo}>
              <span className={styles.fieldInfoItem}>
                Value: <strong>{formatValue(valueNode)}</strong>
              </span>
            </div>
          )}
        </div>
      )}
    </div>
  );
}

/**
 * Read-only 2D grid renderer for `kind: "matrix"` nodes from the Odin
 * decoder (e.g. AOETiles' `bool[9,9]`). Cells are flat row-major in
 * `cells`; we reshape to rows×cols at render time. The visual editor's
 * write path uses the same shape via cell-addressed Set directives.
 */
function MatrixGrid({
  rows,
  cols,
  cells,
}: {
  rows: number;
  cols: number;
  cells: readonly InspectedFieldNode[];
}) {
  if (rows <= 0 || cols <= 0 || cells.length !== rows * cols) {
    return (
      <div className={styles.memberFieldInfo}>
        <span className={styles.fieldInfoItem}>
          (matrix shape mismatch: {rows}×{cols} declared, {cells.length} cells)
        </span>
      </div>
    );
  }
  return (
    <div className={styles.matrixGrid}>
      {Array.from({ length: rows }, (_, r) => (
        <div key={r} className={styles.matrixRow}>
          {Array.from({ length: cols }, (_, c) => {
            const cell = cells[r * cols + c];
            return (
              <span
                key={c}
                className={`${styles.matrixCell} ${matrixCellTone(cell)}`}
                title={`[${r},${c}] ${formatMatrixCell(cell)}`}
              >
                {formatMatrixCell(cell)}
              </span>
            );
          })}
        </div>
      ))}
    </div>
  );
}

function matrixCellTone(cell: InspectedFieldNode | undefined): string {
  if (!cell) return "";
  if (cell.kind === "bool")
    return cell.value === true ? styles.matrixCellTrue : styles.matrixCellFalse;
  return "";
}

// --- Value rendering helpers ---

function renderValueLine(
  value: InspectedFieldNode,
  member: TemplateMember,
  namedArrayLabelMap: Record<number, string> | null,
  enumLabelMap: Record<number, string> | null,
): React.ReactNode {
  if (value.null) {
    return <span className={styles.valueNull}>null</span>;
  }

  // Named array: show summarised preview in collapsed state.
  if (
    member.namedArrayEnumTypeName &&
    value.kind === "array" &&
    value.elements &&
    value.elements.length > 0
  ) {
    const preview = value.elements.slice(0, 3);
    const rest = value.elements.length - preview.length;
    return (
      <span className={styles.valueArray}>
        {preview.map((elem, idx) => {
          const label = namedArrayLabelMap?.[idx] ?? `[${idx}]`;
          // eslint-disable-next-line @typescript-eslint/no-base-to-string, @typescript-eslint/restrict-template-expressions -- guarded field preview.
          return `${label}=${elem.value ?? "?"}${idx < preview.length - 1 ? ", " : ""}`;
        })}
        {rest > 0 && ` (+${rest} more)`}
      </span>
    );
  }

  if (value.kind === "array") {
    const count = value.count ?? value.elements?.length ?? 0;
    if (count === 0) return <span className={styles.valueEmpty}>[ ]</span>;
    return <span className={styles.valueArray}>{formatValue(value)}</span>;
  }

  if (value.kind === "object") {
    return <span className={styles.valueObject}>{formatValue(value)}</span>;
  }

  if (value.kind === "reference") {
    return <span className={styles.valueRef}>{formatValue(value)}</span>;
  }

  if (value.kind === "string" || typeof value.value === "string") {
    return <span className={styles.valueString}>{formatValue(value)}</span>;
  }

  if (value.value !== undefined && value.value !== null) {
    // Enum-leaf field: the inspector returns the numeric index; surface the
    // member name when we can resolve it, with the raw value alongside as a
    // disambiguator for the modder. Falls through to the plain scalar render
    // when the enum member set hasn't loaded yet or the value isn't defined.
    const enumLabel = resolveEnumLeafLabel(value.value, enumLabelMap);
    if (enumLabel !== null) {
      return (
        <span className={styles.valueScalar}>
          {enumLabel}
          {/* eslint-disable-next-line @typescript-eslint/no-base-to-string -- numeric scalar */}
          <span className={styles.valueEnumIndex}> ({String(value.value)})</span>
        </span>
      );
    }
    // eslint-disable-next-line @typescript-eslint/no-base-to-string -- String() handles unknown at runtime.
    return <span className={styles.valueScalar}>{String(value.value)}</span>;
  }

  return <span className={styles.valueUnknown}>?</span>;
}

function renderReferenceLink(
  reference: InspectedReference,
  instanceLookup: ReadonlyMap<string, TemplateInstanceEntry>,
  onNavigate: (key: string) => void,
): React.ReactNode {
  if (!reference.pathId) return <span>{reference.name ?? "null"}</span>;
  // Find the referenced template in the index by matching name + className,
  // since the reference's fileId is a dependency index, not a collection name.
  let targetKey: string | null = null;
  for (const [k, inst] of instanceLookup) {
    if (
      inst.identity.pathId === reference.pathId &&
      (reference.name === undefined || inst.name === reference.name)
    ) {
      targetKey = k;
      break;
    }
  }
  const target = targetKey ? instanceLookup.get(targetKey) : undefined;
  const label = target
    ? `${target.className}:${target.name}`
    : (reference.name ?? reference.className ?? `[pathId=${reference.pathId}]`);
  return (
    <button
      type="button"
      className={styles.refLink}
      onClick={(e) => {
        e.stopPropagation();
        if (targetKey) onNavigate(targetKey);
      }}
    >
      {label}
    </button>
  );
}

// --- Expandable element row (used by both named and regular arrays) ---

function ExpandableElementRow({
  elem,
  label,
  typeName,
  subFields,
  instanceLookup,
  onNavigate,
}: {
  elem: InspectedFieldNode;
  label: string;
  typeName: string;
  subFields: readonly InspectedFieldNode[] | null;
  instanceLookup: ReadonlyMap<string, TemplateInstanceEntry>;
  onNavigate: (key: string) => void;
}) {
  const [subExpanded, setSubExpanded] = useState(false);
  const hasChildren = subFields !== null;

  // When the element resolves to a known template, render the value as a
  // navigable link instead of plain text. Covers reference-array fields like
  // SkillTemplate.EventHandlers (List<SkillEventHandlerTemplate>) and
  // SkillGroup.Skills (List<SkillTemplate>).
  const referenceLink =
    elem.kind === "reference" && elem.reference
      ? renderReferenceLink(elem.reference, instanceLookup, onNavigate)
      : null;

  return (
    <div>
      <div
        className={`${styles.memberNestedRow} ${hasChildren ? styles.memberHeaderExpandable : ""}`}
        {...(hasChildren && {
          role: "button",
          tabIndex: 0,
          "aria-expanded": subExpanded,
          onClick: () => setSubExpanded(!subExpanded),
          onKeyDown: onKeyActivate(() => setSubExpanded(!subExpanded)),
        })}
      >
        {hasChildren ? (
          <button
            type="button"
            className={`${styles.chevron} ${subExpanded ? styles.chevronOpen : ""}`}
            aria-label={subExpanded ? "Collapse" : "Expand"}
            tabIndex={-1}
          >
            ▸
          </button>
        ) : (
          <span className={styles.chevronSpacer} />
        )}
        <div className={styles.memberNestedScroll}>
          <span className={styles.memberNestedLabel}>{label}</span>
          <span className={styles.memberNestedType}>{typeName}</span>
          <span className={styles.memberNestedValue}>{referenceLink ?? formatValue(elem)}</span>
        </div>
      </div>
      {subExpanded && subFields && (
        <div className={styles.memberNestedSubList}>
          {subFields.map((f, i) => (
            <NestedValueRow key={f.name ?? `unnamed-${i}`} value={f} />
          ))}
        </div>
      )}
    </div>
  );
}

// --- Recursive nested value row ---

function NestedValueRow({ value }: { value: InspectedFieldNode }) {
  const [open, setOpen] = useState(false);
  const children =
    value.kind === "object" && value.fields && value.fields.length > 0 ? value.fields : null;
  const hasChildren = children !== null;

  return (
    <div>
      <div
        className={`${styles.memberNestedSubRow} ${hasChildren ? styles.memberHeaderExpandable : ""}`}
        {...(hasChildren && {
          role: "button",
          tabIndex: 0,
          "aria-expanded": open,
          onClick: () => setOpen(!open),
          onKeyDown: onKeyActivate(() => setOpen(!open)),
        })}
      >
        {hasChildren ? (
          <button
            type="button"
            className={`${styles.chevron} ${open ? styles.chevronOpen : ""}`}
            aria-label={open ? "Collapse" : "Expand"}
            tabIndex={-1}
          >
            ▸
          </button>
        ) : (
          <span className={styles.chevronSpacer} />
        )}
        <span className={styles.memberNestedLabel}>{value.name ?? "?"}</span>
        <span className={styles.memberNestedType}>{value.fieldTypeName ?? ""}</span>
        <span className={styles.memberNestedValue}>{formatValue(value)}</span>
      </div>
      {open && children && (
        <div className={styles.memberNestedSubList}>
          {children.map((c, i) => (
            <NestedValueRow key={c.name ?? `unnamed-${i}`} value={c} />
          ))}
        </div>
      )}
    </div>
  );
}
