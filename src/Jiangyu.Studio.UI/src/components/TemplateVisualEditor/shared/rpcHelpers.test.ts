import { describe, it, expect, vi, beforeEach } from "vitest";

vi.mock("@lib/rpc", () => ({
  rpcCall: vi.fn(),
}));

import { rpcCall } from "@lib/rpc";
import {
  vanillaCacheKey,
  getCachedTemplateTypes,
  invalidateProjectClonesCache,
  getCachedProjectClones,
  templateTypesCache,
} from "./rpcHelpers";

const mockRpcCall = vi.mocked(rpcCall);

beforeEach(() => {
  vi.clearAllMocks();
  templateTypesCache.types = null;
  // Reset the module-level projectClonesCache by invalidating.
  invalidateProjectClonesCache();
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
