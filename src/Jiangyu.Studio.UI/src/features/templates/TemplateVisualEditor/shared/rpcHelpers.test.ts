import { describe, it, expect, vi, beforeEach } from "vitest";

vi.mock("@shared/rpc", () => ({
  rpcCall: vi.fn(),
}));

import { rpcCall } from "@shared/rpc";
import {
  vanillaCacheKey,
  getCachedTemplateTypes,
  invalidateProjectClonesCache,
  getCachedProjectClones,
  invalidateProjectAdditionsCache,
  getCachedProjectAdditions,
  templateTypesCache,
} from "./rpcHelpers";

const mockRpcCall = vi.mocked(rpcCall);

beforeEach(() => {
  vi.clearAllMocks();
  templateTypesCache.types = null;
  // Reset module-level caches by invalidating.
  invalidateProjectClonesCache();
  invalidateProjectAdditionsCache();
});

describe("vanillaCacheKey", () => {
  it("produces unique keys for distinct (typeName, id) pairs", () => {
    const a = vanillaCacheKey("UnitTemplate", "archer_01");
    const b = vanillaCacheKey("UnitTemplate", "archer_02");
    const c = vanillaCacheKey("BuildingTemplate", "archer_01");
    expect(a).not.toBe(b);
    expect(a).not.toBe(c);
  });

  it("uses NUL separator so names containing the other part cannot collide", () => {
    // "A\x00B" key for ("A","B") must differ from ("A\x00B","").
    const ab = vanillaCacheKey("A", "B");
    expect(ab).toBe("A\u0000B");
  });
});

describe("getCachedTemplateTypes", () => {
  it("fetches from RPC on first call and caches on second", async () => {
    mockRpcCall.mockResolvedValue({
      types: [],
      instances: [
        { name: "a", className: "UnitTemplate", identity: { collection: "", pathId: 0 } },
        { name: "b", className: "BuildingTemplate", identity: { collection: "", pathId: 1 } },
        { name: "c", className: "UnitTemplate", identity: { collection: "", pathId: 2 } },
      ],
    });

    const first = await getCachedTemplateTypes();
    expect(first).toEqual(["BuildingTemplate", "UnitTemplate"]);
    expect(mockRpcCall).toHaveBeenCalledTimes(1);

    const second = await getCachedTemplateTypes();
    expect(second).toEqual(["BuildingTemplate", "UnitTemplate"]);
    expect(mockRpcCall).toHaveBeenCalledTimes(1);
  });
});

describe("invalidateProjectClonesCache", () => {
  it("clears the cache so next getCachedProjectClones fetches again", async () => {
    mockRpcCall.mockResolvedValue({ clones: [{ templateType: "T", id: "x", file: "f.kdl" }] });

    const first = await getCachedProjectClones();
    expect(first).toHaveLength(1);
    expect(mockRpcCall).toHaveBeenCalledTimes(1);

    invalidateProjectClonesCache();
    mockRpcCall.mockResolvedValue({ clones: [] });

    const second = await getCachedProjectClones();
    expect(second).toHaveLength(0);
    expect(mockRpcCall).toHaveBeenCalledTimes(2);
  });
});

describe("getCachedProjectAdditions", () => {
  it("caches per Unity type and returns the same entries on a second call", async () => {
    mockRpcCall.mockResolvedValue({
      additions: [{ name: "lrm5/icon", file: "assets/additions/sprites/lrm5/icon.png" }],
    });

    const first = await getCachedProjectAdditions("Sprite");
    expect(first.map((a) => a.name)).toEqual(["lrm5/icon"]);
    expect(mockRpcCall).toHaveBeenCalledTimes(1);
    expect(mockRpcCall).toHaveBeenCalledWith("assetsProjectAdditions", { unityType: "Sprite" });

    const second = await getCachedProjectAdditions("Sprite");
    expect(second).toBe(first);
    expect(mockRpcCall).toHaveBeenCalledTimes(1);
  });

  it("fetches separately for distinct Unity types", async () => {
    // Sprites and audio live under different category folders; the
    // picker calling for one type must not see the other type's entries.
    // Caching them under separate keys is what keeps that isolation.
    mockRpcCall.mockImplementation((_, params) => {
      const unityType = (params as { unityType: string }).unityType;
      if (unityType === "Sprite") {
        return Promise.resolve({ additions: [{ name: "icon", file: "f1.png" }] });
      }
      return Promise.resolve({ additions: [{ name: "shot", file: "f2.wav" }] });
    });

    const sprites = await getCachedProjectAdditions("Sprite");
    const audio = await getCachedProjectAdditions("AudioClip");

    expect(sprites.map((a) => a.name)).toEqual(["icon"]);
    expect(audio.map((a) => a.name)).toEqual(["shot"]);
    expect(mockRpcCall).toHaveBeenCalledTimes(2);
  });

  it("invalidate clears every cached type", async () => {
    mockRpcCall.mockResolvedValue({ additions: [{ name: "a", file: "f" }] });

    await getCachedProjectAdditions("Sprite");
    await getCachedProjectAdditions("AudioClip");
    expect(mockRpcCall).toHaveBeenCalledTimes(2);

    invalidateProjectAdditionsCache();

    await getCachedProjectAdditions("Sprite");
    expect(mockRpcCall).toHaveBeenCalledTimes(3);
  });
});
