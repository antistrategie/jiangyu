import { useEffect, useState } from "react";
import type {
  EnumMemberEntry,
  EnumMembersResult,
  InspectedFieldNode,
  TemplateMember,
  TemplateSearchResult,
  TemplateValueResult,
} from "@lib/rpc";
import { rpcCall } from "@lib/rpc";
import type { CrossMemberPayload } from "@lib/drag/crossMember";
import type { DirectiveOp } from "../types";
import { inspectedFieldToEditorValue, makeDefaultValue, type StampedDirective } from "../helpers";
import { uiId } from "./uiId";

// --- RPC wrappers ---

export function templatesSearch(className?: string): Promise<TemplateSearchResult> {
  return rpcCall<TemplateSearchResult>("templatesSearch", className ? { className } : undefined);
}

export function templatesEnumMembers(typeName: string): Promise<EnumMembersResult> {
  return rpcCall<EnumMembersResult>("templatesEnumMembers", { typeName });
}

export function templatesParse(
  text: string,
): Promise<{ nodes: import("../types").EditorNode[]; errors: import("../types").EditorError[] }> {
  return rpcCall("templatesParse", { text });
}

export function templatesSerialise(
  document: import("../types").EditorDocument,
): Promise<{ text: string }> {
  return rpcCall<{ text: string }>("templatesSerialise", document);
}

// --- Caches ---

export const enumMembersCache = new Map<string, readonly EnumMemberEntry[]>();
export const templateTypesCache: { types: readonly string[] | null } = { types: null };

// Per-(typeName, id) Promise cache so multiple cards targeting the same
// vanilla template share one RPC and stay consistent. Lifetime is the
// editor session; rebuilding the index requires reopening the editor to
// pick up new values.
export const templateValuesCache = new Map<string, Promise<TemplateValueResult>>();

// U+0000 separator can't appear in either a C# identifier (so the class
// name is safe) or a Unity template id, so this composite key uniquely
// encodes the (typeName, id) tuple without collision risk between sites.
export function vanillaCacheKey(typeName: string, id: string): string {
  return `${typeName}\u0000${id}`;
}

export function templatesValue(typeName: string, id: string): Promise<TemplateValueResult> {
  const key = vanillaCacheKey(typeName, id);
  let cached = templateValuesCache.get(key);
  if (!cached) {
    cached = rpcCall<TemplateValueResult>("templatesValue", { typeName, id });
    templateValuesCache.set(key, cached);
  }
  return cached;
}

// --- Project clones ---

export interface ProjectCloneEntry {
  readonly templateType: string;
  readonly id: string;
  readonly file: string;
}

let projectClonesCache: readonly ProjectCloneEntry[] | null = null;

export function templatesProjectClones(): Promise<{ clones: readonly ProjectCloneEntry[] }> {
  return rpcCall<{ clones: readonly ProjectCloneEntry[] }>("templatesProjectClones");
}

export async function getCachedProjectClones(): Promise<readonly ProjectCloneEntry[]> {
  if (projectClonesCache) return projectClonesCache;
  const result = await templatesProjectClones();
  projectClonesCache = result.clones;
  return result.clones;
}

export function invalidateProjectClonesCache() {
  projectClonesCache = null;
}

// --- Cached enum / template type lookups ---

export async function getCachedEnumEntries(typeName: string): Promise<readonly EnumMemberEntry[]> {
  const cached = enumMembersCache.get(typeName);
  if (cached) return cached;
  const result = await templatesEnumMembers(typeName);
  enumMembersCache.set(typeName, result.members);
  return result.members;
}

export async function getCachedEnumMembers(typeName: string): Promise<readonly string[]> {
  const entries = await getCachedEnumEntries(typeName);
  return entries.map((e) => e.name);
}

export async function getCachedTemplateTypes(): Promise<readonly string[]> {
  if (templateTypesCache.types) return templateTypesCache.types;
  const result = await templatesSearch();
  const types = [...new Set(result.instances.map((i) => i.className))].sort();
  templateTypesCache.types = types;
  return types;
}

// --- Vanilla fields hook ---

// Empty lookup returned when no target is selected or the RPC hasn't
// resolved yet. Module-level constant so consumers' useMemo dependency
// arrays stay stable on the empty case.
export const EMPTY_VANILLA_FIELDS: ReadonlyMap<string, InspectedFieldNode> = new Map();

