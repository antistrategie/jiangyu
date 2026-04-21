import { useState, useCallback } from "react";
import { Topbar } from "./components/Topbar/Topbar.tsx";
import { Sidebar } from "./components/Sidebar/Sidebar.tsx";
import { EditorArea } from "./components/EditorArea/EditorArea.tsx";
import { WelcomeScreen } from "./components/WelcomeScreen/WelcomeScreen.tsx";
import { basename, remapPath } from "./lib/path.ts";
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

  return (
    <div className={styles.shell}>
      <Topbar projectName={projectPath ? basename(projectPath) : null} />
      {projectPath === null ? (
        <WelcomeScreen onOpenProject={setProjectPath} />
      ) : (
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
      )}
    </div>
  );
}
