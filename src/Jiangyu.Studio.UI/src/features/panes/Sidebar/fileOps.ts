import { rpcCall } from "@shared/rpc";
import { dirname, basename, join } from "@shared/path";
import type { Toast } from "@shared/toast";

// File-system operations behind the sidebar tree: thin RPC wrappers that
// report failures as toasts and tell the tree which directories to refresh.
// Kept free of React so the components call them as plain async functions.

export type PushToast = (toast: Omit<Toast, "id" | "sticker">) => void;

export interface FileEntry {
  readonly name: string;
  readonly path: string;
  readonly isDirectory: boolean;
  readonly isIgnored?: boolean;
}

export interface ClipboardState {
  readonly paths: string[];
  readonly mode: "cut" | "copy";
}

// The slice of sidebar state the operations need. SidebarContextValue
// satisfies it structurally.
export interface SidebarOpsContext {
  readonly clipboard: ClipboardState | null;
  readonly setClipboard: (c: ClipboardState | null) => void;
  readonly invalidateDir: (path: string) => void;
  readonly onPathMoved: (oldPath: string, newPath: string) => void;
  readonly pushToast: PushToast;
  readonly confirmDelete: (entry: FileEntry) => Promise<boolean>;
}

export type NameValidation = { value: string; error: null } | { value: null; error: string | null };

// Returns `{ value: null, error: null }` for an empty name — callers treat that
// as "silently cancel", not "show an error". Only structurally-bad names (e.g.
// containing `/`) surface an error string.
export function validateName(name: string): NameValidation {
  const trimmed = name.trim();
  if (trimmed.length === 0) return { value: null, error: null };
  if (trimmed.includes("/")) return { value: null, error: "Name cannot contain '/'" };
  return { value: trimmed, error: null };
}

export function reportRpcError(push: PushToast, action: string, err: unknown): void {
  push({
    variant: "error",
    message: `${action} failed`,
    detail: (err as Error).message,
  });
}

export async function pasteClipboard(ctx: SidebarOpsContext, destDir: string): Promise<void> {
  const cb = ctx.clipboard;
  if (!cb) return;
  const method = cb.mode === "cut" ? "movePath" : "copyPath";
  const affectedDirs = new Set<string>([destDir]);
  const failures: string[] = [];
  const results = await Promise.allSettled(
    cb.paths.map((srcPath) => {
      const destPath = join(destDir, basename(srcPath));
      return rpcCall<null>(method, { srcPath, destPath }).then(() => ({ srcPath, destPath }));
    }),
  );
  for (const r of results) {
    if (r.status === "fulfilled") {
      if (cb.mode === "cut") {
        affectedDirs.add(dirname(r.value.srcPath));
        ctx.onPathMoved(r.value.srcPath, r.value.destPath);
      }
    } else {
      failures.push((r.reason as Error).message);
    }
  }
  for (const d of affectedDirs) ctx.invalidateDir(d);
  if (cb.mode === "cut") ctx.setClipboard(null);
  if (failures.length > 0) {
    ctx.pushToast({
      variant: "error",
      message: `Paste failed for ${failures.length} item${failures.length === 1 ? "" : "s"}`,
      detail: failures.join("; "),
    });
  }
}

export async function duplicate(entry: FileEntry, ctx: SidebarOpsContext): Promise<void> {
  const destPath = copyCandidatePath(entry.path);
  try {
    await rpcCall<null>("copyPath", { srcPath: entry.path, destPath });
    ctx.invalidateDir(dirname(destPath));
  } catch (err) {
    reportRpcError(ctx.pushToast, "Duplicate", err);
  }
}

function copyCandidatePath(path: string): string {
  const dir = dirname(path);
  const name = basename(path);
  const dot = name.lastIndexOf(".");
  const stem = dot > 0 ? name.slice(0, dot) : name;
  const ext = dot > 0 ? name.slice(dot) : "";
  return join(dir, `${stem} copy${ext}`);
}

export async function moveWithFeedback(
  srcPath: string,
  destPath: string,
  ctx: SidebarOpsContext,
): Promise<void> {
  try {
    await rpcCall<null>("movePath", { srcPath, destPath });
    ctx.onPathMoved(srcPath, destPath);
    ctx.invalidateDir(dirname(srcPath));
    ctx.invalidateDir(dirname(destPath));
  } catch (err) {
    reportRpcError(ctx.pushToast, "Move", err);
  }
}

export async function performDelete(entry: FileEntry, ctx: SidebarOpsContext): Promise<void> {
  const confirmed = await ctx.confirmDelete(entry);
  if (!confirmed) return;
  try {
    await rpcCall<null>("deletePath", { path: entry.path });
    ctx.invalidateDir(dirname(entry.path));
  } catch (err) {
    reportRpcError(ctx.pushToast, "Delete", err);
  }
}
