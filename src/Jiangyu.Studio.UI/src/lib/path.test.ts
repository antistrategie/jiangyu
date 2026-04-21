import { describe, it, expect } from "vitest";
import { dirname, basename } from "./path.ts";

describe("dirname", () => {
  it("returns parent of a nested path", () => {
    expect(dirname("/foo/bar/baz.txt")).toBe("/foo/bar");
  });

  it("returns the root when path has a single segment under root", () => {
    expect(dirname("/foo")).toBe("/foo");
  });

  it("returns the input when there is no separator", () => {
    expect(dirname("foo")).toBe("foo");
  });

  it("handles empty string", () => {
    expect(dirname("")).toBe("");
  });

  it("handles trailing slash", () => {
    expect(dirname("/foo/bar/")).toBe("/foo/bar");
  });
});

describe("basename", () => {
  it("returns the final segment", () => {
    expect(basename("/foo/bar/baz.txt")).toBe("baz.txt");
  });

  it("returns the input when there is no separator", () => {
    expect(basename("foo.txt")).toBe("foo.txt");
  });

  it("returns empty for a trailing slash", () => {
    expect(basename("/foo/bar/")).toBe("");
  });
});
