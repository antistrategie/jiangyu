import { useState, useEffect, useCallback } from "react";
import type { EnumMemberEntry, TemplateMember } from "@lib/rpc";
import type { EditorValue } from "../types";
import {
  resolveEnumCommitType,
  shouldShowRefTypeSelector,
  resolveRefTypeDisplay,
} from "../helpers";
import { CommitInput } from "../shared/CommitInput";
import { SuggestionCombobox, type SuggestionItem } from "../shared/SuggestionCombobox";
import {
  getCachedEnumMembers,
  getCachedEnumEntries,
  getCachedTemplateTypes,
  getCachedProjectClones,
  templatesSearch,
} from "../shared/rpcHelpers";
import styles from "../TemplateVisualEditor.module.css";

// --- RangeHint ---

export interface RangeHintProps {
  readonly min: number | null | undefined;
  readonly max: number | null | undefined;
}

export function RangeHint({ min, max }: RangeHintProps) {
  if (min == null && max == null) return null;
  let text: string;
  if (min != null && max != null) {
    text = `${min}\u2013${max}`;
  } else if (min != null) {
    text = `\u2265${min}`;
  } else if (max != null) {
    text = `\u2264${max}`;
  } else {
    // Unreachable — the first guard already returned for (null, null).
    return null;
  }
  return <span className={styles.rangeHint}>{text}</span>;
}

// --- NamedArrayIndexPicker ---
//
// Dropdown picker for `set "Field" index=N` on a [NamedArray(typeof(T))]
// member. Fetches the paired enum's members via the cached
// templatesEnumMembers RPC and labels each option by name; stores the
// ordinal value on the directive.

export interface NamedArrayIndexPickerProps {
  enumTypeName: string;
  index: number;
  onChange: (index: number) => void;
}

export function NamedArrayIndexPicker({
  enumTypeName,
  index,
  onChange,
}: NamedArrayIndexPickerProps) {
  const [entries, setEntries] = useState<readonly EnumMemberEntry[] | null>(null);
  useEffect(() => {
    let cancelled = false;
    void getCachedEnumEntries(enumTypeName).then((m) => {
      if (!cancelled) setEntries(m);
    });
    return () => {
      cancelled = true;
    };
  }, [enumTypeName]);

  if (entries === null || entries.length === 0) {
    // Fall back to numeric while loading, or when the enum isn't resolvable.
    return (
      <CommitInput
        type="number"
        className={styles.setIndexInput}
        value={index}
        min={0}
        step={1}
        onCommit={(v) => onChange(Number(v))}
      />
    );
  }

  return (
    <select
      className={styles.setIndexSelect}
      value={index}
      onChange={(e) => onChange(Number(e.target.value))}
    >
      {entries.map((entry) => (
        <option key={entry.name} value={entry.value}>
          {entry.name}
        </option>
      ))}
    </select>
  );
}

// --- ValueEditor ---

export interface ValueEditorProps {
  value: EditorValue;
  onChange: (value: EditorValue) => void;
  member?: TemplateMember | undefined;
}

export function ValueEditor({ value, onChange, member }: ValueEditorProps) {
  switch (value.kind) {
    case "Boolean":
      return (
        <input
          type="checkbox"
          className={styles.setValueCheckbox}
          checked={value.boolean ?? false}
          onChange={(e) => onChange({ kind: "Boolean", boolean: e.target.checked })}
        />
      );

    case "Byte": {
      // [Range]/[Min] tightens the default 0..255 byte range; never widens it.
      const min = Math.max(0, member?.numericMin ?? 0);
      const max = Math.min(255, member?.numericMax ?? 255);
      const num = value.int32 ?? 0;
      const invalid = num < min || num > max || !Number.isInteger(num);
      return (
        <span className={styles.setValueInputWrap}>
          <CommitInput
            type="number"
            className={`${styles.setValueInput} ${invalid ? styles.setValueInvalid : ""}`}
            value={num}
            min={min}
            max={max}
            step={1}
            onCommit={(v) => onChange({ kind: "Byte", int32: Number(v) })}
          />
          <RangeHint min={min} max={max} />
        </span>
      );
    }

    case "Int32": {
      const num = value.int32 ?? 0;
      const min = member?.numericMin ?? undefined;
      const max = member?.numericMax ?? undefined;
      const invalid =
        !Number.isInteger(num) || (min != null && num < min) || (max != null && num > max);
      return (
        <span className={styles.setValueInputWrap}>
          <CommitInput
            type="number"
            className={`${styles.setValueInput} ${invalid ? styles.setValueInvalid : ""}`}
            value={num}
            min={min}
            max={max}
            step={1}
            onCommit={(v) => onChange({ kind: "Int32", int32: Number(v) })}
          />
          <RangeHint min={min} max={max} />
        </span>
      );
    }

    case "Single": {
      const num = value.single ?? 0;
      const min = member?.numericMin ?? undefined;
      const max = member?.numericMax ?? undefined;
      const invalid = (min != null && num < min) || (max != null && num > max);
      return (
        <span className={styles.setValueInputWrap}>
          <CommitInput
            type="number"
            className={`${styles.setValueInput} ${invalid ? styles.setValueInvalid : ""}`}
            value={num}
            min={min}
            max={max}
            step={0.01}
            onCommit={(v) => onChange({ kind: "Single", single: Number(v) })}
          />
          <RangeHint min={min} max={max} />
        </span>
      );
    }

    case "String":
      return (
        <CommitInput
          type="text"
          className={styles.setValueInput}
          value={value.string ?? ""}
          onCommit={(v) => onChange({ kind: "String", string: v })}
        />
      );

    case "Enum":
      return <EnumValueEditor value={value} onChange={onChange} member={member} />;

    case "TemplateReference":
      return <RefValueEditor value={value} onChange={onChange} member={member} />;

    case "Composite":
    case "HandlerConstruction":
      // Field-bag values are rendered by CompositeEditor at SetRow level.
      return null;

    default:
      return <span className={styles.setKind}>?</span>;
  }
}

