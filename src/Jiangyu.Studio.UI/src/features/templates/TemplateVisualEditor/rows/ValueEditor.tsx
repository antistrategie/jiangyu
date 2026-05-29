import { useCallback, useEffect, useState } from "react";
import {
  rpcCall,
  type EnumMemberEntry,
  type TemplateConversationRolesResult,
  type TemplateMember,
} from "@shared/rpc";
import { useConversationSource } from "../store";
import type { EditorValue } from "../types";
import {
  resolveEnumCommitType,
  shouldShowRefTypeSelector,
  resolveRefTypeDisplay,
} from "../helpers";
import { CommitInput } from "../shared/CommitInput";
import { SuggestionCombobox, type SuggestionItem } from "../shared/SuggestionCombobox";
import {
  getCachedTemplateTypes,
  getCachedProjectClones,
  getCachedProjectAdditions,
  templatesSearch,
} from "../shared/rpcHelpers";
import { assetsSearch } from "@features/assets/assets";
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
// member. Reads enum entries from the schema query's inlined
// `member.enumMembers`; falls back to a numeric input when the schema
// didn't resolve them (e.g. a synthesised cross-drag member).

export interface NamedArrayIndexPickerProps {
  entries: readonly EnumMemberEntry[] | null | undefined;
  index: number;
  onChange: (index: number) => void;
}

export function NamedArrayIndexPicker({ entries, index, onChange }: NamedArrayIndexPickerProps) {
  if (!entries || entries.length === 0) {
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
  // RoleGuid: surface the source conversation's role names as a
  // combobox. Field is HashableId Int32 but the modder authors the
  // role-name string; the validator (RoleGuidResolver) maps name → int
  // at compile.
  if (member?.name === "RoleGuid" && (value.kind === "String" || value.kind === "Int32")) {
    return <RoleGuidValueEditor value={value} onChange={onChange} />;
  }
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

    case "String": {
      // Single-line input by default; promote to an auto-growing textarea
      // when the value already contains newlines so multi-line strings
      // round-trip without collapsing. Source-view authoring with
      // `"""..."""` flips the visual editor onto the textarea path on
      // next load.
      const stringVal = value.string ?? "";
      const multiline = stringVal.includes("\n");
      if (multiline) {
        return (
          <CommitInput
            multiline
            className={styles.setValueInput}
            value={stringVal}
            onCommit={(v) => onChange({ kind: "String", string: v })}
          />
        );
      }
      return (
        <CommitInput
          type="text"
          className={styles.setValueInput}
          value={stringVal}
          onCommit={(v) => onChange({ kind: "String", string: v })}
        />
      );
    }

    case "Enum":
      return <EnumValueEditor value={value} onChange={onChange} member={member} />;

    case "TemplateReference":
      return <RefValueEditor value={value} onChange={onChange} member={member} />;

    case "AssetReference":
      return <AssetValueEditor value={value} onChange={onChange} member={member} />;

    case "Composite":
    case "TypeConstruction":
      // Field-bag values are rendered by CompositeEditor at SetRow level.
      return null;

    case "Null":
      return <span className={styles.setValueNull}>null</span>;

    default:
      return <span className={styles.setKind}>?</span>;
  }
}

