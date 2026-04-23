import { useCallback, useEffect, useMemo, useRef, useState } from "react";
import { Topbar } from "@components/Topbar/Topbar.tsx";
import { Sidebar } from "@components/Sidebar/Sidebar.tsx";
import { WorkspaceGrid } from "@components/Workspace/WorkspaceGrid.tsx";
import { WelcomeScreen } from "@components/WelcomeScreen/WelcomeScreen.tsx";
import { Palette } from "@components/Palette/Palette.tsx";
import { SettingsModal } from "@components/SettingsModal/SettingsModal.tsx";
import { CompileModal } from "@components/CompileModal/CompileModal.tsx";
import { StatusBar } from "@components/StatusBar/StatusBar.tsx";
import { useCompile } from "@lib/compile/compile.ts";
import { basename, join, remapPath } from "@lib/path.ts";
import {
  PALETTE_SCOPE,
  useRegisterActions,
  useRegisteredActions,
  type PaletteAction,
} from "@lib/palette/actions.tsx";
import { buildProjectActions } from "@lib/palette/appActions.ts";
import { usePaneActions } from "@lib/palette/paneActions.ts";
import { useFileEntries } from "@lib/project/useFileEntries.ts";
import { useProjectStore } from "@lib/project/store.ts";
import { useEditorContent, useEditorContentSync } from "@lib/editor/content.ts";
import { usePaneWindowStore, useSyncPaneWindowProject } from "@lib/panes/paneWindowStore.ts";
import { useLayoutStore } from "@lib/panes/layoutStore.ts";
import { useCrossWindowTabDrag } from "@lib/drag/useCrossWindowTabDrag.ts";
import { useAppShortcuts } from "@lib/ui/useAppShortcuts.ts";
import { findPane } from "@lib/layout.ts";
import styles from "./App.module.css";

