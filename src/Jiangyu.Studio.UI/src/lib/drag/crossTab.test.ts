import { describe, expect, it } from "vitest";
import { encodeCrossTabPayload, parseCrossTabPayload } from "@lib/drag/crossTab";

describe("encode/parseCrossTabPayload", () => {
  it("round-trips a file path", () => {
    const raw = encodeCrossTabPayload("/proj/src/A");
    expect(parseCrossTabPayload(raw)).toBe("/proj/src/A");
  });

  it("returns null for an empty string (plain text drop on the editor)", () => {
    expect(parseCrossTabPayload("")).toBeNull();
  });

  it("returns null for non-JSON input", () => {
    expect(parseCrossTabPayload("plain text")).toBeNull();
  });

  it("returns null for JSON missing the marker", () => {
    expect(parseCrossTabPayload(JSON.stringify({ path: "/a" }))).toBeNull();
  });

  it("returns null for JSON with the wrong marker (version skew)", () => {
    expect(parseCrossTabPayload(JSON.stringify({ m: "other/1", path: "/a" }))).toBeNull();
  });

  it("returns null when path isn't a string", () => {
    expect(parseCrossTabPayload(JSON.stringify({ m: "jiangyu-tab-drag/1", path: 42 }))).toBeNull();
  });

  it("survives a path containing JSON-control characters", () => {
    const weird = '/proj/with "quotes" and \n newline';
    expect(parseCrossTabPayload(encodeCrossTabPayload(weird))).toBe(weird);
  });
});
