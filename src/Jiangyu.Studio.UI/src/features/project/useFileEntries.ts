import { useCallback, useEffect, useRef, useState } from "react";
import { rpcCall, subscribe, type FileChangedEvent } from "@shared/rpc";

// Keeps a flat list of all project files (for palette go-to-file) in sync
// with the filesystem. A single `fileChanged` subscription fans out to both
// a debounced refetch (300ms) and the caller's `onDeleted` callback — the
// consumer uses that to close editor tabs for deleted files.
//
// `refreshFiles` is exposed so explicit flushes (e.g. after a scaffold op
// writes a new file and wants to see it in the palette immediately) can
// request a refetch without waiting for the watcher to catch up.
export function useFileEntries(
  projectPath: string | null,
  onDeleted: (path: string) => void,
): { readonly fileEntries: readonly string[]; readonly refreshFiles: () => void } {
  const [fileEntries, setFileEntries] = useState<readonly string[]>([]);
  const refreshRef = useRef<() => void>(() => {});
  const onDeletedRef = useRef(onDeleted);
  useEffect(() => {
    onDeletedRef.current = onDeleted;
  }, [onDeleted]);

  // Reset to empty synchronously when the project closes so palette
  // searches don't briefly hit the previous project's files.
  const [prevPath, setPrevPath] = useState(projectPath);
  if (prevPath !== projectPath) {
    setPrevPath(projectPath);
    setFileEntries([]);
  }

  useEffect(() => {
    if (projectPath === null) {
      refreshRef.current = () => {};
      return;
    }
    let cancelled = false;
    let refreshTimer: ReturnType<typeof setTimeout> | null = null;

    const refresh = () => {
      void rpcCall<string[]>("listAllFiles", { path: projectPath })
        .then((files) => {
          if (!cancelled) setFileEntries(files);
        })
        .catch((err: unknown) => {
          console.error("[useFileEntries] listAllFiles failed:", err);
        });
    };

    refreshRef.current = refresh;
    refresh();

    const unsubscribe = subscribe("fileChanged", (params) => {
      const event = params as FileChangedEvent;
      if (event.kind === "deleted") onDeletedRef.current(event.path);
      if (refreshTimer !== null) clearTimeout(refreshTimer);
      refreshTimer = setTimeout(refresh, 300);
    });

    return () => {
      cancelled = true;
      if (refreshTimer !== null) clearTimeout(refreshTimer);
      unsubscribe();
    };
  }, [projectPath]);

  // Wrap refreshRef.current in a stable callback so consumers can pass it as
  // a prop without re-binding on every render.
  const refreshFiles = useCallback(() => refreshRef.current(), []);
  return { fileEntries, refreshFiles };
}