export function App() {
  useEditorContentSync();

  const projectPath = useProjectStore((s) => s.projectPath);

  // Layout store selectors — per-slice subscription avoids blanket re-renders.
  const layout = useLayoutStore((s) => s.layout);
  const fullscreenPaneId = useLayoutStore((s) => s.fullscreenPaneId);
  const revealRequest = useLayoutStore((s) => s.revealRequest);
  const lastCodePath = useLayoutStore((s) => s.lastCodePath);

  const dirtyFiles = useEditorContent((s) => s.dirty);

  const [paletteOpen, setPaletteOpen] = useState(false);
  const [settingsOpen, setSettingsOpen] = useState(false);
  const [compileOpen, setCompileOpen] = useState(false);
  const { state: compileState, start: startCompile } = useCompile();

  // Latest-value refs for the shortcut binding + compile kick-off.
  const projectPathRef = useRef(projectPath);
  useEffect(() => {
    projectPathRef.current = projectPath;
  }, [projectPath]);
  const compileStateRef = useRef(compileState);
  useEffect(() => {
    compileStateRef.current = compileState;
  }, [compileState]);
  const startCompileRef = useRef(startCompile);
  useEffect(() => {
    startCompileRef.current = startCompile;
  }, [startCompile]);

  useSyncPaneWindowProject(projectPath);

  const handleTearOutPane = useCallback((paneId: string) => {
    const store = useLayoutStore.getState();
    const pane = findPane(store.layout, paneId);
    if (pane === null) return;
    const paneStore = usePaneWindowStore.getState();
    if (pane.kind === "code") {
      if (pane.tabs.length === 0) return;
      void paneStore.openPaneWindow({
        kind: "code",
        filePaths: pane.tabs.map((t) => t.path),
        activeFilePath: pane.activeTab,
      });
    } else {
      void paneStore.openPaneWindow({
        kind: pane.kind,
        filePaths: [],
        activeFilePath: null,
        browserState: pane.state,
      });
    }
    store.closePane(paneId);
  }, []);

  const handleAppendToFile = useCallback(async (path: string, snippet: string) => {
    useLayoutStore.getState().openFile(path);
    await useEditorContent.getState().appendToFile(path, snippet);
  }, []);

  const handlePathMoved = useCallback((oldPath: string, newPath: string) => {
    useLayoutStore.getState().remapPaths((p) => remapPath(oldPath, newPath, p));
    useEditorContent.getState().remapPath(oldPath, newPath);
  }, []);

  const handleDeletedPath = useCallback((path: string) => {
    useLayoutStore.getState().closeTabsEverywhere([path]);
  }, []);

  const { fileEntries, refreshFiles } = useFileEntries(projectPath, handleDeletedPath);

  const recentProjects = useProjectStore((s) => s.recentProjects);

  const { handleCrossDragStart } = useCrossWindowTabDrag((paneId, paths) =>
    useLayoutStore.getState().closeTabsInPane(paneId, paths),
  );

  const paneActions = usePaneActions({
    projectPath,
    layout,
    onTearOutPane: handleTearOutPane,
  });

  const actions = useMemo<PaletteAction[]>(() => {
    const ps = useProjectStore.getState();
    const result: PaletteAction[] = [
      ...buildProjectActions(projectPath, recentProjects, {
        openProject: () => void ps.openProject(),
        closeProject: ps.closeProject,
        revealProject: ps.revealProject,
        switchProject: ps.switchProject,
      }),
      {
        id: "app.openSettings",
        label: "Settings",
        scope: PALETTE_SCOPE.App,
        cn: "设置",
        run: () => setSettingsOpen(true),
      },
      ...paneActions,
    ];
    if (projectPath !== null) {
      // Palette's Compile entry just opens the dossier; the Run-Compile button
      // inside kicks off the pipeline. Surfacing state before acting lets the
      // user see asset and template counts before committing to a multi-minute
      // Unity build.
      result.push({
        id: "project.compile",
        label: "Compile",
        scope: PALETTE_SCOPE.Project,
        cn: "编译",
        desc: "Build the mod.",
        run: () => setCompileOpen(true),
      });
      const root = projectPath;
      for (const rel of fileEntries) {
        result.push({
          id: `file:${rel}`,
          label: rel,
          scope: PALETTE_SCOPE.GoToFile,
          run: () => useLayoutStore.getState().openFile(join(root, rel)),
        });
      }
    }
    return result;
  }, [projectPath, recentProjects, fileEntries, paneActions]);

  useRegisterActions(actions);

  const allActions = useRegisteredActions();
  const allActionsRef = useRef(allActions);
  useEffect(() => {
    allActionsRef.current = allActions;
  }, [allActions]);

  useAppShortcuts({
    setPaletteOpen,
    projectPathRef,
    compileStateRef,
    startCompileRef,
    allActionsRef,
  });

  return (
    <div className={styles.shell}>
      {projectPath === null ? (
        <WelcomeScreen onOpenProject={useProjectStore.getState().switchProject} />
      ) : (
        <>
          <Topbar projectName={basename(projectPath)} onOpenPalette={() => setPaletteOpen(true)} />
          <div className={styles.main}>
            <Sidebar
              projectPath={projectPath}
              onOpenFile={(path) => useLayoutStore.getState().openFile(path)}
              dirtyPaths={dirtyFiles}
              onPathMoved={handlePathMoved}
              revealPath={revealRequest}
            />
            <WorkspaceGrid
              projectPath={projectPath}
              layout={layout}
              fullscreenPaneId={fullscreenPaneId}
              fileEntries={fileEntries}
              lastCodePath={lastCodePath}
              onTearOutPane={handleTearOutPane}
              onCrossDragStart={handleCrossDragStart}
              onAppendToFile={handleAppendToFile}
              onRefreshFiles={refreshFiles}
            />
          </div>
          <StatusBar compileState={compileState} onOpenCompileModal={() => setCompileOpen(true)} />
        </>
      )}
      <Palette open={paletteOpen} onClose={() => setPaletteOpen(false)} actions={allActions} />
      {settingsOpen && <SettingsModal onClose={() => setSettingsOpen(false)} />}
      {compileOpen && (
        <CompileModal
          state={compileState}
          onClose={() => setCompileOpen(false)}
          onStart={startCompile}
        />
      )}
    </div>
  );
}
