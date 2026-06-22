import { PALETTE_SCOPE, type PaletteAction } from "@shared/palette/actions";

const PROJECT_SCOPE = PALETTE_SCOPE.Project;

export interface ProjectActionHandlers {
  readonly openProject: () => void;
  readonly closeProject: () => void;
  readonly revealProject: () => void;
  readonly deployMod: () => void;
  readonly packageMod: () => void;
}

/**
 * Build the PROJECT palette actions for the current app state: always-present
 * "Open Project…", and "Close Project" / "Reveal Project" / "Deploy Mod" when a
 * project is open.
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
    actions.push({
      id: "project.deploy",
      label: "Deploy Mod",
      scope: PROJECT_SCOPE,
      cn: "部署",
      desc: "Copy compiled/ into the game's Mods folder.",
      run: handlers.deployMod,
    });
    actions.push({
      id: "project.package",
      label: "Package Mod",
      scope: PROJECT_SCOPE,
      cn: "打包",
      desc: "Package compiled/ into a distributable zip.",
      run: handlers.packageMod,
    });
  }
  return actions;
}
