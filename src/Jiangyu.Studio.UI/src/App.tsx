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
import { rpcCall } from "@lib/rpc.ts";
import { loadSessionRestoreProject, useApplyUiFontScale, useSidebarHidden } from "@lib/settings.ts";
import { basename, join, remapPath } from "@lib/path.ts";
import {
  PALETTE_SCOPE,
  useRegisterActions,
  useRegisteredActions,
  type PaletteAction,
} from "@lib/palette/actions.ts";
import { buildProjectActions } from "@lib/palette/appActions.ts";
import { usePaneActions } from "@lib/palette/paneActions.ts";
import { useFileEntries } from "@lib/project/useFileEntries.ts";
import { useGitBranch } from "@lib/project/gitBranch.ts";
import { useProjectStore } from "@lib/project/store.ts";
import { useEditorContent, useEditorContentSync } from "@lib/editor/content.ts";
import { usePaneWindowStore, useSyncPaneWindowProject } from "@lib/panes/paneWindowStore.ts";
import { useLayoutStore } from "@lib/panes/layoutStore.ts";
import { useCrossWindowTabDrag } from "@lib/drag/useCrossWindowTabDrag.ts";
import { useAppShortcuts } from "@lib/ui/useAppShortcuts.ts";
import { findPane } from "@lib/layout.ts";
import styles from "./App.module.css";

export function App() {
  useApplyUiFontScale();
  useEditorContentSync();

  const [sidebarHidden, setSidebarHidden] = useSidebarHidden();

  // Auto-reopen the most recent project on launch when the user has opted
  // in. Runs once on mount; no-op if a project is already open (e.g. during
  // hot reload) or the recent list is empty. Routes through the host's
  // openProject RPC — same path the WelcomeScreen uses — so the host-side
  // ProjectWatcher.ProjectRoot stays in sync with the UI's projectPath.
  // Going through store.switchProject directly would leave the host
  // unaware of the restored project and break compile + path-scoped RPCs.
  useEffect(() => {
    if (!loadSessionRestoreProject()) return;
    const store = useProjectStore.getState();
    if (store.projectPath !== null) return;
    const last = store.recentProjects[0];
    if (last === undefined) return;
    void rpcCall<string>("openProject", { path: last })
      .then(() => store.switchProject(last))
      .catch((err: unknown) => {
        // Swallow — most likely the directory moved/was deleted since last
        // run. Leaving the user on the welcome screen is the right
        // fallback, no toast needed.
        console.error("[Studio] auto-restore failed:", err);
      });
  }, []);

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
  const gitBranch = useGitBranch(projectPath);

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

  const toggleSidebar = useCallback(
    () => setSidebarHidden(!sidebarHidden),
    [sidebarHidden, setSidebarHidden],
  );

  useAppShortcuts({
    setPaletteOpen,
    toggleSidebar,
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
          <Topbar
            projectName={basename(projectPath)}
            gitBranch={gitBranch}
            onOpenPalette={() => setPaletteOpen(true)}
          />
          <div className={styles.main}>
            {!sidebarHidden && (
              <Sidebar
                projectPath={projectPath}
                onOpenFile={(path) => useLayoutStore.getState().openFile(path)}
                dirtyPaths={dirtyFiles}
                onPathMoved={handlePathMoved}
                revealPath={revealRequest}
              />
            )}
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
