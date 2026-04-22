import { describe, expect, expectTypeOf, it } from "vitest";
import {
  classifyAsset,
  isAudioClip,
  isSprite,
  type AssetEntry,
  type AudioClipAsset,
  type SpriteAsset,
} from "./assets.ts";

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
