import { describe, expect, expectTypeOf, it } from "vitest";
import {
  buildNameUniquenessMap,
  buildReplacementAlias,
  buildReplacementPath,
  classifyAsset,
  countAssetInstances,
  isAssetNameUnique,
  isAudioClip,
  isSprite,
  passesKindFilter,
  type AssetEntry,
  type AudioClipAsset,
  type SpriteAsset,
} from "./assets";

const BASE = {
  name: "foo",
  canonicalPath: null,
  classId: 0,
  pathId: 1,
  collection: "resources.assets",
} as const;

describe("isAudioClip", () => {
  it("is true only for entries with className === 'AudioClip'", () => {
    const audio: AssetEntry = { ...BASE, className: "AudioClip", audioFrequency: 44100 };
    const tex: AssetEntry = { ...BASE, className: "Texture2D" };
    expect(isAudioClip(audio)).toBe(true);
    expect(isAudioClip(tex)).toBe(false);
  });

  it("narrows the type so audio-specific fields are reachable", () => {
    const a: AssetEntry = { ...BASE, className: "AudioClip", audioFrequency: 48000 };
    if (isAudioClip(a)) {
      expectTypeOf(a).toEqualTypeOf<AudioClipAsset>();
      // This line is the whole point — it would be a type error under the old flat shape
      // if accessed without a guard on a non-AudioClip entry.
      expect(a.audioFrequency).toBe(48000);
    }
  });
});

describe("isSprite", () => {
  it("is true only for entries with className === 'Sprite'", () => {
    const sprite: AssetEntry = {
      ...BASE,
      className: "Sprite",
      spriteBackingTextureName: "atlas_1",
    };
    const audio: AssetEntry = { ...BASE, className: "AudioClip" };
    expect(isSprite(sprite)).toBe(true);
    expect(isSprite(audio)).toBe(false);
  });

  it("narrows the type so sprite-specific fields are reachable", () => {
    const s: AssetEntry = { ...BASE, className: "Sprite", spriteBackingTextureName: "hero_atlas" };
    if (isSprite(s)) {
      expectTypeOf(s).toEqualTypeOf<SpriteAsset>();
      expect(s.spriteBackingTextureName).toBe("hero_atlas");
    }
  });

  it("exposes textureRect fields for atlas-backed sprites", () => {
    const s: AssetEntry = {
      ...BASE,
      className: "Sprite",
      spriteBackingTextureName: "hero_atlas",
      spriteTextureRectX: 10,
      spriteTextureRectY: 20,
      spriteTextureRectWidth: 64,
      spriteTextureRectHeight: 32,
      spritePackingRotation: 0,
    };
    if (isSprite(s)) {
      expect(s.spriteTextureRectWidth).toBe(64);
      expect(s.spriteTextureRectHeight).toBe(32);
      expect(s.spriteTextureRectX).toBe(10);
      expect(s.spriteTextureRectY).toBe(20);
      expect(s.spritePackingRotation).toBe(0);
    }
  });
});

describe("classifyAsset", () => {
  it("maps known classes to their kind group", () => {
    expect(classifyAsset("AudioClip")).toBe("audio");
    expect(classifyAsset("Sprite")).toBe("sprite");
    expect(classifyAsset("Texture2D")).toBe("texture");
    expect(classifyAsset("Mesh")).toBe("mesh");
    expect(classifyAsset("PrefabHierarchyObject")).toBe("model");
  });

  it("returns null for unknown or missing classes", () => {
    expect(classifyAsset(null)).toBeNull();
    expect(classifyAsset(undefined)).toBeNull();
    expect(classifyAsset("Transform")).toBeNull();
  });
});

describe("buildReplacementAlias", () => {
  it("returns bare name when unique", () => {
    expect(buildReplacementAlias("soldier", 519, true)).toBe("soldier");
  });

  it("appends pathId when not unique", () => {
    expect(buildReplacementAlias("soldier", 519, false)).toBe("soldier--519");
  });
});

describe("buildReplacementPath", () => {
  it("returns model path for PrefabHierarchyObject", () => {
    const entry: AssetEntry = { ...BASE, className: "PrefabHierarchyObject" };
    expect(buildReplacementPath(entry, true)).toBe("models/foo/model.gltf (.glb)");
  });

  it("returns texture path for Texture2D", () => {
    const entry: AssetEntry = { ...BASE, className: "Texture2D" };
    expect(buildReplacementPath(entry, true)).toBe("textures/foo.png");
  });

  it("returns sprite path for Sprite", () => {
    const entry: AssetEntry = { ...BASE, className: "Sprite" };
    expect(buildReplacementPath(entry, true)).toBe("sprites/foo.png");
  });

  it("returns audio path for AudioClip", () => {
    const entry: AssetEntry = { ...BASE, className: "AudioClip" };
    expect(buildReplacementPath(entry, true)).toBe("audio/foo.wav");
  });

  it("keeps Texture2D path bare even when name is ambiguous", () => {
    const entry: AssetEntry = { ...BASE, className: "Texture2D", pathId: 42 };
    expect(buildReplacementPath(entry, false)).toBe("textures/foo.png");
  });

  it("keeps Sprite path bare even when name is ambiguous", () => {
    const entry: AssetEntry = { ...BASE, className: "Sprite", pathId: 42 };
    expect(buildReplacementPath(entry, false)).toBe("sprites/foo.png");
  });

  it("keeps AudioClip path bare even when name is ambiguous", () => {
    const entry: AssetEntry = { ...BASE, className: "AudioClip", pathId: 42 };
    expect(buildReplacementPath(entry, false)).toBe("audio/foo.wav");
  });

  it("includes pathId for ambiguous PrefabHierarchyObject", () => {
    const entry: AssetEntry = { ...BASE, className: "PrefabHierarchyObject", pathId: 42 };
    expect(buildReplacementPath(entry, false)).toBe("models/foo--42/model.gltf (.glb)");
  });

  it("returns null for Mesh", () => {
    const entry: AssetEntry = { ...BASE, className: "Mesh" };
    expect(buildReplacementPath(entry, true)).toBeNull();
  });

  it("returns null for unnamed assets", () => {
    const entry: AssetEntry = { ...BASE, name: null, className: "Texture2D" };
    expect(buildReplacementPath(entry, true)).toBeNull();
  });
});

