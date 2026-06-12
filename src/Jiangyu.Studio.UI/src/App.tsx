import { useCallback, useEffect, useMemo, useRef, useState } from "react";
import splashUrl from "./assets/splash.jpg";
import { Topbar } from "@features/panes/Topbar/Topbar";
import { Sidebar } from "@features/panes/Sidebar/Sidebar";
import { WorkspaceGrid } from "@features/panes/Workspace/WorkspaceGrid";
import { WelcomeScreen } from "@features/project/WelcomeScreen/WelcomeScreen";
import { Palette } from "@shared/ui/Palette/Palette";
import { SettingsModal } from "@features/settings/SettingsModal/SettingsModal";
import { CompileModal } from "@features/compile/CompileModal/CompileModal";
import { AgentRegistryModal } from "@features/agent/AgentRegistryModal/AgentRegistryModal";
import { StatusBar } from "@features/panes/StatusBar/StatusBar";
import { rpcCall } from "@shared/rpc";
import {
  loadSessionRestoreProject,
  useAiEnabled,
  useApplyUiFontScale,
  useSidebarHidden,
} from "@features/settings/settings";
import { useRegistryModalStore } from "@features/agent/registryModal";
import { basename, join, remapPath } from "@shared/path";
import {
  PALETTE_SCOPE,
  useRegisterActions,
  useRegisteredActions,
  type PaletteAction,
} from "@shared/palette/actions";
import { buildProjectActions } from "@features/project/paletteActions";
import { buildUnityActions } from "@features/unity/paletteActions";
import { unityInit, unityOpen } from "@features/unity/unity";
import { buildCodeActions } from "@features/code/paletteActions";
import { codeSync, deploy } from "@features/code/code";
import { revealInExplorer } from "@features/assets/assets";
import { useToastStore, type Toast } from "@shared/toast";
import { usePaneActions } from "@features/panes/paneActions";
import { useFileEntries } from "@features/project/useFileEntries";
import { useGitBranch } from "@features/project/gitBranch";
import { useProjectStore } from "@features/project/store";
import { useEditorContent, useEditorContentSync } from "@features/editor/content";
import { usePaneWindowStore, useSyncPaneWindowProject } from "@features/panes/paneWindowStore";
import { useLayoutStore } from "@features/panes/layoutStore";
import { useCrossWindowTabDrag } from "@features/panes/useCrossWindowTabDrag";
import { useAppShortcuts } from "@shared/utils/useAppShortcuts";
import { findPane } from "@features/panes/layout";
import styles from "./App.module.css";

// Run a host RPC and surface its outcome as a toast: a success toast built from
// the result, or an error toast prefixed with errorLabel. Shared by the palette
// commands that kick off an async host action.
function runRpcWithToast<T>(
  run: () => Promise<T>,
  errorLabel: string,
  onSuccess: (result: T) => Omit<Toast, "id" | "sticker" | "variant">,
): void {
  const push = useToastStore.getState().push;
  void (async () => {
    try {
      push({ variant: "success", ...onSuccess(await run()) });
    } catch (err) {
      push({ variant: "error", message: `${errorLabel}: ${(err as Error).message}` });
    }
  })();
}

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
  // True while auto-restoring the last project — stay on the loading splash
  // instead of flashing the welcome screen.
  const [restoringProject, setRestoringProject] = useState(() => {
    if (!loadSessionRestoreProject()) return false;
    const store = useProjectStore.getState();
    if (store.projectPath !== null) return false;
    return store.recentProjects[0] !== undefined;
  });

  useEffect(() => {
    if (!restoringProject) return;
    const store = useProjectStore.getState();
    const last = store.recentProjects[0];
    // restoringProject's lazy init already verified recent[0] exists, so
    // this branch shouldn't fire — but guard defensively.
    if (last === undefined) return;
    void rpcCall<string>("openProject", { path: last })
      .then(() => {
        store.switchProject(last);
        setRestoringProject(false);
      })
      .catch((err: unknown) => {
        setRestoringProject(false);
        console.error("[Studio] auto-restore failed:", err);
      });
    // One-shot mount-time auto-restore. Including restoringProject in deps
    // would re-run when the effect itself flips it to false.
    // eslint-disable-next-line react-hooks/exhaustive-deps, @eslint-react/exhaustive-deps
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
  const registryOpen = useRegistryModalStore((s) => s.open);
  const setRegistryOpen = useRegistryModalStore((s) => s.setOpen);
  const [aiEnabled] = useAiEnabled();

  // Latest-value ref for the shortcut binding.
  const projectPathRef = useRef(projectPath);
  useEffect(() => {
    projectPathRef.current = projectPath;
  }, [projectPath]);

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
      ...buildProjectActions(projectPath, {
        openProject: () => void ps.openProject(),
        closeProject: ps.closeProject,
        revealProject: ps.revealProject,
        deployMod: () =>
          runRpcWithToast(deploy, "Deploy failed", (r) => ({
            message: `Deployed ${r.modName}`,
            detail: r.destDir,
            actions: [{ label: "Reveal", run: () => void revealInExplorer(r.destDir) }],
          })),
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
    if (aiEnabled) {
      result.push({
        id: "ai.browseRegistry",
        label: "Browse Agent Registry",
        scope: PALETTE_SCOPE.AI,
        cn: "代理库",
        desc: "Install ACP agents from the public registry.",
        run: () => setRegistryOpen(true),
      });
    }
    if (projectPath !== null) {
      const root = projectPath;
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
      result.push(
        ...buildUnityActions(root, {
          initUnity: () =>
            runRpcWithToast(unityInit, "Unity init failed", (r) => ({
              message: "Unity project synced",
              detail: `Created ${r.createdCount.toString()} · Updated ${r.updatedCount.toString()}`,
              actions: [{ label: "Reveal", run: () => void revealInExplorer(join(root, "unity")) }],
            })),
          openUnity: () =>
            runRpcWithToast(unityOpen, "Open Unity failed", (r) => ({
              message: "Launched Unity Editor",
              detail: `${r.editorPath} (pid ${r.pid.toString()})`,
            })),
        }),
      );
      result.push(
        ...buildCodeActions(root, {
          syncCode: () =>
            runRpcWithToast(codeSync, "Code sync failed", (r) => ({
              message: "Code project synced",
              detail: `Created ${r.createdCount.toString()} · Updated ${r.updatedCount.toString()}${r.sdkResolved ? "" : " · SDK path unresolved"}`,
              actions: [{ label: "Reveal", run: () => void revealInExplorer(join(root, "code")) }],
            })),
        }),
      );
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
  }, [projectPath, fileEntries, paneActions, aiEnabled, setRegistryOpen]);

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
    allActionsRef,
  });

  return (
    <div className={styles.shell}>
      {projectPath === null ? (
        restoringProject ? (
          <RestoringSplash />
        ) : (
          <WelcomeScreen onOpenProject={useProjectStore.getState().switchProject} />
        )
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
          <StatusBar onOpenCompileModal={() => setCompileOpen(true)} />
        </>
      )}
      <Palette open={paletteOpen} onClose={() => setPaletteOpen(false)} actions={allActions} />
      {settingsOpen && <SettingsModal onClose={() => setSettingsOpen(false)} />}
      {compileOpen && <CompileModal onClose={() => setCompileOpen(false)} />}
      {registryOpen && <AgentRegistryModal onClose={() => setRegistryOpen(false)} />}
    </div>
  );
}

function RestoringSplash() {
  return (
    <main className={styles.splash}>
      <img src={splashUrl} alt="" className={styles.splashImage} />
      <div className={styles.splashLoader}>Restoring project…</div>
    </main>
  );
}
