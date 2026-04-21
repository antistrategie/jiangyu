import { rpcCall } from "./rpc.ts";

export type AssetKind =
  | "GameObject"
  | "PrefabHierarchyObject"
  | "Mesh"
  | "Texture2D"
  | "Sprite"
  | "AudioClip";

export interface AssetEntry {
  readonly name: string | null;
  readonly canonicalPath: string | null;
  readonly className: string | null;
  readonly classId: number;
  readonly pathId: number;
  readonly collection: string | null;
  readonly spriteBackingTexturePathId?: number | null;
  readonly spriteBackingTextureCollection?: string | null;
  readonly spriteBackingTextureName?: string | null;
  readonly audioFrequency?: number | null;
  readonly audioChannels?: number | null;
}

export type AssetIndexState = "current" | "stale" | "missing" | "noGame";

export interface AssetIndexStatus {
  readonly state: AssetIndexState;
  readonly reason?: string | null;
  readonly assetCount?: number | null;
  readonly indexedAt?: string | null;
}

export interface AssetExportResult {
  readonly outputPath: string;
}

export interface AssetSearchParams {
  readonly query?: string;
  readonly kind?: string;
  readonly collection?: string;
  readonly limit?: number;
}

export interface AssetExportParams {
  readonly assetName: string;
  readonly collection: string;
  readonly pathId: number;
  readonly kind: AssetKind | string;
  readonly baseDir: string;
}

/** High-level "filter pill" classification, mapping user-facing buckets to index class names. */
export type AssetKindGroup = "model" | "mesh" | "texture" | "sprite" | "audio";

export const ASSET_KIND_GROUP_LABEL: Record<AssetKindGroup, string> = {
  model: "Model",
  mesh: "Mesh",
  texture: "Texture",
  sprite: "Sprite",
  audio: "Audio",
};

// PrefabHierarchyObject is the modder-facing model target (see JIANGYU-CONTRACT
// in AssetPipelineService.ResolveGameObjectBacking). Bare GameObjects are
// intentionally hidden because each PHO has a same-named GameObject backing it,
// so surfacing both would show every model twice.
export const ASSET_KIND_GROUP_CLASSES: Record<AssetKindGroup, readonly string[]> = {
  model: ["PrefabHierarchyObject"],
  mesh: ["Mesh"],
  texture: ["Texture2D"],
  sprite: ["Sprite"],
  audio: ["AudioClip"],
};

// The union of every exportable class name — used to hide Unity internals
// (Transform, MonoBehaviour, AnimatorStateTransition, …) from the default
// "All" view. The asset index catalogues every Unity object, but only the
// five kinds above have a working export path.
export const EXPORTABLE_CLASS_NAMES: ReadonlySet<string> = new Set(
  Object.values(ASSET_KIND_GROUP_CLASSES).flat(),
);

export function classifyAsset(className: string | null | undefined): AssetKindGroup | null {
  if (!className) return null;
  for (const group of Object.keys(ASSET_KIND_GROUP_CLASSES) as AssetKindGroup[]) {
    if (ASSET_KIND_GROUP_CLASSES[group].includes(className)) return group;
  }
  return null;
}

export function assetsIndexStatus(): Promise<AssetIndexStatus> {
  return rpcCall<AssetIndexStatus>("assetsIndexStatus");
}

export function assetsIndex(): Promise<AssetIndexStatus> {
  return rpcCall<AssetIndexStatus>("assetsIndex");
}

export function assetsSearch(params: AssetSearchParams): Promise<AssetEntry[]> {
  return rpcCall<AssetEntry[]>("assetsSearch", params);
}

export function assetsExport(params: AssetExportParams): Promise<AssetExportResult> {
  return rpcCall<AssetExportResult>("assetsExport", params);
}

export function pickDirectory(params?: {
  title?: string;
  initial?: string;
}): Promise<string | null> {
  return rpcCall<string | null>("pickDirectory", params ?? {});
}

export function revealInExplorer(path: string): Promise<void> {
  return rpcCall<void>("revealInExplorer", { path });
}
