import { describe, expect, it } from "vitest";
import { PALETTE_SCOPE, type PaletteAction } from "@lib/palette/actions.tsx";
import {
  FILES_SCOPE,
  MAX_RESULTS,
  buildSearchIndex,
  filterActions,
  groupByScope,
} from "./paletteFilter.ts";

const noop = () => {};

function action(
  partial: Partial<PaletteAction> & { id: string; label: string; scope: string },
): PaletteAction {
  return { run: noop, ...partial };
}

describe("filterActions", () => {
  const commands: readonly PaletteAction[] = [
    action({ id: "a.save", label: "Save", scope: "FILE · 文件", cn: "保存" }),
    action({ id: "a.close", label: "Close Tab", scope: "FILE · 文件", cn: "关闭" }),
    action({ id: "a.find", label: "Find", scope: "EDIT · 编辑" }),
    action({ id: "a.format", label: "Format Document", scope: "EDIT · 编辑", desc: "typescript" }),
    action({ id: "a.open", label: "Open Project…", scope: "PROJECT · 项目", cn: "打开" }),
  ];

  const files: readonly PaletteAction[] = [
    action({ id: "file:src/App.tsx", label: "src/App.tsx", scope: FILES_SCOPE }),
    action({ id: "file:src/main.tsx", label: "src/main.tsx", scope: FILES_SCOPE }),
    action({ id: "file:src/lib/path.ts", label: "src/lib/path.ts", scope: FILES_SCOPE }),
  ];

  const all = [...commands, ...files];

  it("returns commands only when query is empty", () => {
    const result = filterActions("", all, buildSearchIndex(all));
    expect(result).toHaveLength(commands.length);
    expect(result.every((a) => a.scope !== FILES_SCOPE)).toBe(true);
  });

  it("matches label substrings", () => {
    const result = filterActions("save", all, buildSearchIndex(all));
    expect(result.some((a) => a.id === "a.save")).toBe(true);
  });

  it("matches CJK labels via the cn key", () => {
    const result = filterActions("关闭", all, buildSearchIndex(all));
    expect(result.some((a) => a.id === "a.close")).toBe(true);
  });

  it("matches files by path", () => {
    const result = filterActions("main", all, buildSearchIndex(all));
    expect(result.some((a) => a.id === "file:src/main.tsx")).toBe(true);
  });

  it("tolerates typos via fuzzy matching", () => {
    const result = filterActions("formt", all, buildSearchIndex(all));
    expect(result.some((a) => a.id === "a.format")).toBe(true);
  });

  it("caps results at MAX_RESULTS when the dataset is large", () => {
    const many: PaletteAction[] = [];
    for (let i = 0; i < MAX_RESULTS + 20; i++) {
      many.push(action({ id: `cmd.${i}`, label: `command ${i}`, scope: "TEST" }));
    }
    const result = filterActions("command", many, buildSearchIndex(many));
    expect(result.length).toBeLessThanOrEqual(MAX_RESULTS);
  });

  it("withholds files from the empty-query list even when there are few commands", () => {
    const sparse: readonly PaletteAction[] = [
      action({ id: "only.cmd", label: "Only Command", scope: "X" }),
      action({ id: "file:a", label: "a", scope: FILES_SCOPE }),
      action({ id: "file:b", label: "b", scope: FILES_SCOPE }),
    ];
    const result = filterActions("", sparse, buildSearchIndex(sparse));
    expect(result).toHaveLength(1);
    expect(result[0]!.id).toBe("only.cmd");
  });
});

describe("groupByScope", () => {
  it("groups actions under their scope, preserving first-seen order", () => {
    const actions: readonly PaletteAction[] = [
      action({ id: "1", label: "one", scope: "A" }),
      action({ id: "2", label: "two", scope: "B" }),
      action({ id: "3", label: "three", scope: "A" }),
    ];
    const groups = groupByScope(actions);
    expect(groups.map(([scope]) => scope)).toEqual(["A", "B"]);
    expect(groups[0]![1].map((a) => a.id)).toEqual(["1", "3"]);
    expect(groups[1]![1].map((a) => a.id)).toEqual(["2"]);
  });

  it("returns an empty list for no actions", () => {
    expect(groupByScope([])).toEqual([]);
  });

  it("pins the files scope to the bottom regardless of insertion order", () => {
    const actions: readonly PaletteAction[] = [
      action({ id: "f1", label: "a.ts", scope: FILES_SCOPE }),
      action({ id: "1", label: "cmd", scope: PALETTE_SCOPE.Editor }),
      action({ id: "f2", label: "b.ts", scope: FILES_SCOPE }),
      action({ id: "2", label: "other", scope: PALETTE_SCOPE.File }),
    ];
    const groups = groupByScope(actions);
    expect(groups.map(([scope]) => scope)).toEqual([
      PALETTE_SCOPE.File,
      PALETTE_SCOPE.Editor,
      FILES_SCOPE,
    ]);
  });

  it("orders known scopes Project → View → File → Editor regardless of insertion order", () => {
    const actions: readonly PaletteAction[] = [
      action({ id: "1", label: "ed", scope: PALETTE_SCOPE.Editor }),
      action({ id: "2", label: "f", scope: PALETTE_SCOPE.File }),
      action({ id: "3", label: "v", scope: PALETTE_SCOPE.View }),
      action({ id: "4", label: "p", scope: PALETTE_SCOPE.Project }),
    ];
    const groups = groupByScope(actions);
    expect(groups.map(([scope]) => scope)).toEqual([
      PALETTE_SCOPE.Project,
      PALETTE_SCOPE.View,
      PALETTE_SCOPE.File,
      PALETTE_SCOPE.Editor,
    ]);
  });
});
