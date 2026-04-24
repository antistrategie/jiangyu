import { rpcCall } from "@lib/rpc.ts";
import { relative } from "@lib/path.ts";

export interface FileCommand {
  readonly id: "close" | "copyPath" | "copyRelativePath" | "reveal";
  readonly label: string;
  readonly cn?: string;
  readonly shortcut?: string;
  readonly desc?: string;
  readonly run: () => void;
}

/**
 * Shared set of actions that can target a single file path — consumed by both
 * the tab context menu and the command palette's FILE scope so they stay in
 * sync.
 */
export function fileTargetCommands(
  path: string,
  projectPath: string,
  onCloseFiles: (paths: string[]) => void,
): FileCommand[] {
  const rel = relative(projectPath, path);
  return [
    {
      id: "close",
      label: "Close",
      cn: "关闭",
      shortcut: "Ctrl+W",
      run: () => onCloseFiles([path]),
    },
    {
      id: "copyPath",
      label: "Copy Path",
      run: () => void navigator.clipboard.writeText(path),
    },
    {
      id: "copyRelativePath",
      label: "Copy Relative Path",
      desc: rel,
      run: () => void navigator.clipboard.writeText(rel),
    },
    {
      id: "reveal",
      label: "Reveal in File Explorer",
      run: () => {
        void rpcCall<null>("revealInExplorer", { path }).catch((err: unknown) => {
          console.error("[File] reveal failed:", err);
        });
      },
    },
  ];
}
