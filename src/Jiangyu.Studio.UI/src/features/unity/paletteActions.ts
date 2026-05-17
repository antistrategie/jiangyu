import { PALETTE_SCOPE, type PaletteAction } from "@shared/palette/actions";

const UNITY_SCOPE = PALETTE_SCOPE.Unity;

export interface UnityActionHandlers {
  readonly initUnity: () => void;
  readonly openUnity: () => void;
}

/**
 * Build the UNITY palette actions for the current app state. Returns an
 * empty list when no project is open; Unity commands are project-scoped.
 */
export function buildUnityActions(
  projectPath: string | null,
  handlers: UnityActionHandlers,
): PaletteAction[] {
  if (projectPath === null) return [];
  return [
    {
      id: "unity.init",
      label: "Sync Unity Project",
      scope: UNITY_SCOPE,
      cn: "同步",
      desc: "Scaffold or refresh unity/.",
      run: handlers.initUnity,
    },
    {
      id: "unity.open",
      label: "Open Unity Editor",
      scope: UNITY_SCOPE,
      cn: "打开",
      desc: "Launch Unity Editor on unity/.",
      run: handlers.openUnity,
    },
  ];
}
