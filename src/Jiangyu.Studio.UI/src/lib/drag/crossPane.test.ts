import { describe, expect, it } from "vitest";
import { encodeCrossPanePayload, parseCrossPanePayload } from "@lib/drag/crossPane.ts";
import { DEFAULT_ASSET_BROWSER_STATE } from "@lib/panes/browserState.ts";

describe("encode/parseCrossPanePayload", () => {
  it("round-trips a code pane payload with tabs + active tab", () => {
    const raw = encodeCrossPanePayload({
      kind: "code",
      filePaths: ["/a.tsx", "/b.tsx"],
      activeFilePath: "/b.tsx",
    });
    expect(parseCrossPanePayload(raw)).toEqual({
      m: "jiangyu-pane-drag/1",
      kind: "code",
      filePaths: ["/a.tsx", "/b.tsx"],
      activeFilePath: "/b.tsx",
    });
  });

  it("round-trips a browser pane payload carrying opaque state", () => {
    const raw = encodeCrossPanePayload({
      kind: "assetBrowser",
      browserState: DEFAULT_ASSET_BROWSER_STATE,
    });
    const parsed = parseCrossPanePayload(raw);
    expect(parsed?.kind).toBe("assetBrowser");
    expect(parsed?.browserState).toEqual(DEFAULT_ASSET_BROWSER_STATE);
  });

  it("returns null for empty input", () => {
    expect(parseCrossPanePayload("")).toBeNull();
  });

  it("returns null for non-JSON input", () => {
    expect(parseCrossPanePayload("not json")).toBeNull();
  });

  it("returns null when the marker is missing", () => {
    expect(parseCrossPanePayload(JSON.stringify({ kind: "code" }))).toBeNull();
  });

  it("rejects unknown pane kinds", () => {
    const raw = JSON.stringify({ m: "jiangyu-pane-drag/1", kind: "scripting" });
    expect(parseCrossPanePayload(raw)).toBeNull();
  });

  it("accepts all three supported kinds", () => {
    const kinds = ["code", "assetBrowser", "templateBrowser"] as const;
    for (const kind of kinds) {
      const raw = encodeCrossPanePayload({ kind });
      expect(parseCrossPanePayload(raw)?.kind).toBe(kind);
    }
  });
});
