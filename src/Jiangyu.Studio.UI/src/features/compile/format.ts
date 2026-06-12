// Compile result formatting helpers. A sibling of store.ts / subscriptions.ts
// rather than part of index.ts so the subscription wiring can import them
// without pulling the whole feature entry point into the import cycle.

export function buildSuccessDetail(
  duration: string | null,
  warnings: number,
  bundlePath: string | null,
): string | null {
  const parts: string[] = [];
  if (duration !== null) parts.push(duration);
  if (warnings > 0) parts.push(`${warnings} warning${warnings === 1 ? "" : "s"}`);
  if (bundlePath !== null) parts.push(bundlePath);
  return parts.length === 0 ? null : parts.join(" · ");
}

export function formatDurationShort(ms: number): string {
  const total = Math.floor(ms / 1000);
  const mins = Math.floor(total / 60);
  const secs = total % 60;
  if (mins === 0) return `${secs}s`;
  return `${mins}m${secs.toString().padStart(2, "0")}s`;
}
