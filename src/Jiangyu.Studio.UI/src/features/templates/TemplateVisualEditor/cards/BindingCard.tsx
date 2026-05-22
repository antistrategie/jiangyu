import React, { memo, useCallback, useMemo } from "react";
import { GripVertical, X } from "lucide-react";
import { SuggestionCombobox, type SuggestionItem } from "../shared/SuggestionCombobox";
import { getCachedProjectClones, templatesSearch } from "../shared/rpcHelpers";
import { useEditorDispatch, useNodeIndex } from "../store";
import type { StampedNode } from "../helpers";
import type { EditorBindAttribute } from "../types";
import styles from "../TemplateVisualEditor.module.css";

// --- Binding-kind schema -----------------------------------------------
//
// The compiler is the source of truth for which kinds exist and what
// attributes they require (`KdlTemplateParser.TryParseBindNode`). We
// mirror the table here so the visual editor can render the right
// attribute fields per kind. New kinds added to the compiler require an
// entry below; if a file references an unknown kind the editor still
// loads it and renders attributes as free-form strings so no data is
// lost on save.

interface BindAttributeSchema {
  readonly name: string;
  /** Template type filter for picker suggestions. Empty = free-form string. */
  readonly templateType?: string;
}

interface BindKindSchema {
  readonly kind: string;
  readonly attributes: readonly BindAttributeSchema[];
}

const BIND_KIND_SCHEMAS: readonly BindKindSchema[] = [
  {
    kind: "leader_armor",
    attributes: [
      { name: "leader", templateType: "UnitLeaderTemplate" },
      { name: "armor", templateType: "ArmorTemplate" },
    ],
  },
];

const BIND_KINDS: readonly string[] = BIND_KIND_SCHEMAS.map((s) => s.kind);

function schemaForKind(kind: string | undefined): BindKindSchema | undefined {
  return BIND_KIND_SCHEMAS.find((s) => s.kind === kind);
}

const EMPTY_SUGGESTIONS: readonly SuggestionItem[] = [];
const EMPTY_SUGGESTIONS_FETCH = (): Promise<readonly SuggestionItem[]> =>
  Promise.resolve(EMPTY_SUGGESTIONS);

// --- BindingCard --------------------------------------------------------

export interface BindingCardProps {
  node: StampedNode;
  isDragging: boolean;
  onDragStart: () => void;
  onDragEnd: () => void;
  onDragOverCard: (e: React.DragEvent) => void;
  onDropCard: () => void;
}

