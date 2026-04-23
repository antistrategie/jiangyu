import { afterEach, describe, expect, it, vi } from "vitest";
import { getWindowParams } from "./windowRole.ts";
import { DEFAULT_TEMPLATE_BROWSER_STATE } from "./browserState.ts";

function stubSearch(search: string) {
  vi.stubGlobal("window", {
    location: { search },
  });
}

afterEach(() => {
  vi.unstubAllGlobals();
});

describe("getWindowParams", () => {
  it("defaults to the main role when no params are set", () => {
    stubSearch("");
    expect(getWindowParams()).toEqual({
      role: "main",
      paneKind: null,
      projectPath: null,
      filePaths: [],
      activeFilePath: null,
      browserState: null,
    });
  });

  it("parses a code pane window with tabs and an active file", () => {
    stubSearch(
      "?window=pane&kind=code&projectPath=/proj" +
        "&filePath=/proj/a.tsx&filePath=/proj/b.tsx&activeFilePath=/proj/b.tsx",
    );
    const params = getWindowParams();
    expect(params.role).toBe("pane");
    expect(params.paneKind).toBe("code");
    expect(params.projectPath).toBe("/proj");
    expect(params.filePaths).toEqual(["/proj/a.tsx", "/proj/b.tsx"]);
    expect(params.activeFilePath).toBe("/proj/b.tsx");
  });

  it("parses a browserState JSON blob when present", () => {
    const state = DEFAULT_TEMPLATE_BROWSER_STATE;
    const encoded = encodeURIComponent(JSON.stringify(state));
    stubSearch(`?window=pane&kind=templateBrowser&browserState=${encoded}`);
    expect(getWindowParams().browserState).toEqual(state);
  });

  it("ignores unknown pane kinds so a hand-crafted URL can't render the wrong shell", () => {
    stubSearch("?window=pane&kind=zzz");
    expect(getWindowParams().paneKind).toBeNull();
  });

  it("treats any non-'pane' role value as main", () => {
    stubSearch("?window=wibble");
    expect(getWindowParams().role).toBe("main");
  });

  it("returns null browserState for malformed JSON", () => {
    stubSearch("?window=pane&kind=assetBrowser&browserState=%7Bnot-json");
    expect(getWindowParams().browserState).toBeNull();
  });

  it("returns null browserState when the param is empty", () => {
    stubSearch("?window=pane&kind=assetBrowser&browserState=");
    expect(getWindowParams().browserState).toBeNull();
  });
});
