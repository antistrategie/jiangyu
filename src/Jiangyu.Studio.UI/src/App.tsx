import { useCallback, useEffect, useMemo, useRef, useState } from "react";
import { Topbar } from "./components/Topbar/Topbar.tsx";
import { Sidebar } from "./components/Sidebar/Sidebar.tsx";
import { EditorGrid } from "./components/EditorArea/EditorGrid.tsx";
import { WelcomeScreen } from "./components/WelcomeScreen/WelcomeScreen.tsx";
import { Palette } from "./components/Palette/Palette.tsx";
import { basename, join, remapPath } from "./lib/path.ts";
import { rpcCall, subscribe } from "./lib/rpc.ts";
import { pickProjectFolder } from "./lib/projectCommands.ts";
import {
  PALETTE_SCOPE,
  useRegisterActions,
  useRegisteredActions,
  type PaletteAction,
} from "./lib/actions.tsx";
import { buildProjectActions } from "./lib/appActions.ts";
import {
  BROWSER_KIND_META,
  EMPTY_LAYOUT,
  closePane as closePaneInLayout,
  closeTabs,
  closeTabsEverywhere,
  convertPane as convertPaneInLayout,
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
  const [fileEntries, setFileEntries] = useState<readonly string[]>([]);
  const [recentProjects, setRecentProjects] = useState<readonly string[]>([]);

  const handleOpenFile = useCallback((path: string) => {
    setLayout((prev) => openFileInLayout(prev, path));
  }, []);

  const handleSelectTab = useCallback((paneId: string, path: string) => {
    setLayout((prev) => selectTabInLayout(prev, paneId, path));
  }, []);

  const handleSetActivePane = useCallback((paneId: string) => {
    setLayout((prev) => setActivePaneInLayout(prev, paneId));
  }, []);

  const handleCloseTabsInPane = useCallback((paneId: string, paths: readonly string[]) => {
    setLayout((prev) => closeTabs(prev, paneId, paths));
  }, []);

  const handleMoveTab = useCallback((fromPaneId: string, toPaneId: string, path: string) => {
    setLayout((prev) => moveTabInLayout(prev, fromPaneId, toPaneId, path));
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

  const handleConvertPane = useCallback((paneId: string, kind: PaneKind) => {
    setLayout((prev) => convertPaneInLayout(prev, paneId, kind));
  }, []);

  const handleSplitFromPane = useCallback((paneId: string, direction: "right" | "down") => {
    setLayout((prev) => {
      const withActive = setActivePaneInLayout(prev, paneId);
      return direction === "right" ? splitRight(withActive) : splitDown(withActive);
    });
  }, []);

  // Fullscreen pane is wired to be a no-op for now; the pane-chrome button
  // just lives in the UI until we add a proper modal overlay.
  const handleToggleFullscreen = useCallback((_paneId: string) => {}, []);

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

    refresh();

    // Sidebar mutations (create/delete/rename) and external changes fire
    // fileChanged; debounce so a burst of events only triggers one refetch.
    const unsubscribe = subscribe("fileChanged", () => {
      if (refreshTimer !== null) clearTimeout(refreshTimer);
      refreshTimer = setTimeout(refresh, 300);
    });

    return () => {
      cancelled = true;
      if (refreshTimer !== null) clearTimeout(refreshTimer);
      unsubscribe();
    };
  }, [projectPath]);

  useEffect(() => {
    let cancelled = false;
    void rpcCall<string[]>("getRecentProjects")
      .then((projects) => {
        if (!cancelled) setRecentProjects(projects);
      })
      .catch(() => {});
    return () => {
      cancelled = true;
    };
  }, [projectPath]);

  // Close-on-disk events: drop tabs everywhere and forget the file.
  useEffect(() => {
    return subscribe<{ path: string; kind: string }>("fileChanged", (event) => {
      if (event.kind !== "deleted") return;
      setLayout((prev) => closeTabsEverywhere(prev, [event.path]));
    });
  }, []);

  // Keyboard shortcuts: read latest layout via ref so we don't re-bind on every change.
  const layoutRef = useRef(layout);
  useEffect(() => {
    layoutRef.current = layout;
  }, [layout]);

  useEffect(() => {
    const onKey = (e: KeyboardEvent) => {
      const mod = e.ctrlKey || e.metaKey;
      const key = e.key.toLowerCase();
      if (mod && e.shiftKey && key === "p") {
        e.preventDefault();
        setPaletteOpen((o) => !o);
      } else if (mod && !e.shiftKey && !e.altKey && key === "k") {
        e.preventDefault();
        setPaletteOpen((o) => !o);
      } else if (mod && !e.shiftKey && !e.altKey && key === "w") {
        const active = getActiveCodePane(layoutRef.current);
        if (active === null || active.activeTab === null) return;
        e.preventDefault();
        handleCloseTabsInPane(active.id, [active.activeTab]);
      } else if (mod && e.shiftKey && key === "w") {
        const active = getActivePane(layoutRef.current);
        if (active === null) return;
        e.preventDefault();
        handleClosePane(active.id);
      } else if (mod && !e.shiftKey && !e.altKey && e.key === "\\") {
        e.preventDefault();
        handleSplitRight();
      } else if (mod && e.shiftKey && e.key === "|") {
        e.preventDefault();
        handleSplitDown();
      } else if (mod && e.shiftKey && e.key === "]") {
        const panes = getAllPanes(layoutRef.current);
        const active = getActivePane(layoutRef.current);
        if (active === null || panes.length < 2) return;
        const idx = panes.findIndex((p) => p.id === active.id);
        const next = panes[(idx + 1) % panes.length]!;
        e.preventDefault();
        handleSetActivePane(next.id);
      } else if (mod && e.shiftKey && e.key === "[") {
        const panes = getAllPanes(layoutRef.current);
        const active = getActivePane(layoutRef.current);
        if (active === null || panes.length < 2) return;
        const idx = panes.findIndex((p) => p.id === active.id);
        const prev = panes[(idx - 1 + panes.length) % panes.length]!;
        e.preventDefault();
        handleSetActivePane(prev.id);
      }
    };
    window.addEventListener("keydown", onKey);
    return () => window.removeEventListener("keydown", onKey);
  }, [
    handleCloseTabsInPane,
    handleClosePane,
    handleSplitRight,
    handleSplitDown,
    handleSetActivePane,
  ]);

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

  // Keep paneActions cheap: it only cares about topology (active id + pane id
  // order), not weights. Depending on `layout` directly rebuilds the palette
  // registry once per pixel during drag-resize.
  const paneIdSignature = useMemo(
    () =>
      getAllPanes(layout)
        .map((p) => p.id)
        .join(","),
    [layout],
  );
  const activePaneId = layout.activePaneId;

  const paneActions = useMemo<PaletteAction[]>(() => {
    if (projectPath === null) return [];
    const paneIds = paneIdSignature.length > 0 ? paneIdSignature.split(",") : [];
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
  useRegisterActions(paneActions);
  useRegisterActions(fileActions);

  const allActions = useRegisteredActions();

  return (
    <div className={styles.shell}>
      {projectPath === null ? (
        <WelcomeScreen onOpenProject={setProjectPath} />
      ) : (
        <>
          <Topbar
            projectName={projectPath ? basename(projectPath) : null}
            onOpenPalette={() => setPaletteOpen(true)}
          />
          <div className={styles.main}>
            <Sidebar
              projectPath={projectPath}
              onOpenFile={handleOpenFile}
              dirtyPaths={dirtyFiles}
              onPathMoved={handlePathMoved}
            />
            <EditorGrid
              projectPath={projectPath}
              layout={layout}
              dirtyFiles={dirtyFiles}
              onSelectTab={handleSelectTab}
              onSetActivePane={handleSetActivePane}
              onCloseTabsInPane={handleCloseTabsInPane}
              onClosePane={handleClosePane}
              onMoveTab={handleMoveTab}
              onMarkDirty={handleMarkDirty}
              onOpenBrowserPane={handleSplitRight}
              onResizeColumns={handleResizeColumns}
              onResizePanes={handleResizePanes}
              onSplitWithTab={handleSplitWithTab}
              onMovePaneToEdge={handleMovePaneToEdge}
              onConvertPane={handleConvertPane}
              onSplitFromPane={handleSplitFromPane}
              onToggleFullscreen={handleToggleFullscreen}
            />
          </div>
        </>
      )}
      <Palette open={paletteOpen} onClose={() => setPaletteOpen(false)} actions={allActions} />
    </div>
  );
}