function BindingCardImpl({
  node,
  isDragging,
  onDragStart,
  onDragEnd,
  onDragOverCard,
  onDropCard,
}: BindingCardProps) {
  const dispatch = useEditorDispatch();
  const nodeIndex = useNodeIndex();

  const schema = useMemo(() => schemaForKind(node.bindKind), [node.bindKind]);
  // Render free-form name+value pairs when the kind isn't in the local
  // schema table (forward-compat with newer compiler kinds). Modder can
  // edit values; renaming the kind to a known one swaps the renderer.
  const attrs = useMemo<readonly EditorBindAttribute[]>(
    () => node.bindAttributes ?? [],
    [node.bindAttributes],
  );

  const updateAttribute = useCallback(
    (name: string, value: string) => {
      const existing = node.bindAttributes ?? [];
      // Preserve order; replace in place if present, append if new.
      const idx = existing.findIndex((a) => a.name === name);
      const next: EditorBindAttribute[] =
        idx >= 0
          ? existing.map((a, i) => (i === idx ? { name, value } : a))
          : [...existing, { name, value }];
      dispatch({ type: "updateNode", nodeIndex, node: { ...node, bindAttributes: next } });
    },
    [node, nodeIndex, dispatch],
  );

  const setKind = useCallback(
    (kind: string) => {
      const target = schemaForKind(kind);
      // When swapping to a known kind, seed missing required attributes
      // with empty values so the modder sees the expected fields.
      // Existing attribute values are preserved if names match.
      const existing = new Map((node.bindAttributes ?? []).map((a) => [a.name, a.value]));
      const seeded: EditorBindAttribute[] = target
        ? target.attributes.map((spec) => ({
            name: spec.name,
            value: existing.get(spec.name) ?? "",
          }))
        : (node.bindAttributes ?? []);
      dispatch({
        type: "updateNode",
        nodeIndex,
        node: { ...node, bindKind: kind, bindAttributes: seeded },
      });
    },
    [node, nodeIndex, dispatch],
  );

  const fetchKindSuggestions = useCallback(
    (): Promise<readonly SuggestionItem[]> =>
      Promise.resolve(BIND_KINDS.map((k) => ({ label: k }))),
    [],
  );

  // Attribute value picker: project clones of the right type first,
  // then same-typed vanilla instances. Matches the NodeCard fetchInstances
  // pattern so the dropdown feels consistent.
  const fetchAttributeValueSuggestions = useCallback(
    (templateType: string | undefined): (() => Promise<readonly SuggestionItem[]>) => {
      if (!templateType) return EMPTY_SUGGESTIONS_FETCH;
      return async () => {
        const [searchResult, projectClones] = await Promise.all([
          templatesSearch(templateType),
          getCachedProjectClones(),
        ]);
        const gameItems: SuggestionItem[] = searchResult.instances.map((i) => ({ label: i.name }));
        const gameLabels = new Set(gameItems.map((i) => i.label));
        const cloneItems: SuggestionItem[] = projectClones
          .filter((c) => c.templateType === templateType && !gameLabels.has(c.id))
          .map((c) => ({ label: c.id, tag: "clone" }));
        return [...cloneItems, ...gameItems];
      };
    },
    [],
  );

  const attrByName = useMemo(() => new Map(attrs.map((a) => [a.name, a.value])), [attrs]);

  // Stable single-line fetcher per (schema row), so the SuggestionCombobox
  // doesn't rebuild its cached suggestion list on every keystroke.
  const fetchersByAttr = useMemo(() => {
    const map = new Map<string, () => Promise<readonly SuggestionItem[]>>();
    if (!schema) return map;
    for (const spec of schema.attributes) {
      map.set(spec.name, fetchAttributeValueSuggestions(spec.templateType));
    }
    return map;
  }, [schema, fetchAttributeValueSuggestions]);

  return (
    <div
      className={`${styles.card} ${isDragging ? styles.cardDragging : ""}`}
      role="presentation"
      onDragEnter={(e) => {
        if (e.dataTransfer.types.includes("application/x-jiangyu-card-reorder")) {
          e.preventDefault();
        }
      }}
      onDragOver={onDragOverCard}
      onDrop={(e) => {
        e.preventDefault();
        onDropCard();
      }}
    >
      <div className={styles.cardHeader}>
        <span
          className={styles.dragGrip}
          role="presentation"
          draggable
          onDragStart={(e) => {
            e.stopPropagation();
            e.dataTransfer.effectAllowed = "move";
            e.dataTransfer.setData("application/x-jiangyu-card-reorder", node._uiId);
            onDragStart();
          }}
          onDragEnd={onDragEnd}
          title="Drag to reorder"
        >
          <GripVertical size={12} />
        </span>
        <span className={`${styles.cardBadge} ${styles.cardBadgeBind}`}>bind</span>
        <SuggestionCombobox
          value={node.bindKind ?? ""}
          placeholder="Kind"
          fetchSuggestions={fetchKindSuggestions}
          onChange={setKind}
          className={styles.cardTypeInput}
        />
        {schema?.attributes.map((spec) => (
          <React.Fragment key={spec.name}>
            <span className={styles.cardProp}>{spec.name}</span>
            <SuggestionCombobox
              value={attrByName.get(spec.name) ?? ""}
              placeholder={spec.templateType ? `${spec.templateType} id` : "value"}
              fetchSuggestions={fetchersByAttr.get(spec.name) ?? EMPTY_SUGGESTIONS_FETCH}
              onChange={(v) => updateAttribute(spec.name, v)}
              className={styles.cardIdInput}
            />
          </React.Fragment>
        ))}
        {!schema &&
          attrs.map((a) => (
            <React.Fragment key={a.name}>
              <span className={styles.cardProp}>{a.name}</span>
              <SuggestionCombobox
                value={a.value}
                placeholder="value"
                fetchSuggestions={EMPTY_SUGGESTIONS_FETCH}
                onChange={(v) => updateAttribute(a.name, v)}
                className={styles.cardIdInput}
              />
            </React.Fragment>
          ))}
        <button
          type="button"
          className={styles.cardDelete}
          onClick={(e) => {
            e.stopPropagation();
            dispatch({ type: "deleteNode", nodeIndex });
          }}
          title="Remove binding"
        >
          <X size={14} />
        </button>
      </div>
    </div>
  );
}

// Memoise to match NodeCard's behaviour — sibling-edit re-renders stay
// out of bind cards whose props are identity-stable.
export const BindingCard = memo(BindingCardImpl);
