/** Clamp a zoom level to the allowed range. */
export function clampZoom(value: number, min: number, max: number): number {
  return Math.max(min, Math.min(max, value));
}

/**
 * Compute the new offset after zooming towards a fixed point (e.g. the cursor).
 * The point at `cursor` in container-space stays visually pinned.
 */
export function zoomTowardsCursor(
  cursor: number,
  oldOffset: number,
  oldZoom: number,
  newZoom: number,
): number {
  const scale = newZoom / oldZoom;
  return cursor - scale * (cursor - oldOffset);
}
