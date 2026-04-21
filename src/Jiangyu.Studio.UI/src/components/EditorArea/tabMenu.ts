import { rpcCall } from "../../lib/rpc.ts";
import { relative } from "../../lib/path.ts";
import type { ContextMenuEntry } from "../ContextMenu/ContextMenu.tsx";
import type { OpenFile } from "../../App.tsx";

export function buildTabMenu(
  targetPath: string,
  openFiles: OpenFile[],
  projectPath: string,
  onCloseFiles: (paths: string[]) => void,
): ContextMenuEntry[] {
  const targetIndex = openFiles.findIndex((f) => f.path === targetPath);
  const others = openFiles.filter((f) => f.path !== targetPath).map((f) => f.path);
  const toRight = targetIndex >= 0 ? openFiles.slice(targetIndex + 1).map((f) => f.path) : [];
  const all = openFiles.map((f) => f.path);

  return [
    { label: "Close", shortcut: "Ctrl+W", onSelect: () => onCloseFiles([targetPath]) },
    {
      label: "Close Others",
      disabled: others.length === 0,
      onSelect: () => onCloseFiles(others),
    },
    {
      label: "Close to the Right",
      disabled: toRight.length === 0,
      onSelect: () => onCloseFiles(toRight),
    },
    { label: "Close All", onSelect: () => onCloseFiles(all) },
    "separator",
    {
      label: "Copy Path",
      onSelect: () => {
        void navigator.clipboard.writeText(targetPath);
      },
    },
    {
      label: "Copy Relative Path",
      onSelect: () => {
        void navigator.clipboard.writeText(relative(projectPath, targetPath));
      },
    },
    {
      label: "Reveal in File Explorer",
      onSelect: () => {
        void rpcCall<null>("revealInExplorer", { path: targetPath }).catch((err) => {
          console.error("[Editor] reveal failed:", err);
        });
      },
    },
  ];
}
