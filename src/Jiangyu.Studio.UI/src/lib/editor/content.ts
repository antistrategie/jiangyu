import { useEffect } from "react";
import { create } from "zustand";
import type { editor as monacoEditor } from "monaco-editor";
import { rpcCall, subscribe, type FileChangeKind, type FileChangedEvent } from "@lib/rpc.ts";

// In-memory editor state for this window: content buffers, dirty flags,
// conflict banners, and live Monaco editor instances keyed by pane id.
//
// Each window (primary + each secondary) has its own store instance because
// each is a separate webview with its own module scope. The host's file
// watcher fans `fileChanged` out to every window, so all stores stay
// consistent via the shared filesystem: writes suppress the originating
// window's event, but other windows see it as an external change.
interface EditorContentState {
  readonly contents: Readonly<Record<string, string>>;
  readonly dirty: ReadonlySet<string>;
  readonly conflicts: Readonly<Record<string, FileChangeKind>>;
  readonly editors: Readonly<Record<string, monacoEditor.IStandaloneCodeEditor>>;
  // Paths with a readFile in flight — guard so two consumers opening the
  // same file don't double-read and clobber each other.
  readonly inflight: ReadonlySet<string>;

  // Lazy-read content from disk. No-op if already cached or a read is
  // already in flight for this path.
  readonly loadContent: (path: string) => Promise<void>;

  // Overwrite the buffer. Caller is responsible for marking dirty (save()
  // clears it).
  readonly setContent: (path: string, text: string) => void;

  readonly markDirty: (path: string, isDirty: boolean) => void;
  readonly save: (path: string) => Promise<void>;
  readonly reload: (path: string) => Promise<void>;
  readonly dismissConflict: (path: string) => void;

  // Append a snippet to the file, coordinating with any in-flight read.
  // Used by template-snippet flows that want "load + append" without a
  // window where the loaded text overwrites the appended snippet.
  readonly appendToFile: (path: string, snippet: string) => Promise<void>;

  readonly mountEditor: (paneId: string, editor: monacoEditor.IStandaloneCodeEditor) => void;

  // Called by the module-level fileChanged subscription (see
  // useEditorContentSync) for every path we care about.
  readonly onFileChanged: (event: FileChangedEvent) => void;

  // Drop state that no longer has a consumer in the layout.
  // openPaths: paths still open in some code pane.
  // openPaneIds: live code pane ids.
  readonly prune: (openPaths: ReadonlySet<string>, openPaneIds: ReadonlySet<string>) => void;

  // Rekey content/dirty/conflict entries when a file is renamed on disk.
  // Layout remap is separate (layout.remapPaths); both should fire together.
  readonly remapPath: (oldPath: string, newPath: string) => void;
}

function omit<V>(record: Readonly<Record<string, V>>, key: string): Record<string, V> {
  const { [key]: _, ...rest } = record;
  void _;
  return rest;
}