function AssetValueEditor({ value, onChange, member }: ValueEditorProps) {
  // Asset references carry only the logical name (the path under
  // assets/additions/<category>/ with the extension stripped). Category is
  // derived from the destination field's Unity type at apply time, so the
  // editor surfaces a name input plus a category hint label, with a
  // picker that mirrors the template-reference one: project additions
  // first (tagged so the modder can tell their own files from vanilla
  // ones), then any same-type vanilla game asset, deduped by name.
  const unityType = member?.typeName ?? "";
  const fetchAssetSuggestions = useCallback(async (): Promise<readonly SuggestionItem[]> => {
    if (!unityType) return [];
    const [additions, gameAssets] = await Promise.all([
      getCachedProjectAdditions(unityType),
      assetsSearch({ kind: unityType, limit: 5_000 }).catch(() => []),
    ]);
    const additionItems: SuggestionItem[] = additions.map((a) => ({
      label: a.name,
      tag: "addition",
    }));
    // Dedup vanilla suggestions against additions and against each other —
    // the asset index can list the same logical name in multiple
    // collections, and an addition shadowing a vanilla asset is always
    // intentional, so additions win.
    const seen = new Set(additionItems.map((i) => i.label));
    const gameItems: SuggestionItem[] = [];
    for (const entry of gameAssets) {
      if (!entry.name || seen.has(entry.name)) continue;
      seen.add(entry.name);
      gameItems.push({ label: entry.name });
    }
    return [...additionItems, ...gameItems];
  }, [unityType]);

  return (
    <div className={styles.setRefRow}>
      <span className={styles.setRefLabel}>{unityType || "asset"}</span>
      <SuggestionCombobox
        value={value.assetName ?? ""}
        placeholder="path/to/asset"
        fetchSuggestions={fetchAssetSuggestions}
        onChange={(v) => onChange({ kind: "AssetReference", assetName: v })}
      />
    </div>
  );
}

function EnumValueEditor({ value, onChange, member }: ValueEditorProps) {
  // Mirrors RefValueEditor: when the catalog supplies the declared enum type
  // we hide the "enum"/type chrome entirely and only show the value picker.
  // The placeholder carries the type hint instead. C# enums are sealed so
  // there's no polymorphic case; the type label only appears as a fallback
  // when the catalog couldn't resolve a declared type (rare).
  const declaredEnumType = member?.enumTypeName ?? "";
  const showTypeSpan = declaredEnumType === "";
  const enumMembers = member?.enumMembers;
  const fetchEnumValues = useCallback(
    () => Promise.resolve(enumMembers?.map((e) => e.name) ?? []),
    [enumMembers],
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

// --- RoleGuidValueEditor ---
//
// SAY/CHOICE nodes carry RoleGuid as an Int32 field referencing a
// Role.Guid in the enclosing ConversationTemplate's Roles list. The
// modder authors the role NAME as a string; RoleGuidResolver compiles
// it to the int. This editor surfaces the source conversation's role
// names (looked up via ConversationSourceContext + templatesConversationRoles)
// as a combobox so the modder never has to remember a Guid.
//
// Falls back to free text when the editor isn't inside a
// ConversationTemplate card or the lookup hasn't returned (e.g. the
// asset index hasn't been built yet). Free-text values still validate
// at compile time.
interface RoleGuidValueEditorProps {
  readonly value: EditorValue;
  readonly onChange: (value: EditorValue) => void;
}

function RoleGuidValueEditor({ value, onChange }: RoleGuidValueEditorProps) {
  const sourceId = useConversationSource();
  const [roles, setRoles] = useState<readonly { name: string; guid: number }[]>([]);

  useEffect(() => {
    if (!sourceId) return;
    let cancelled = false;
    void rpcCall<TemplateConversationRolesResult>("templatesConversationRoles", {
      templateId: sourceId,
    })
      .then((r) => {
        if (cancelled) return;
        setRoles(r.roles);
      })
      .catch(() => {
        if (cancelled) return;
        setRoles([]);
      });
    return () => {
      cancelled = true;
    };
  }, [sourceId]);

  const display = value.kind === "String" ? (value.string ?? "") : String(value.int32 ?? 0);

  // Map the current int Guid back to a known role name so re-opening a
  // post-compile KDL (where RoleGuid is already Int32) shows a name in
  // the combobox instead of a raw number.
  const intDisplay =
    value.kind === "Int32" ? (roles.find((r) => r.guid === value.int32)?.name ?? display) : display;

  const fetchSuggestions = useCallback(
    (): Promise<readonly SuggestionItem[]> =>
      Promise.resolve(
        roles.map((r) => ({
          value: r.name,
          label: r.name,
          detail: String(r.guid),
        })),
      ),
    [roles],
  );

  return (
    <SuggestionCombobox
      value={intDisplay}
      placeholder={sourceId ? "Role name" : "Role name (int Guid)"}
      fetchSuggestions={fetchSuggestions}
      onChange={(name) => onChange({ kind: "String", string: name })}
    />
  );
}
