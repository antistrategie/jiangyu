import { useCallback, useEffect, useMemo, useRef, useState } from "react";
import { Topbar } from "./components/Topbar/Topbar.tsx";
import { Sidebar } from "./components/Sidebar/Sidebar.tsx";
import { EditorArea } from "./components/EditorArea/EditorArea.tsx";
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
import styles from "./App.module.css";

export interface OpenFile {
  path: string;
  name: string;
}

export function App() {
  const [projectPath, setProjectPath] = useState<string | null>(null);
  const [openFiles, setOpenFiles] = useState<OpenFile[]>([]);
  const [activeFile, setActiveFile] = useState<string | null>(null);
  const [dirtyFiles, setDirtyFiles] = useState<Set<string>>(new Set());
  const [paletteOpen, setPaletteOpen] = useState(false);
  const [fileEntries, setFileEntries] = useState<readonly string[]>([]);
  const [recentProjects, setRecentProjects] = useState<readonly string[]>([]);

  const handleOpenFile = useCallback((path: string) => {
    setOpenFiles((prev) => {
      if (prev.some((f) => f.path === path)) return prev;
      return [...prev, { path, name: basename(path) }];
    });
    setActiveFile(path);
  }, []);

  const handleCloseFiles = useCallback((paths: string[]) => {
    if (paths.length === 0) return;
    const closing = new Set(paths);
    let nextOpen: OpenFile[] | null = null;
    setOpenFiles((prev) => {
      nextOpen = prev.filter((f) => !closing.has(f.path));
      return nextOpen.length === prev.length ? prev : nextOpen;
    });
    setActiveFile((current) => {
      if (current === null || !closing.has(current)) return current;
      return nextOpen?.[nextOpen.length - 1]?.path ?? null;
    });
    setDirtyFiles((prev) => {
      let changed = false;
      const next = new Set(prev);
      for (const p of paths) {
        if (next.delete(p)) changed = true;
      }
      return changed ? next : prev;
    });
  }, []);

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
    setOpenFiles((prev) => {
      let changed = false;
      const next = prev.map((f) => {
        const remapped = remap(f.path);
        if (remapped === f.path) return f;
        changed = true;
        return { path: remapped, name: basename(remapped) };
      });
      return changed ? next : prev;
    });
    setActiveFile((current) => (current === null ? current : remap(current)));
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

  // Reading openFiles through a ref keeps closeProject/switchProject identities
  // stable across tab open/close — otherwise every tab change churns appActions
  // and triggers palette re-registration.
  const openFilesRef = useRef(openFiles);
  useEffect(() => {
    openFilesRef.current = openFiles;
  }, [openFiles]);

  const closeProject = useCallback(() => {
    handleCloseFiles(openFilesRef.current.map((f) => f.path));
    setProjectPath(null);
  }, [handleCloseFiles]);

  const switchProject = useCallback(
    (path: string) => {
      handleCloseFiles(openFilesRef.current.map((f) => f.path));
      setProjectPath(path);
    },
    [handleCloseFiles],
  );

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
        if (activeFile === null) return;
        e.preventDefault();
        handleCloseFiles([activeFile]);
      }
    };
    window.addEventListener("keydown", onKey);
    return () => window.removeEventListener("keydown", onKey);
  }, [activeFile, handleCloseFiles]);

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
            <EditorArea
              projectPath={projectPath}
              openFiles={openFiles}
              activeFile={activeFile}
              dirtyFiles={dirtyFiles}
              onSelectFile={setActiveFile}
              onCloseFiles={handleCloseFiles}
              onMarkDirty={handleMarkDirty}
            />
          </div>
        </>
      )}
      <Palette open={paletteOpen} onClose={() => setPaletteOpen(false)} actions={allActions} />
    </div>
  );
}
