import { rpcCall } from "./rpc.ts";

export type AssetKind =
  | "GameObject"
  | "PrefabHierarchyObject"
  | "Mesh"
  | "Texture2D"
  | "Sprite"
  | "AudioClip";

interface AssetEntryBase {
  readonly name: string | null;
  readonly canonicalPath: string | null;
  readonly className: string | null;
  readonly classId: number;
  readonly pathId: number;
  readonly collection: string | null;
}

export interface AudioClipAsset extends AssetEntryBase {
  readonly className: "AudioClip";
  readonly audioFrequency?: number | null;
  readonly audioChannels?: number | null;
}

export interface SpriteAsset extends AssetEntryBase {
  readonly className: "Sprite";
  readonly spriteBackingTexturePathId?: number | null;
  readonly spriteBackingTextureCollection?: string | null;
  readonly spriteBackingTextureName?: string | null;
}

/**
 * Catch-all variant for classes Jiangyu indexes without kind-specific fields
 * (Texture2D / Mesh / GameObject / PrefabHierarchyObject / MonoBehaviour / …)
 * and for entries whose `className` is null or unknown. Use `isAudioClip` /
 * `isSprite` type guards to narrow to the specific shapes.
 */
export interface GenericAsset extends AssetEntryBase {}

export type AssetEntry = AudioClipAsset | SpriteAsset | GenericAsset;

export function isAudioClip(a: AssetEntry): a is AudioClipAsset {
  return a.className === "AudioClip";
}

export function isSprite(a: AssetEntry): a is SpriteAsset {
  return a.className === "Sprite";
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

/**
 * Build the replacement alias: bare name when unique, `name--pathId` otherwise.
 */
export function buildReplacementAlias(name: string, pathId: number, unique: boolean): string {
  return unique ? name : `${name}--${pathId}`;
}

/**
 * Build the suggested replacement path for an asset entry.
 * Returns `null` for asset types that don't have a replacement convention
 * (e.g. bare Mesh entries).
 */
export function buildReplacementPath(
  entry: AssetEntry,
  unique: boolean,
): string | null {
  const name = entry.name;
  if (!name) return null;
  const alias = buildReplacementAlias(name, entry.pathId, unique);
  switch (entry.className) {
    case "PrefabHierarchyObject":
      return `models/${alias}/model.gltf (.glb)`;
    case "Texture2D":
      return `textures/${alias}.png`;
    case "Sprite":
      return `sprites/${alias}.png`;
    case "AudioClip":
      return `audio/${alias}.wav`;
    default:
      return null;
  }
}

/**
 * Build a lookup counting how many index entries share each `(className, name)` pair.
 * Used to decide whether an asset name needs `--pathId` disambiguation.
 *
 * For model targets (PrefabHierarchyObject), each PHO has a same-named backing
 * GameObject — those pairs are collapsed so a single PHO+GO pair counts as 1.
 */
export function buildNameUniquenessMap(
  allAssets: readonly AssetEntry[],
): ReadonlyMap<string, number> {
  const counts = new Map<string, number>();
  for (const a of allAssets) {
    if (!a.name || !a.className) continue;
    // Collapse PHO/GO pairs: count GameObjects under PHO's key so both
    // contribute to the same uniqueness bucket.
    const cls = a.className === "GameObject" ? "PrefabHierarchyObject" : a.className;
    const key = `${cls}\0${a.name}`;
    counts.set(key, (counts.get(key) ?? 0) + 1);
  }
  // For PHO entries, each unique name normally produces exactly 2 counts
  // (one PHO + one backing GO). That pair is unique — normalise to 1.
  for (const [key, count] of counts) {
    if (key.startsWith("PrefabHierarchyObject\0") && count === 2) {
      counts.set(key, 1);
    }
  }
  return counts;
}

/**
 * Check whether an asset's name is unique among same-class entries.
 */
export function isAssetNameUnique(
  entry: AssetEntry,
  uniquenessMap: ReadonlyMap<string, number>,
): boolean {
  if (!entry.name || !entry.className) return false;
  const cls =
    entry.className === "GameObject" ? "PrefabHierarchyObject" : entry.className;
  const key = `${cls}\0${entry.name}`;
  return (uniquenessMap.get(key) ?? 0) <= 1;
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

export interface AssetPreviewResult {
  readonly data: string;
  readonly mimeType: string;
}

export function assetsPreview(params: {
  collection: string;
  pathId: number;
  className: string;
}): Promise<AssetPreviewResult | null> {
  return rpcCall<AssetPreviewResult | null>("assetsPreview", params);
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
