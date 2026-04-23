import { useCallback, useEffect, useMemo, useRef, useState } from "react";
import { Topbar } from "./components/Topbar/Topbar.tsx";
import { Sidebar } from "./components/Sidebar/Sidebar.tsx";
import { EditorGrid, type EditorGridHandle } from "./components/EditorArea/EditorGrid.tsx";
import { WelcomeScreen } from "./components/WelcomeScreen/WelcomeScreen.tsx";
import { Palette } from "./components/Palette/Palette.tsx";
import { SettingsModal } from "./components/SettingsModal/SettingsModal.tsx";
import { CompileModal } from "./components/CompileModal/CompileModal.tsx";
import { StatusBar } from "./components/StatusBar/StatusBar.tsx";
import { useCompile } from "./lib/compile.ts";
import { basename, join, remapPath } from "./lib/path.ts";
import { rpcCall, subscribe, type FileChangedEvent } from "./lib/rpc.ts";
import { pickProjectFolder } from "./lib/projectCommands.ts";
import {
  PALETTE_SCOPE,
  useRegisterActions,
  useRegisteredActions,
  type PaletteAction,
} from "./lib/actions.tsx";
import { buildProjectActions } from "./lib/appActions.ts";
import { loadRecentProjects, recordRecentProject } from "./lib/recentProjects.ts";
import { matchBinding, type KeyBinding } from "./lib/shortcuts.ts";
import {
  BROWSER_KIND_META,
  EMPTY_LAYOUT,
  closePane as closePaneInLayout,
  closeTabs,
  closeTabsEverywhere,
  convertPane as convertPaneInLayout,
  findPane,
  getActiveCodePane,
  getActivePane,
  getAllOpenPaths,
  getAllPanes,
  loadLayout,
  moveTab as moveTabInLayout,
  movePaneToEdge as movePaneToEdgeInLayout,
  openFile as openFileInLayout,
  remapPaths as remapLayoutPaths,
  saveLayout,
  selectTab as selectTabInLayout,
  setActivePane as setActivePaneInLayout,
  setColumnWeight,
  setPaneWeight,
  splitDown,
  splitRight,
  splitWithTab as splitWithTabInLayout,
  swapPanes as swapPanesInLayout,
  type BrowserPane,
  type Layout,
  type PaneKind,
  type SplitEdge,
} from "./lib/layout.ts";
import styles from "./App.module.css";

