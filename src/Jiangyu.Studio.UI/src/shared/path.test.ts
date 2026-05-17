import { describe, it, expect } from "vitest";
import {
  dirname,
  basename,
  join,
  relative,
  isDescendant,
  remapPath,
  isAbsolute,
  normalise,
} from "./path";

describe("isAbsolute", () => {
  it("returns true for POSIX absolute paths", () => {
    expect(isAbsolute("/home/user/project")).toBe(true);
  });

  it("returns true for Windows drive paths with backslash", () => {
    expect(isAbsolute("C:\\Users\\me")).toBe(true);
  });

  it("returns true for Windows drive paths with forward slash", () => {
    expect(isAbsolute("D:/Games/MENACE")).toBe(true);
  });

  it("returns false for relative paths", () => {
    expect(isAbsolute("src/lib/path")).toBe(false);
    expect(isAbsolute("./relative")).toBe(false);
    expect(isAbsolute("")).toBe(false);
  });
});

describe("normalise", () => {
  it("converts backslashes to forward slashes", () => {
    expect(normalise("C:\\Users\\me\\project")).toBe("C:/Users/me/project");
  });

  it("leaves POSIX paths unchanged", () => {
    expect(normalise("/home/user/project")).toBe("/home/user/project");
  });

  it("handles mixed separators", () => {
    expect(normalise("C:\\foo/bar\\baz")).toBe("C:/foo/bar/baz");
  });

  it("handles empty string", () => {
    expect(normalise("")).toBe("");
  });
});

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

describe("join", () => {
  it("joins two segments", () => {
    expect(join("/foo", "bar")).toBe("/foo/bar");
  });

  it("strips trailing slashes from first segment", () => {
    expect(join("/foo/", "bar")).toBe("/foo/bar");
  });

  it("strips leading slashes from subsequent segments", () => {
    expect(join("/foo", "/bar", "/baz")).toBe("/foo/bar/baz");
  });

  it("ignores empty segments", () => {
    expect(join("/foo", "", "bar")).toBe("/foo/bar");
  });
});

describe("relative", () => {
  it("returns empty when equal", () => {
    expect(relative("/a/b", "/a/b")).toBe("");
  });

  it("returns child portion when under from", () => {
    expect(relative("/a/b", "/a/b/c/d")).toBe("c/d");
  });

  it("returns to unchanged when not under from", () => {
    expect(relative("/a/b", "/other")).toBe("/other");
  });
});

describe("isDescendant", () => {
  it("is true for equal paths", () => {
    expect(isDescendant("/a", "/a")).toBe(true);
  });

  it("is true for strict descendants", () => {
    expect(isDescendant("/a", "/a/b")).toBe(true);
  });

  it("is false for siblings with shared prefix", () => {
    expect(isDescendant("/a", "/ab")).toBe(false);
  });

  it("is false for unrelated paths", () => {
    expect(isDescendant("/a", "/b")).toBe(false);
  });
});

describe("remapPath", () => {
  it("returns newBase when path equals oldBase", () => {
    expect(remapPath("/a/old", "/a/new", "/a/old")).toBe("/a/new");
  });

  it("rewrites descendants onto newBase", () => {
    expect(remapPath("/a/old", "/a/new", "/a/old/child.txt")).toBe("/a/new/child.txt");
  });

  it("rewrites deep descendants", () => {
    expect(remapPath("/a/old", "/b/moved", "/a/old/deep/nested/file.md")).toBe(
      "/b/moved/deep/nested/file.md",
    );
  });

  it("leaves unrelated paths untouched", () => {
    expect(remapPath("/a/old", "/a/new", "/other/file.txt")).toBe("/other/file.txt");
  });

  it("does not false-match paths with a shared prefix", () => {
    expect(remapPath("/a/old", "/a/new", "/a/oldish/file.txt")).toBe("/a/oldish/file.txt");
  });
});
