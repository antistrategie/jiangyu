import type { ContextMenuEntry } from "../ContextMenu/ContextMenu.tsx";
import type { OpenFile } from "../../App.tsx";
import { fileTargetCommands, type FileCommand } from "../../lib/fileCommands.ts";

function entryFor(cmd: FileCommand): ContextMenuEntry {
  return cmd.shortcut !== undefined
    ? { label: cmd.label, shortcut: cmd.shortcut, onSelect: cmd.run }
    : { label: cmd.label, onSelect: cmd.run };
}

export function buildTabMenu(
  targetPath: string,
  openFiles: OpenFile[],
  projectPath: string,
  onCloseFiles: (paths: string[]) => void,
): ContextMenuEntry[] {
  const [closeCmd, ...fileOps] = fileTargetCommands(targetPath, projectPath, onCloseFiles);

  const targetIndex = openFiles.findIndex((f) => f.path === targetPath);
  const others = openFiles.filter((f) => f.path !== targetPath).map((f) => f.path);
  const toRight = targetIndex >= 0 ? openFiles.slice(targetIndex + 1).map((f) => f.path) : [];
  const all = openFiles.map((f) => f.path);

  return [
    entryFor(closeCmd!),
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
    ...fileOps.map(entryFor),
  ];
}