export function App() {
  const [projectPath, setProjectPath] = useState<string | null>(null);
  const [layout, setLayout] = useState<Layout>(EMPTY_LAYOUT);
  const [dirtyFiles, setDirtyFiles] = useState<Set<string>>(new Set());
  const [paletteOpen, setPaletteOpen] = useState(false);
  const [settingsOpen, setSettingsOpen] = useState(false);
  const [compileOpen, setCompileOpen] = useState(false);
  const { state: compileState, start: startCompile } = useCompile();
  const [fileEntries, setFileEntries] = useState<readonly string[]>([]);
  const [recentProjects, setRecentProjects] = useState<readonly string[]>([]);
  const [fullscreenPaneId, setFullscreenPaneId] = useState<string | null>(null);
  const revealTickRef = useRef(0);
  const [revealRequest, setRevealRequest] = useState<{ path: string; tick: number } | null>(null);
  const editorGridRef = useRef<EditorGridHandle>(null);
  const lastCodePathRef = useRef<string | null>(null);
  const refreshFilesRef = useRef<() => void>(() => {});

  const revealFile = useCallback((path: string) => {
    revealTickRef.current += 1;
    setRevealRequest({ path, tick: revealTickRef.current });
  }, []);

  const handleOpenFile = useCallback(
    (path: string) => {
      setLayout((prev) => openFileInLayout(prev, path));
      lastCodePathRef.current = path;
      revealFile(path);
    },
    [revealFile],
  );

  const handleSelectTab = useCallback(
    (paneId: string, path: string) => {
      setLayout((prev) => selectTabInLayout(prev, paneId, path));
      lastCodePathRef.current = path;
      revealFile(path);
    },
    [revealFile],
  );

  const handleSetActivePane = useCallback(
    (paneId: string) => {
      setLayout((prev) => setActivePaneInLayout(prev, paneId));
      const pane = findPane(layoutRef.current, paneId);
      if (pane && pane.kind === "code" && pane.activeTab) {
        lastCodePathRef.current = pane.activeTab;
        revealFile(pane.activeTab);
      }
    },
    [revealFile],
  );

  const handleCloseTabsInPane = useCallback((paneId: string, paths: readonly string[]) => {
    setLayout((prev) => closeTabs(prev, paneId, paths));
  }, []);

  const handleMoveTab = useCallback((fromPaneId: string, toPaneId: string, path: string) => {
    setLayout((prev) => moveTabInLayout(prev, fromPaneId, toPaneId, path));
  }, []);

  const handleAppendToFile = useCallback(async (path: string, snippet: string) => {
    // Open the tab; appendToFile coordinates with the loader itself, so no
    // wait-for-render hack is needed here.
    setLayout((prev) => openFileInLayout(prev, path));
    await editorGridRef.current?.appendToFile(path, snippet);
  }, []);

  const handleRefreshFiles = useCallback(() => {
    refreshFilesRef.current();
  }, []);

  const handleSplitRight = useCallback((kind: PaneKind = "code") => {
    setLayout((prev) => splitRight(prev, kind));
  }, []);

  const handleSplitDown = useCallback((kind: PaneKind = "code") => {
    setLayout((prev) => splitDown(prev, kind));
  }, []);

  const handleClosePane = useCallback((paneId: string) => {
    setLayout((prev) => closePaneInLayout(prev, paneId));
  }, []);

  const handleResizeColumns = useCallback(
    (leftId: string, leftWeight: number, rightId: string, rightWeight: number) => {
      setLayout((prev) => {
        const next = setColumnWeight(prev, leftId, leftWeight);
        return setColumnWeight(next, rightId, rightWeight);
      });
    },
    [],
  );

  const handleResizePanes = useCallback(
    (topId: string, topWeight: number, bottomId: string, bottomWeight: number) => {
      setLayout((prev) => {
        const next = setPaneWeight(prev, topId, topWeight);
        return setPaneWeight(next, bottomId, bottomWeight);
      });
    },
    [],
  );

  const handleSplitWithTab = useCallback(
    (fromPaneId: string, toPaneId: string, path: string, edge: SplitEdge) => {
      setLayout((prev) => splitWithTabInLayout(prev, fromPaneId, toPaneId, path, edge));
    },
    [],
  );

  const handleMovePaneToEdge = useCallback(
    (paneId: string, targetPaneId: string, edge: SplitEdge) => {
      setLayout((prev) => movePaneToEdgeInLayout(prev, paneId, targetPaneId, edge));
    },
    [],
  );

  const handleSwapPanes = useCallback((paneIdA: string, paneIdB: string) => {
    setLayout((prev) => swapPanesInLayout(prev, paneIdA, paneIdB));
  }, []);

  const handleConvertPane = useCallback((paneId: string, kind: PaneKind) => {
    setLayout((prev) => convertPaneInLayout(prev, paneId, kind));
  }, []);

  const handleSplitFromPane = useCallback((paneId: string, direction: "right" | "down") => {
    setLayout((prev) => {
      const withActive = setActivePaneInLayout(prev, paneId);
      return direction === "right" ? splitRight(withActive) : splitDown(withActive);
    });
  }, []);

  const handleToggleFullscreen = useCallback((paneId: string) => {
    setFullscreenPaneId((prev) => (prev === paneId ? null : paneId));
  }, []);

  // Clear fullscreen when the target pane leaves the layout (closed / pruned).
  useEffect(() => {
    if (fullscreenPaneId !== null && !getAllPanes(layout).some((p) => p.id === fullscreenPaneId)) {
      setFullscreenPaneId(null);
    }
  }, [fullscreenPaneId, layout]);

  const handleMarkDirty = useCallback((path: string, isDirty: boolean) => {
    setDirtyFiles((prev) => {
      if (isDirty && prev.has(path)) return prev;
      if (!isDirty && !prev.has(path)) return prev;
      const next = new Set(prev);
      if (isDirty) next.add(path);
      else next.delete(path);
      return next;
    });
  }, []);

  const handlePathMoved = useCallback((oldPath: string, newPath: string) => {
    const remap = (p: string) => remapPath(oldPath, newPath, p);
    setLayout((prev) => remapLayoutPaths(prev, remap));
    setDirtyFiles((prev) => {
      let changed = false;
      const next = new Set<string>();
      for (const p of prev) {
        const remapped = remap(p);
        if (remapped !== p) changed = true;
        next.add(remapped);
      }
      return changed ? next : prev;
    });
  }, []);

  // Prune dirty entries when their path leaves all panes.
  const openPaths = useMemo(() => getAllOpenPaths(layout), [layout]);
  useEffect(() => {
    setDirtyFiles((prev) => {
      let changed = false;
      const next = new Set<string>();
      for (const p of prev) {
        if (openPaths.has(p)) next.add(p);
        else changed = true;
      }
      return changed ? next : prev;
    });
  }, [openPaths]);

  const closeProject = useCallback(() => {
    setLayout(EMPTY_LAYOUT);
    setProjectPath(null);
  }, []);

  const switchProject = useCallback((path: string) => {
    setLayout(EMPTY_LAYOUT);
    setProjectPath(path);
    setRecentProjects(recordRecentProject(path));
  }, []);

  const openProject = useCallback(() => {
    void pickProjectFolder().then((path) => {
      if (path !== null) switchProject(path);
    });
  }, [switchProject]);

  const revealProject = useCallback(() => {
    if (projectPath === null) return;
    void rpcCall<null>("revealInExplorer", { path: projectPath }).catch((err) => {
      console.error("[App] reveal project failed:", err);
    });
  }, [projectPath]);

  // Layout persistence: load when project changes, save once the load commits.
  // `layoutProject` tracks which project the current `layout` belongs to, so a
  // save fired in the same render as a project switch sees a mismatch and skips
  // (otherwise the previous project's layout would clobber the new one's slot).
  const [layoutProject, setLayoutProject] = useState<string | null>(null);
  useEffect(() => {
    if (projectPath === null) {
      setLayout(EMPTY_LAYOUT);
      setLayoutProject(null);
      return;
    }
    const loaded = loadLayout(projectPath);
    setLayout(loaded ?? EMPTY_LAYOUT);
    setLayoutProject(projectPath);
  }, [projectPath]);
  useEffect(() => {
    if (projectPath === null || layoutProject !== projectPath) return;
    // Debounce the save so drag-resize (which fires one layout update per
    // pixel) doesn't pound localStorage + JSON.stringify.
    const handle = setTimeout(() => saveLayout(projectPath, layout), 150);
    return () => clearTimeout(handle);
  }, [projectPath, layoutProject, layout]);

  // Single `fileChanged` subscription handles both the debounced palette file
  // list refresh and tab cleanup for deleted files. The flat list is only
  // relevant while a project is open; if none is, we clear entries and skip
  // subscribing — the layout is EMPTY_LAYOUT so there are no tabs to close.
  useEffect(() => {
    if (projectPath === null) {
      setFileEntries([]);
      return;
    }
    let cancelled = false;
    let refreshTimer: ReturnType<typeof setTimeout> | null = null;

    const refresh = () => {
      void rpcCall<string[]>("listAllFiles", { path: projectPath })
        .then((files) => {
          if (!cancelled) setFileEntries(files);
        })
        .catch((err) => {
          console.error("[App] listAllFiles failed:", err);
        });
    };

    refreshFilesRef.current = refresh;
    refresh();

    const unsubscribe = subscribe<FileChangedEvent>("fileChanged", (event) => {
      if (event.kind === "deleted") {
        setLayout((prev) => closeTabsEverywhere(prev, [event.path]));
      }
      // Debounce: a burst of sidebar mutations should trigger one refetch.
      if (refreshTimer !== null) clearTimeout(refreshTimer);
      refreshTimer = setTimeout(refresh, 300);
    });

    return () => {
      cancelled = true;
      if (refreshTimer !== null) clearTimeout(refreshTimer);
      unsubscribe();
    };
  }, [projectPath]);

  // Hydrate recent-projects list from localStorage on mount. `switchProject`
  // updates it on each open, so there's no need to re-read on project change.
  useEffect(() => {
    setRecentProjects(loadRecentProjects());
  }, []);

  // Latest values read inside the keyboard handler without re-binding on every change.
  const layoutRef = useRef(layout);
  useEffect(() => {
    layoutRef.current = layout;
  }, [layout]);

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

  const appActions = useMemo<PaletteAction[]>(
    () =>
      buildProjectActions(projectPath, recentProjects, {
        openProject,
        closeProject,
        revealProject,
        switchProject,
      }),
    [projectPath, recentProjects, openProject, closeProject, revealProject, switchProject],
  );

  const settingsActions = useMemo<PaletteAction[]>(
    () => [
      {
        id: "app.openSettings",
        label: "Settings",
        scope: PALETTE_SCOPE.App,
        cn: "设置",
        run: () => setSettingsOpen(true),
      },
    ],
    [],
  );

  // Palette just opens the dossier; the Run-Compile button inside kicks off
  // the pipeline. Surfacing state before acting lets the user see asset and
  // template counts before committing to a multi-minute Unity build.
  const compileActions = useMemo<PaletteAction[]>(() => {
    if (projectPath === null) return [];
    return [
      {
        id: "project.compile",
        label: "Compile",
        scope: PALETTE_SCOPE.Project,
        cn: "编译",
        desc: "Build the mod.",
        run: () => setCompileOpen(true),
      },
    ];
  }, [projectPath]);

  // Keep paneActions cheap: it only cares about topology (active id + pane id
  // order), not weights. Depending on `layout` directly rebuilds the palette
  // registry once per pixel during drag-resize. Use `` as the delimiter
  // so we can round-trip safely — pane ids are base36/underscore only.
  const PANE_ID_DELIM = "";
  const paneIdSignature = useMemo(
    () =>
      getAllPanes(layout)
        .map((p) => p.id)
        .join(PANE_ID_DELIM),
    [layout],
  );
  const activePaneId = layout.activePaneId;

  const paneActions = useMemo<PaletteAction[]>(() => {
    if (projectPath === null) return [];
    const paneIds = paneIdSignature.length > 0 ? paneIdSignature.split(PANE_ID_DELIM) : [];
    const active = activePaneId !== null && paneIds.includes(activePaneId) ? activePaneId : null;
    const actions: PaletteAction[] = [];
    for (const kind of Object.keys(BROWSER_KIND_META) as BrowserPane["kind"][]) {
      const meta = BROWSER_KIND_META[kind];
      actions.push({
        id: `view.openBrowser:${kind}`,
        label: `Open ${meta.label}`,
        scope: PALETTE_SCOPE.View,
        run: () => handleSplitRight(kind),
      });
    }
    actions.push(
      {
        id: "view.splitRight",
        label: "Split Right",
        scope: PALETTE_SCOPE.View,
        cn: "右分屏",
        kbd: "Ctrl+\\",
        run: handleSplitRight,
      },
      {
        id: "view.splitDown",
        label: "Split Down",
        scope: PALETTE_SCOPE.View,
        cn: "下分屏",
        kbd: "Ctrl+Shift+\\",
        run: handleSplitDown,
      },
    );
    if (active !== null) {
      actions.push({
        id: "view.closePane",
        label: "Close Pane",
        scope: PALETTE_SCOPE.View,
        cn: "关闭面板",
        kbd: "Ctrl+Shift+W",
        run: () => handleClosePane(active),
      });
    }
    if (paneIds.length > 1 && active !== null) {
      const idx = paneIds.indexOf(active);
      const nextId = paneIds[(idx + 1) % paneIds.length]!;
      const prevId = paneIds[(idx - 1 + paneIds.length) % paneIds.length]!;
      actions.push({
        id: "view.focusNextPane",
        label: "Focus Next Pane",
        scope: PALETTE_SCOPE.View,
        kbd: "Ctrl+Shift+]",
        run: () => handleSetActivePane(nextId),
      });
      actions.push({
        id: "view.focusPrevPane",
        label: "Focus Previous Pane",
        scope: PALETTE_SCOPE.View,
        kbd: "Ctrl+Shift+[",
        run: () => handleSetActivePane(prevId),
      });
    }
    return actions;
  }, [
    projectPath,
    paneIdSignature,
    activePaneId,
    handleSplitRight,
    handleSplitDown,
    handleClosePane,
    handleSetActivePane,
  ]);

  const fileActions = useMemo<PaletteAction[]>(() => {
    if (projectPath === null) return [];
    const root = projectPath;
    return fileEntries.map((rel) => ({
      id: `file:${rel}`,
      label: rel,
      scope: PALETTE_SCOPE.GoToFile,
      run: () => handleOpenFile(join(root, rel)),
    }));
  }, [projectPath, fileEntries, handleOpenFile]);

  useRegisterActions(appActions);
  useRegisterActions(settingsActions);
  useRegisterActions(compileActions);
  useRegisterActions(paneActions);
  useRegisterActions(fileActions);

  const allActions = useRegisteredActions();
  const allActionsRef = useRef(allActions);
  useEffect(() => {
    allActionsRef.current = allActions;
  }, [allActions]);

  useEffect(() => {
    const runActionById = (id: string): boolean => {
      const action = allActionsRef.current.find((a) => a.id === id);
      if (!action) return false;
      void action.run();
      return true;
    };
    const focusAdjacentPane = (direction: 1 | -1): boolean => {
      const panes = getAllPanes(layoutRef.current);
      const active = getActivePane(layoutRef.current);
      if (active === null || panes.length < 2) return false;
      const idx = panes.findIndex((p) => p.id === active.id);
      const target = panes[(idx + direction + panes.length) % panes.length]!;
      handleSetActivePane(target.id);
      return true;
    };
    const shortcuts: { binding: KeyBinding; run: () => boolean }[] = [
      {
        binding: { key: "Escape" },
        run: () => {
          if (fullscreenPaneId === null) return false;
          setFullscreenPaneId(null);
          return true;
        },
      },
      {
        binding: { mod: true, shift: true, key: "p" },
        run: () => {
          setPaletteOpen((o) => !o);
          return true;
        },
      },
      {
        binding: { mod: true, key: "k" },
        run: () => {
          setPaletteOpen((o) => !o);
          return true;
        },
      },
      // Ctrl+S delegates to the palette-registered save action so it works
      // even when the editor doesn't have keyboard focus.
      { binding: { mod: true, key: "s" }, run: () => runActionById("editor.save") },
      {
        binding: { mod: true, key: "w" },
        run: () => {
          const active = getActiveCodePane(layoutRef.current);
          if (active === null || active.activeTab === null) return false;
          handleCloseTabsInPane(active.id, [active.activeTab]);
          return true;
        },
      },
      {
        binding: { mod: true, shift: true, key: "w" },
        run: () => {
          const active = getActivePane(layoutRef.current);
          if (active === null) return false;
          handleClosePane(active.id);
          return true;
        },
      },
      {
        binding: { mod: true, key: "\\" },
        run: () => {
          handleSplitRight();
          return true;
        },
      },
      {
        binding: { mod: true, shift: true, key: "|" },
        run: () => {
          handleSplitDown();
          return true;
        },
      },
      { binding: { mod: true, shift: true, key: "]" }, run: () => focusAdjacentPane(1) },
      { binding: { mod: true, shift: true, key: "[" }, run: () => focusAdjacentPane(-1) },
      {
        binding: { mod: true, shift: true, key: "b" },
        run: () => {
          if (projectPathRef.current === null) return false;
          if (compileStateRef.current.status === "running") return false;
          startCompileRef.current();
          return true;
        },
      },
    ];
    const onKey = (e: KeyboardEvent) => {
      for (const { binding, run } of shortcuts) {
        if (!matchBinding(e, binding)) continue;
        if (run()) e.preventDefault();
        return;
      }
    };
    window.addEventListener("keydown", onKey);
    return () => window.removeEventListener("keydown", onKey);
  }, [
    fullscreenPaneId,
    handleCloseTabsInPane,
    handleClosePane,
    handleSplitRight,
    handleSplitDown,
    handleSetActivePane,
  ]);

  return (
    <div className={styles.shell}>
      {projectPath === null ? (
        <WelcomeScreen onOpenProject={switchProject} />
      ) : (
        <>
          <Topbar projectName={basename(projectPath)} onOpenPalette={() => setPaletteOpen(true)} />
          <div className={styles.main}>
            <Sidebar
              projectPath={projectPath}
              onOpenFile={handleOpenFile}
              dirtyPaths={dirtyFiles}
              onPathMoved={handlePathMoved}
              revealPath={revealRequest}
            />
            <EditorGrid
              ref={editorGridRef}
              projectPath={projectPath}
              layout={layout}
              fullscreenPaneId={fullscreenPaneId}
              dirtyFiles={dirtyFiles}
              fileEntries={fileEntries}
              lastCodePath={lastCodePathRef.current}
              onSelectTab={handleSelectTab}
              onSetActivePane={handleSetActivePane}
              onCloseTabsInPane={handleCloseTabsInPane}
              onClosePane={handleClosePane}
              onMoveTab={handleMoveTab}
              onMarkDirty={handleMarkDirty}
              onOpenBrowserPane={handleSplitRight}
              onOpenFile={handleOpenFile}
              onAppendToFile={handleAppendToFile}
              onRefreshFiles={handleRefreshFiles}
              onResizeColumns={handleResizeColumns}
              onResizePanes={handleResizePanes}
              onSplitWithTab={handleSplitWithTab}
              onMovePaneToEdge={handleMovePaneToEdge}
              onSwapPanes={handleSwapPanes}
              onConvertPane={handleConvertPane}
              onSplitFromPane={handleSplitFromPane}
              onToggleFullscreen={handleToggleFullscreen}
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