describe("buildNameUniquenessMap", () => {
  it("counts distinct entries per (className, name)", () => {
    const assets: AssetEntry[] = [
      { ...BASE, className: "Texture2D", name: "tex_a", pathId: 1 },
      { ...BASE, className: "Texture2D", name: "tex_a", pathId: 2 },
      { ...BASE, className: "Texture2D", name: "tex_b", pathId: 3 },
    ];
    const map = buildNameUniquenessMap(assets);
    expect(map.get("Texture2D\0tex_a")).toBe(2);
    expect(map.get("Texture2D\0tex_b")).toBe(1);
  });

  it("collapses PHO + GO pair to count 1", () => {
    const assets: AssetEntry[] = [
      { ...BASE, className: "PrefabHierarchyObject", name: "soldier", pathId: 10 },
      { ...BASE, className: "GameObject", name: "soldier", pathId: 11 },
    ];
    const map = buildNameUniquenessMap(assets);
    expect(map.get("PrefabHierarchyObject\0soldier")).toBe(1);
  });

  it("flags multiple PHOs with same name as non-unique", () => {
    const assets: AssetEntry[] = [
      { ...BASE, className: "PrefabHierarchyObject", name: "soldier", pathId: 10 },
      { ...BASE, className: "PrefabHierarchyObject", name: "soldier", pathId: 20 },
      { ...BASE, className: "GameObject", name: "soldier", pathId: 11 },
    ];
    const map = buildNameUniquenessMap(assets);
    expect(map.get("PrefabHierarchyObject\0soldier")).toBe(3);
  });
});

describe("countAssetInstances", () => {
  it("returns the count for the entry's (className, name) bucket", () => {
    const map = new Map([["Texture2D\0tex_a", 3]]);
    const entry: AssetEntry = { ...BASE, className: "Texture2D", name: "tex_a" };
    expect(countAssetInstances(entry, map)).toBe(3);
  });

  it("returns 0 for entries with no name or class", () => {
    const map = new Map<string, number>();
    const entry: AssetEntry = { ...BASE, className: null };
    expect(countAssetInstances(entry, map)).toBe(0);
  });

  it("collapses GameObject lookups into the PHO bucket", () => {
    const map = new Map([["PrefabHierarchyObject\0soldier", 2]]);
    const entry: AssetEntry = { ...BASE, className: "GameObject", name: "soldier" };
    expect(countAssetInstances(entry, map)).toBe(2);
  });
});

describe("isAssetNameUnique", () => {
  it("returns true when count is 1", () => {
    const map = new Map([["Texture2D\0tex_a", 1]]);
    const entry: AssetEntry = { ...BASE, className: "Texture2D", name: "tex_a" };
    expect(isAssetNameUnique(entry, map)).toBe(true);
  });

  it("returns false when count > 1", () => {
    const map = new Map([["Texture2D\0tex_a", 2]]);
    const entry: AssetEntry = { ...BASE, className: "Texture2D", name: "tex_a" };
    expect(isAssetNameUnique(entry, map)).toBe(false);
  });

  it("returns false for unnamed entries", () => {
    const map = new Map<string, number>();
    const entry: AssetEntry = { ...BASE, className: "Texture2D", name: null };
    expect(isAssetNameUnique(entry, map)).toBe(false);
  });

  it("collapses GameObject to PHO bucket", () => {
    const map = new Map([["PrefabHierarchyObject\0soldier", 1]]);
    const entry: AssetEntry = { ...BASE, className: "GameObject", name: "soldier" };
    expect(isAssetNameUnique(entry, map)).toBe(true);
  });
});

describe("passesKindFilter", () => {
  it("excludes entries with null className", () => {
    const entry: AssetEntry = { ...BASE, className: null };
    expect(passesKindFilter(entry, "all")).toBe(false);
    expect(passesKindFilter(entry, "texture")).toBe(false);
  });

  it("under 'all' only accepts exportable classes", () => {
    const tex: AssetEntry = { ...BASE, className: "Texture2D" };
    const mesh: AssetEntry = { ...BASE, className: "Mesh" };
    const odd: AssetEntry = { ...BASE, className: "MonoBehaviour" };
    expect(passesKindFilter(tex, "all")).toBe(true);
    expect(passesKindFilter(mesh, "all")).toBe(true);
    expect(passesKindFilter(odd, "all")).toBe(false);
  });

  it("under a specific kind, only accepts classes in that kind's class list", () => {
    const tex: AssetEntry = { ...BASE, className: "Texture2D" };
    const sprite: AssetEntry = { ...BASE, className: "Sprite" };
    expect(passesKindFilter(tex, "texture")).toBe(true);
    expect(passesKindFilter(tex, "sprite")).toBe(false);
    expect(passesKindFilter(sprite, "sprite")).toBe(true);
    expect(passesKindFilter(sprite, "texture")).toBe(false);
  });
});
