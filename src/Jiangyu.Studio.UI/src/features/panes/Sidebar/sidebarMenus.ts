import { rpcCall } from "@shared/rpc";
import { dirname, relative } from "@shared/path";
import type { ContextMenuEntry } from "@shared/ui/ContextMenu/ContextMenu";
import {
  duplicate,
  pasteClipboard,
  performDelete,
  reportRpcError,
  type FileEntry,
} from "./fileOps";
import type { SidebarContextValue } from "./sidebarContext";

// Context-menu builders for the sidebar: the tree root and individual
// entries. Pure functions over the sidebar context so the components only
// own menu positioning.

export interface EntryMenuContext extends SidebarContextValue {
  readonly startRename: () => void;
}

export function buildRootMenu(ctx: SidebarContextValue): ContextMenuEntry[] {
  const canPaste = ctx.clipboard !== null && ctx.clipboard.paths.length > 0;
  return [
    {
      label: "New File",
      onSelect: () => ctx.setPendingNew({ parentPath: ctx.projectPath, kind: "file" }),
    },
    {
      label: "New Folder",
      onSelect: () => ctx.setPendingNew({ parentPath: ctx.projectPath, kind: "folder" }),
    },
    "separator",
    {
      label: "Paste",
      shortcut: "Ctrl+V",
      disabled: !canPaste,
      onSelect: () => void pasteClipboard(ctx, ctx.projectPath),
    },
  ];
}

export function buildEntryMenu(entry: FileEntry, mctx: EntryMenuContext): ContextMenuEntry[] {
  const items: ContextMenuEntry[] = [];
  const parentDir = entry.isDirectory ? entry.path : dirname(entry.path);
  const canPaste = mctx.clipboard !== null && mctx.clipboard.paths.length > 0;

  if (entry.isDirectory) {
    items.push(
      {
        label: "New File",
        onSelect: () => {
          mctx.expandDirectory(entry.path);
          mctx.setPendingNew({ parentPath: entry.path, kind: "file" });
        },
      },
      {
        label: "New Folder",
        onSelect: () => {
          mctx.expandDirectory(entry.path);
          mctx.setPendingNew({ parentPath: entry.path, kind: "folder" });
        },
      },
      "separator",
    );
  }

  items.push(
    {
      label: "Reveal in File Explorer",
      onSelect: () => {
        void rpcCall<null>("revealInExplorer", { path: entry.path }).catch((err: unknown) => {
          reportRpcError(mctx.pushToast, "Reveal", err);
        });
      },
    },
    {
      label: "Copy Path",
      onSelect: () => {
        void navigator.clipboard.writeText(entry.path);
      },
    },
    {
      label: "Copy Relative Path",
      onSelect: () => {
        void navigator.clipboard.writeText(relative(mctx.projectPath, entry.path));
      },
    },
    "separator",
    {
      label: "Cut",
      shortcut: "Ctrl+X",
      onSelect: () => mctx.setClipboard({ paths: [entry.path], mode: "cut" }),
    },
    {
      label: "Copy",
      shortcut: "Ctrl+C",
      onSelect: () => mctx.setClipboard({ paths: [entry.path], mode: "copy" }),
    },
    {
      label: "Paste",
      shortcut: "Ctrl+V",
      disabled: !canPaste,
      onSelect: () => void pasteClipboard(mctx, parentDir),
    },
    {
      label: "Duplicate",
      onSelect: () => void duplicate(entry, mctx),
    },
    "separator",
    {
      label: "Rename",
      shortcut: "F2",
      onSelect: mctx.startRename,
    },
    {
      label: "Delete",
      shortcut: "Del",
      onSelect: () => void performDelete(entry, mctx),
    },
  );

  return items;
}
