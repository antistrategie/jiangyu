import { PALETTE_SCOPE, type PaletteAction } from "@lib/palette/actions";

const PROJECT_SCOPE = PALETTE_SCOPE.Project;

export interface ProjectActionHandlers {
  readonly openProject: () => void;
  readonly closeProject: () => void;
  readonly revealProject: () => void;
}

/**
 * Build the PROJECT palette actions for the current app state: always-present
 * "Open Project…", and "Close Project" / "Reveal Project" when a project is open.
 */
export function buildProjectActions(
  projectPath: string | null,
  handlers: ProjectActionHandlers,
): PaletteAction[] {
  const actions: PaletteAction[] = [
    {
      id: "app.openProject",
      label: "Open Project",
      scope: PROJECT_SCOPE,
      cn: "打开",
      run: handlers.openProject,
    },
  ];
  if (projectPath !== null) {
    actions.push({
      id: "app.closeProject",
      label: "Close Project",
      scope: PROJECT_SCOPE,
      cn: "关闭",
      run: handlers.closeProject,
    });
    actions.push({
      id: "app.revealProject",
      label: "Reveal Project in File Explorer",
      scope: PROJECT_SCOPE,
      cn: "显示",
      run: handlers.revealProject,
    });
  }
  return actions;
}