function EnumValueEditor({ value, onChange, member }: ValueEditorProps) {
  // Mirrors RefValueEditor: when the catalog supplies the declared enum type
  // we hide the "enum"/type chrome entirely and only show the value picker.
  // The placeholder carries the type hint instead. C# enums are sealed so
  // there's no polymorphic case; the type label only appears as a fallback
  // when the catalog couldn't resolve a declared type (rare).
  const declaredEnumType = member?.enumTypeName ?? "";
  const showTypeSpan = declaredEnumType === "";
  const fetchEnumValues = useCallback(
    () => (declaredEnumType ? getCachedEnumMembers(declaredEnumType) : Promise.resolve([])),
    [declaredEnumType],
  );
  return (
    <div className={styles.setRefRow}>
      {showTypeSpan && (
        <>
          <span className={styles.setRefLabel}>enum</span>
          <span className={styles.setRefTypeInput}>{value.enumType ?? "?"}</span>
        </>
      )}
      <SuggestionCombobox
        value={value.enumValue ?? ""}
        placeholder={declaredEnumType ? `${declaredEnumType} value` : "Value"}
        fetchSuggestions={fetchEnumValues}
        onChange={(v) => {
          const next: EditorValue = { ...value, enumValue: v };
          const committedType = resolveEnumCommitType(declaredEnumType, value.enumType);
          if (committedType === undefined) delete next.enumType;
          else next.enumType = committedType;
          onChange(next);
        }}
      />
    </div>
  );
}

function RefValueEditor({ value, onChange, member }: ValueEditorProps) {
  const declaredRefType = member?.referenceTypeName ?? "";
  const isPolymorphic = member?.isReferenceTypePolymorphic === true;
  const explicitRefType = value.referenceType ?? "";
  const showTypeSelector = shouldShowRefTypeSelector(
    declaredRefType,
    isPolymorphic,
    explicitRefType,
  );
  const refType = resolveRefTypeDisplay(declaredRefType, isPolymorphic, explicitRefType);

  const fetchRefInstances = useCallback(async (): Promise<readonly SuggestionItem[]> => {
    if (!refType) return [];
    const [searchResult, projectClones] = await Promise.all([
      templatesSearch(refType),
      getCachedProjectClones(),
    ]);
    const gameItems: SuggestionItem[] = searchResult.instances.map((i) => ({ label: i.name }));
    const gameLabels = new Set(gameItems.map((i) => i.label));
    const cloneItems: SuggestionItem[] = projectClones
      .filter((c) => c.templateType === refType && !gameLabels.has(c.id))
      .map((c) => ({ label: c.id, tag: "clone" }));
    return [...cloneItems, ...gameItems];
  }, [refType]);

  return (
    <div className={styles.setRefRow}>
      {showTypeSelector && (
        <>
          <span className={styles.setRefLabel}>ref</span>
          <SuggestionCombobox
            value={refType}
            placeholder="Type"
            fetchSuggestions={getCachedTemplateTypes}
            onChange={(t) => {
              const next: EditorValue = { ...value };
              // For polymorphic destinations the explicit type IS the
              // disambiguation; never drop it, even when the modder happens
              // to pick a value that matches the (abstract) declared type.
              // For monomorphic destinations clearing or matching the
              // declared type is safe — the loader infers the type from
              // the field, so the explicit type is redundant.
              if (t === "") delete next.referenceType;
              else if (!isPolymorphic && t === declaredRefType) delete next.referenceType;
              else next.referenceType = t;
              onChange(next);
            }}
            className={styles.setRefTypeInput}
          />
        </>
      )}
      <SuggestionCombobox
        value={value.referenceId ?? ""}
        placeholder={refType ? `${refType} id` : "ID"}
        fetchSuggestions={fetchRefInstances}
        onChange={(id) => onChange({ ...value, referenceId: id })}
      />
    </div>
  );
}
