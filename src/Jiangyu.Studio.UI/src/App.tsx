import { useState, useCallback } from "react";
import { Topbar } from "./components/Topbar/Topbar.tsx";
import { Sidebar } from "./components/Sidebar/Sidebar.tsx";
import { EditorArea } from "./components/EditorArea/EditorArea.tsx";
import { WelcomeScreen } from "./components/WelcomeScreen/WelcomeScreen.tsx";
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
      const name = path.split("/").pop() ?? path;
      return [...prev, { path, name }];
    });
    setActiveFile(path);
  }, []);

  const handleCloseFile = useCallback(
    (path: string) => {
      setOpenFiles((prev) => {
        const next = prev.filter((f) => f.path !== path);
        return next;
      });
      setActiveFile((current) => {
        if (current !== path) return current;
        const remaining = openFiles.filter((f) => f.path !== path);
        return remaining[remaining.length - 1]?.path ?? null;
      });
      setDirtyFiles((prev) => {
        if (!prev.has(path)) return prev;
        const next = new Set(prev);
        next.delete(path);
        return next;
      });
    },
    [openFiles],
  );

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

  return (
    <div className={styles.shell}>
      <Topbar projectName={projectPath ? projectPath.split("/").pop() : null} />
      {projectPath === null ? (
        <WelcomeScreen onOpenProject={setProjectPath} />
      ) : (
        <div className={styles.main}>
          <Sidebar projectPath={projectPath} onOpenFile={handleOpenFile} dirtyPaths={dirtyFiles} />
          <EditorArea
            openFiles={openFiles}
            activeFile={activeFile}
            dirtyFiles={dirtyFiles}
            onSelectFile={setActiveFile}
            onCloseFile={handleCloseFile}
            onMarkDirty={handleMarkDirty}
          />
        </div>
      )}
    </div>
  );
}
