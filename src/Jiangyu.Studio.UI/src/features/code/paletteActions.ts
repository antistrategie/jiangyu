import { PALETTE_SCOPE, type PaletteAction } from "@shared/palette/actions";

export interface CodeActionHandlers {
  readonly syncCode: () => void;
}

/**
 * Build the CODE palette actions for the current app state: sync the code/ C#
 * project. Returns an empty list when no project is open; the command is
 * project-scoped. (Deploy lives with Compile under the Project scope.)
 */
export function buildCodeActions(
  projectPath: string | null,
  handlers: CodeActionHandlers,
): PaletteAction[] {
  if (projectPath === null) return [];
  return [
    {
      id: "code.sync",
      label: "Sync Code Project",
      scope: PALETTE_SCOPE.Code,
      cn: "同步",
      desc: "Scaffold or refresh code/.",
      run: handlers.syncCode,
    },
  ];
}
