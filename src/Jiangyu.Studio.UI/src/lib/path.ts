/** Return the parent directory of a POSIX path. */
export function dirname(p: string): string {
  const idx = p.lastIndexOf("/");
  return idx > 0 ? p.slice(0, idx) : p;
}

/** Return the final path segment (filename). */
export function basename(p: string): string {
  return p.split("/").pop() ?? p;
}
