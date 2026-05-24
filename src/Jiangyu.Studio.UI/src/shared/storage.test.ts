import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";
import { loadJson, loadRaw, removeKey, saveJson } from "./storage";

interface FakeStorage {
  data: Map<string, string>;
  throwOnGet: boolean;
  throwOnSet: boolean;
  throwOnRemove: boolean;
  getItem: (k: string) => string | null;
  setItem: (k: string, v: string) => void;
  removeItem: (k: string) => void;
  clear: () => void;
}

let fake: FakeStorage;

beforeEach(() => {
  fake = {
    data: new Map(),
    throwOnGet: false,
    throwOnSet: false,
    throwOnRemove: false,
    getItem(k) {
      if (this.throwOnGet) throw new Error("storage unavailable");
      return this.data.get(k) ?? null;
    },
    setItem(k, v) {
      if (this.throwOnSet) throw new Error("quota exceeded");
      this.data.set(k, v);
    },
    removeItem(k) {
      if (this.throwOnRemove) throw new Error("storage unavailable");
      this.data.delete(k);
    },
    clear() {
      this.data.clear();
    },
  };
  vi.stubGlobal("localStorage", fake);
});

afterEach(() => {
  vi.unstubAllGlobals();
});

const isString = (v: unknown): v is string => typeof v === "string";
const isStringArray = (v: unknown): v is string[] =>
  Array.isArray(v) && v.every((x) => typeof x === "string");

describe("loadJson", () => {
  it("returns null when the key is missing", () => {
    expect(loadJson("missing", isString)).toBeNull();
  });

  it("returns the parsed value when validate accepts it", () => {
    fake.data.set("key", JSON.stringify(["a", "b"]));
    expect(loadJson("key", isStringArray)).toEqual(["a", "b"]);
  });

  it("returns null when validate rejects the parsed value", () => {
    fake.data.set("key", JSON.stringify(["a", 1]));
    expect(loadJson("key", isStringArray)).toBeNull();
  });

  it("returns null when the stored value is not valid JSON", () => {
    fake.data.set("key", "{ not valid }");
    expect(loadJson("key", isString)).toBeNull();
  });

  it("returns null when storage throws (private mode)", () => {
    fake.throwOnGet = true;
    expect(loadJson("key", isString)).toBeNull();
  });
});

describe("loadRaw", () => {
  it("returns null when the key is missing", () => {
    expect(loadRaw("missing")).toBeNull();
  });

  it("returns the parsed unknown value when present", () => {
    fake.data.set("key", JSON.stringify({ a: 1 }));
    expect(loadRaw("key")).toEqual({ a: 1 });
  });

  it("returns null on malformed JSON", () => {
    fake.data.set("key", "{ not valid }");
    expect(loadRaw("key")).toBeNull();
  });

  it("returns null when storage throws", () => {
    fake.throwOnGet = true;
    expect(loadRaw("key")).toBeNull();
  });
});

describe("saveJson", () => {
  it("stringifies and persists the value", () => {
    saveJson("key", { a: 1 });
    expect(fake.data.get("key")).toBe('{"a":1}');
  });

  it("swallows storage errors (quota / private mode)", () => {
    fake.throwOnSet = true;
    expect(() => saveJson("key", { a: 1 })).not.toThrow();
    expect(fake.data.has("key")).toBe(false);
  });
});

describe("removeKey", () => {
  it("removes an existing entry", () => {
    fake.data.set("key", "value");
    removeKey("key");
    expect(fake.data.has("key")).toBe(false);
  });

  it("is a no-op when the key is missing", () => {
    expect(() => removeKey("missing")).not.toThrow();
  });

  it("swallows storage errors", () => {
    fake.throwOnRemove = true;
    expect(() => removeKey("key")).not.toThrow();
  });
});