// Hook: fetches vanilla field values for the (typeName, id) target and
// returns a name → InspectedFieldNode lookup map. Empty until the RPC
// resolves; falls back to empty on failure (callers use neutral defaults).
export function useVanillaFields(
  typeName: string | undefined,
  id: string | undefined,
): ReadonlyMap<string, InspectedFieldNode> {
  const [resolved, setResolved] = useState<{
    key: string;
    map: Map<string, InspectedFieldNode>;
  } | null>(null);

  useEffect(() => {
    if (!typeName || !id) return;
    const key = vanillaCacheKey(typeName, id);
    let cancelled = false;
    void templatesValue(typeName, id)
      .then((result) => {
        if (cancelled) return;
        const map = new Map<string, InspectedFieldNode>();
        for (const f of result.fields) {
          if (f.name) map.set(f.name, f);
        }
        setResolved({ key, map });
      })
      .catch(() => {
        if (!cancelled) setResolved({ key, map: new Map() });
      });
    return () => {
      cancelled = true;
    };
  }, [typeName, id]);

  if (!typeName || !id) return EMPTY_VANILLA_FIELDS;
  const expectedKey = vanillaCacheKey(typeName, id);
  return resolved?.key === expectedKey ? resolved.map : EMPTY_VANILLA_FIELDS;
}

// --- Shared directive factories ---

export function makeDefaultDirective(
  member: TemplateMember,
  vanillaNode?: InspectedFieldNode,
): StampedDirective {
  // NamedArray fields are fixed-size enum-indexed lookups — they only accept
  // `set "Field" index=N <value>`, so default to Set-at-0 and let the picker
  // drive the ordinal. Plain collections default to Append; scalars to Set.
  if (member.namedArrayEnumTypeName) {
    let value = makeDefaultValue(member);
    if (vanillaNode?.kind === "array" && vanillaNode.elements && vanillaNode.elements.length > 0) {
      const vanillaFirst = inspectedFieldToEditorValue(vanillaNode.elements[0], member);
      if (vanillaFirst) value = vanillaFirst;
    }
    return { op: "Set", fieldPath: member.name, index: 0, value, _uiId: uiId() };
  }
  // Plain collections stay on neutral defaults — Append takes one new element,
  // and pre-filling it from a vanilla peer would imply the modder wants a
  // duplicate of an existing entry, which is rarely true.
  const useVanilla = !member.isCollection;
  let value = makeDefaultValue(member);
  if (useVanilla) {
    const vanillaValue = inspectedFieldToEditorValue(vanillaNode, member);
    if (vanillaValue) value = vanillaValue;
  }
  const op: DirectiveOp = member.isCollection ? "Append" : "Set";
  return { op, fieldPath: member.name, value, _uiId: uiId() };
}

// Build a TemplateMember from a cross-member drag payload. Strips undefined
// keys so the resulting object satisfies exactOptionalPropertyTypes — which
// would otherwise reject `{ patchScalarKind: undefined }` even though the
// field is optional on the target type.
export function synthMemberFromPayload(payload: CrossMemberPayload): TemplateMember {
  return {
    name: payload.fieldPath,
    typeName: payload.typeName ?? "",
    isWritable: true,
    isInherited: false,
    ...(payload.patchScalarKind !== undefined ? { patchScalarKind: payload.patchScalarKind } : {}),
    ...(payload.elementTypeName !== undefined ? { elementTypeName: payload.elementTypeName } : {}),
    ...(payload.enumTypeName !== undefined ? { enumTypeName: payload.enumTypeName } : {}),
    ...(payload.referenceTypeName !== undefined
      ? { referenceTypeName: payload.referenceTypeName }
      : {}),
    ...(payload.isCollection !== undefined ? { isCollection: payload.isCollection } : {}),
    ...(payload.isScalar !== undefined ? { isScalar: payload.isScalar } : {}),
    ...(payload.isTemplateReference !== undefined
      ? { isTemplateReference: payload.isTemplateReference }
      : {}),
    ...(payload.namedArrayEnumTypeName !== undefined
      ? { namedArrayEnumTypeName: payload.namedArrayEnumTypeName }
      : {}),
  };
}
