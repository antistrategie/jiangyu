import { describe, it, expect, vi } from "vitest";
import { buildTabMenu } from "./tabMenu.ts";
import type { ContextMenuEntry, ContextMenuItem } from "../ContextMenu/ContextMenu";
import type { Tab } from "../../lib/layout.ts";

const files: Tab[] = [
  { path: "/p/a.txt", name: "a.txt" },
  { path: "/p/b.txt", name: "b.txt" },
  { path: "/p/c.txt", name: "c.txt" },
];

function items(entries: ContextMenuEntry[]): ContextMenuItem[] {
  return entries.filter((e): e is ContextMenuItem => e !== "separator");
}

function find(entries: ContextMenuEntry[], label: string): ContextMenuItem {
  const found = items(entries).find((i) => i.label === label);
  if (!found) throw new Error(`Menu item not found: ${label}`);
  return found;
}

describe("buildTabMenu", () => {
  it("produces the expected items in order", () => {
    const menu = buildTabMenu("/p/b.txt", files, "/p", () => {});
    expect(items(menu).map((i) => i.label)).toEqual([
      "Close",
      "Close Others",
      "Close to the Right",
      "Close All",
      "Copy Path",
      "Copy Relative Path",
      "Reveal in File Explorer",
    ]);
  });

  it("includes a separator between close actions and path/reveal actions", () => {
    const menu = buildTabMenu("/p/b.txt", files, "/p", () => {});
    const sepIndex = menu.indexOf("separator");
    expect(sepIndex).toBe(4);
  });

  it("Close closes only the target tab", () => {
    const onCloseFiles = vi.fn();
    const menu = buildTabMenu("/p/b.txt", files, "/p", onCloseFiles);
    find(menu, "Close").onSelect();
    expect(onCloseFiles).toHaveBeenCalledWith(["/p/b.txt"]);
  });

  it("Close Others closes every tab except the target", () => {
    const onCloseFiles = vi.fn();
    const menu = buildTabMenu("/p/b.txt", files, "/p", onCloseFiles);
    find(menu, "Close Others").onSelect();
    expect(onCloseFiles).toHaveBeenCalledWith(["/p/a.txt", "/p/c.txt"]);
  });

  it("Close to the Right closes only tabs after the target", () => {
    const onCloseFiles = vi.fn();
    const menu = buildTabMenu("/p/a.txt", files, "/p", onCloseFiles);
    find(menu, "Close to the Right").onSelect();
    expect(onCloseFiles).toHaveBeenCalledWith(["/p/b.txt", "/p/c.txt"]);
  });

  it("Close All closes every tab", () => {
    const onCloseFiles = vi.fn();
    const menu = buildTabMenu("/p/b.txt", files, "/p", onCloseFiles);
    find(menu, "Close All").onSelect();
    expect(onCloseFiles).toHaveBeenCalledWith(["/p/a.txt", "/p/b.txt", "/p/c.txt"]);
  });

  it("disables Close Others when the target is the only tab", () => {
    const only: Tab[] = [{ path: "/p/a.txt", name: "a.txt" }];
    const menu = buildTabMenu("/p/a.txt", only, "/p", () => {});
    expect(find(menu, "Close Others").disabled).toBe(true);
  });

  it("disables Close to the Right when the target is the last tab", () => {
    const menu = buildTabMenu("/p/c.txt", files, "/p", () => {});
    expect(find(menu, "Close to the Right").disabled).toBe(true);
  });

  it("enables Close to the Right when the target has tabs after it", () => {
    const menu = buildTabMenu("/p/a.txt", files, "/p", () => {});
    expect(find(menu, "Close to the Right").disabled ?? false).toBe(false);
  });

  it("Copy Path writes the absolute path to the clipboard", () => {
    const writeText = vi.fn(() => Promise.resolve());
    vi.stubGlobal("navigator", { clipboard: { writeText } });
    const menu = buildTabMenu("/p/b.txt", files, "/p", () => {});
    find(menu, "Copy Path").onSelect();
    expect(writeText).toHaveBeenCalledWith("/p/b.txt");
    vi.unstubAllGlobals();
  });

  it("Copy Relative Path writes a project-relative path", () => {
    const writeText = vi.fn(() => Promise.resolve());
    vi.stubGlobal("navigator", { clipboard: { writeText } });
    const menu = buildTabMenu("/p/sub/deep.txt", files, "/p", () => {});
    find(menu, "Copy Relative Path").onSelect();
    expect(writeText).toHaveBeenCalledWith("sub/deep.txt");
    vi.unstubAllGlobals();
  });
});
