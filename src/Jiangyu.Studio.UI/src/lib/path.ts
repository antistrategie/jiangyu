/** Return the parent directory of a POSIX path. */
export function dirname(p: string): string {
  const idx = p.lastIndexOf("/");
  return idx > 0 ? p.slice(0, idx) : p;
}

/** Return the final path segment (filename). */
export function basename(p: string): string {
  return p.split("/").pop() ?? p;
}

/** Join path segments with `/`, stripping redundant separators. */
export function join(...segments: string[]): string {
  return segments
    .filter((s) => s.length > 0)
    .map((s, i) => (i === 0 ? s.replace(/\/+$/, "") : s.replace(/^\/+|\/+$/g, "")))
    .filter((s) => s.length > 0)
    .join("/");
}

/** Compute a path relative to `from`. Returns `to` unchanged if not underneath `from`. */
export function relative(from: string, to: string): string {
  if (to === from) return "";
  const prefix = from.endsWith("/") ? from : from + "/";
  return to.startsWith(prefix) ? to.slice(prefix.length) : to;
}

/** True if `descendant` equals or is under `ancestor`. */
export function isDescendant(ancestor: string, descendant: string): boolean {
  if (descendant === ancestor) return true;
  const prefix = ancestor.endsWith("/") ? ancestor : ancestor + "/";
  return descendant.startsWith(prefix);
}

/**
 * Remap a path when its ancestor has moved. Returns `path` unchanged when it
 * is neither equal to nor underneath `oldBase`.
 */
export function remapPath(oldBase: string, newBase: string, path: string): string {
  if (path === oldBase) return newBase;
  if (isDescendant(oldBase, path)) return join(newBase, relative(oldBase, path));
  return path;
}
