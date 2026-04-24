// Cross-window instance drag: used when a template browser instance row is
// dragged into a visual editor (possibly in another window). Rides on
// text/plain because custom mimetypes don't bridge WebKitGTK's X11 DnD.

export const CROSS_INSTANCE_MIME = "text/plain";
const MARKER = "jiangyu-instance-drag/1";

export interface CrossInstancePayload {
  readonly m: typeof MARKER;
  readonly name: string;
  readonly className: string;
}

export function encodeCrossInstancePayload(payload: Omit<CrossInstancePayload, "m">): string {
  return JSON.stringify({ m: MARKER, ...payload });
}

export function parseCrossInstancePayload(raw: string): CrossInstancePayload | null {
  if (raw.length === 0) return null;
  try {
    const parsed = JSON.parse(raw) as Partial<CrossInstancePayload>;
    if (parsed.m !== MARKER) return null;
    if (typeof parsed.name !== "string" || typeof parsed.className !== "string") return null;
    return parsed as CrossInstancePayload;
  } catch {
    return null;
  }
}
