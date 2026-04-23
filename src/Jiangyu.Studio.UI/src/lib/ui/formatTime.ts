/** Format a duration in seconds as `M:SS`. Non-finite values return `"0:00"`. */
export function formatTime(s: number): string {
  if (!isFinite(s) || s < 0) return "0:00";
  const mins = Math.floor(s / 60);
  const secs = Math.floor(s % 60);
  return `${mins}:${secs.toString().padStart(2, "0")}`;
}