export const useEditorContent = create<EditorContentState>((set, get) => ({
  contents: {},
  dirty: new Set(),
  conflicts: {},
  editors: {},
  inflight: new Set(),

  loadContent: async (path) => {
    const s = get();
    if (path in s.contents || s.inflight.has(path)) return;
    set((prev) => ({ inflight: new Set(prev.inflight).add(path) }));
    try {
      const text = await rpcCall<string>("readFile", { path });
      set((prev) => ({ contents: { ...prev.contents, [path]: text } }));
    } catch (err) {
      console.error("[editor.content] readFile failed:", err);
    } finally {
      set((prev) => {
        const next = new Set(prev.inflight);
        next.delete(path);
        return { inflight: next };
      });
    }
  },

  setContent: (path, text) => {
    set((s) => (s.contents[path] === text ? s : { contents: { ...s.contents, [path]: text } }));
  },

  markDirty: (path, isDirty) => {
    set((s) => {
      if (isDirty && s.dirty.has(path)) return s;
      if (!isDirty && !s.dirty.has(path)) return s;
      const next = new Set(s.dirty);
      if (isDirty) next.add(path);
      else next.delete(path);
      return { dirty: next };
    });
  },

  save: async (path) => {
    const content = get().contents[path];
    if (content === undefined) return;
    try {
      await rpcCall<null>("writeFile", { path, content });
      set((s) => {
        const dirty = new Set(s.dirty);
        dirty.delete(path);
        return { dirty, conflicts: omit(s.conflicts, path) };
      });
    } catch (err) {
      console.error("[editor.content] writeFile failed:", err);
    }
  },

  reload: async (path) => {
    try {
      const text = await rpcCall<string>("readFile", { path });
      set((s) => {
        const dirty = new Set(s.dirty);
        dirty.delete(path);
        return {
          contents: { ...s.contents, [path]: text },
          dirty,
          conflicts: omit(s.conflicts, path),
        };
      });
    } catch (err) {
      console.error("[editor.content] reload failed:", err);
    }
  },

  dismissConflict: (path) => {
    set((s) => (path in s.conflicts ? { conflicts: omit(s.conflicts, path) } : s));
  },

  appendToFile: async (path, snippet) => {
    const s = get();
    const cached = s.contents[path];
    if (cached !== undefined) {
      const sep = cached.length === 0 ? "" : cached.endsWith("\n") ? "\n" : "\n\n";
      set((prev) => ({
        contents: { ...prev.contents, [path]: cached + sep + snippet },
        dirty: new Set(prev.dirty).add(path),
      }));
      return;
    }

    // Load in flight elsewhere — wait for it to land, then append.
    if (s.inflight.has(path)) {
      const deadline = Date.now() + 5000;
      while (
        get().inflight.has(path) &&
        get().contents[path] === undefined &&
        Date.now() < deadline
      ) {
        await new Promise((r) => setTimeout(r, 20));
      }
      const loaded = get().contents[path] ?? "";
      const sep = loaded.length === 0 ? "" : loaded.endsWith("\n") ? "\n" : "\n\n";
      set((prev) => ({
        contents: { ...prev.contents, [path]: loaded + sep + snippet },
        dirty: new Set(prev.dirty).add(path),
      }));
      return;
    }

    // Not cached and nothing in flight — own the load so the content +
    // snippet appear atomically (no window where the loaded text sits
    // without the appended snippet).
    set((prev) => ({ inflight: new Set(prev.inflight).add(path) }));
    try {
      let text = "";
      try {
        text = await rpcCall<string>("readFile", { path });
      } catch {
        // File doesn't exist yet — treat as empty.
      }
      const sep = text.length === 0 ? "" : text.endsWith("\n") ? "\n" : "\n\n";
      set((prev) => ({
        contents: { ...prev.contents, [path]: text + sep + snippet },
        dirty: new Set(prev.dirty).add(path),
      }));
    } finally {
      set((prev) => {
        const next = new Set(prev.inflight);
        next.delete(path);
        return { inflight: next };
      });
    }
  },

  mountEditor: (paneId, editor) => {
    set((s) =>
      s.editors[paneId] === editor ? s : { editors: { ...s.editors, [paneId]: editor } },
    );
    // Self-cleanup: the editor's dispose event drops its entry, so callers
    // don't need a matching unmount call.
    editor.onDidDispose(() => {
      const current = get().editors[paneId];
      if (current !== editor) return;
      set((s) => ({ editors: omit(s.editors, paneId) }));
    });
  },

  onFileChanged: (event) => {
    const s = get();
    if (!(event.path in s.contents)) return;
    if (event.kind === "deleted") {
      set((prev) => ({ conflicts: { ...prev.conflicts, [event.path]: "deleted" } }));
      return;
    }
    if (s.dirty.has(event.path)) {
      set((prev) => ({ conflicts: { ...prev.conflicts, [event.path]: "changed" } }));
      return;
    }
    // Clean buffer — silently reload so external edits are picked up
    // without a banner.
    void rpcCall<string>("readFile", { path: event.path })
      .then((text) => {
        set((prev) =>
          prev.contents[event.path] === text
            ? prev
            : { contents: { ...prev.contents, [event.path]: text } },
        );
      })
      .catch((err) => {
        console.error("[editor.content] silent reload failed:", err);
      });
  },

  remapPath: (oldPath, newPath) => {
    if (oldPath === newPath) return;
    set((s) => {
      const contents = { ...s.contents };
      const conflicts = { ...s.conflicts };
      let changed = false;
      if (oldPath in contents) {
        contents[newPath] = contents[oldPath]!;
        delete contents[oldPath];
        changed = true;
      }
      if (oldPath in conflicts) {
        conflicts[newPath] = conflicts[oldPath]!;
        delete conflicts[oldPath];
        changed = true;
      }
      let dirty: ReadonlySet<string> = s.dirty;
      if (s.dirty.has(oldPath)) {
        const next = new Set(s.dirty);
        next.delete(oldPath);
        next.add(newPath);
        dirty = next;
        changed = true;
      }
      return changed ? { contents, conflicts, dirty } : s;
    });
  },

  prune: (openPaths, openPaneIds) => {
    set((s) => {
      let contents = s.contents;
      let conflicts = s.conflicts;
      let dirty = s.dirty;
      let editors = s.editors;

      const nextContents: Record<string, string> = {};
      let contentsChanged = false;
      for (const [k, v] of Object.entries(s.contents)) {
        if (openPaths.has(k)) nextContents[k] = v;
        else contentsChanged = true;
      }
      if (contentsChanged) contents = nextContents;

      const nextConflicts: Record<string, FileChangeKind> = {};
      let conflictsChanged = false;
      for (const [k, v] of Object.entries(s.conflicts)) {
        if (openPaths.has(k)) nextConflicts[k] = v;
        else conflictsChanged = true;
      }
      if (conflictsChanged) conflicts = nextConflicts;

      const nextDirty = new Set<string>();
      let dirtyChanged = false;
      for (const k of s.dirty) {
        if (openPaths.has(k)) nextDirty.add(k);
        else dirtyChanged = true;
      }
      if (dirtyChanged) dirty = nextDirty;

      const nextEditors: Record<string, monacoEditor.IStandaloneCodeEditor> = {};
      let editorsChanged = false;
      for (const [k, v] of Object.entries(s.editors)) {
        if (openPaneIds.has(k)) nextEditors[k] = v;
        else editorsChanged = true;
      }
      if (editorsChanged) editors = nextEditors;

      if (!contentsChanged && !conflictsChanged && !dirtyChanged && !editorsChanged) return s;
      return { contents, conflicts, dirty, editors };
    });
  },
}));

// Wire the host's fileChanged notification into the store. Mount once per
// window (App / PaneWindow root) — a per-component subscription would fire
// the same handler N times.
export function useEditorContentSync(): void {
  useEffect(() => {
    return subscribe<FileChangedEvent>("fileChanged", (evt) => {
      useEditorContent.getState().onFileChanged(evt);
    });
  }, []);
}
