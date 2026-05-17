// Cross-window instance drag: used when a template browser instance row is
// dragged into a visual editor (possibly in another window). Rides on
// text/plain because custom mimetypes don't bridge WebKitGTK's X11 DnD.

export const CROSS_INSTANCE_MIME = "text/plain";

// Secondary tag MIMEs set alongside text/plain. Used at dragOver time when
// the payload data is not yet readable, to:
//   - skip activating pane-split overlays for template-browser drags
//   - distinguish instance vs member drags so the visual editor can show
//     accept vs reject styles on its two drop zones
// Not reliable cross-window on WebKitGTK's X11 DnD — cross-window drags fall
// back to accepting any text/plain and discriminating at drop time.
export const TEMPLATE_DRAG_TAG = "application/x-jiangyu-template-drag";
export const INSTANCE_DRAG_TAG = "application/x-jiangyu-instance-drag";
export const MEMBER_DRAG_TAG = "application/x-jiangyu-member-drag";

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

// --- Same-window drag context ---
//
// The DataTransfer payload isn't readable during dragOver (browser security),
// so drop targets can only see `types`. That's enough for kind (instance vs
// member) but not for richer decisions like "does the source field belong to
// the target template type?". A module-level store set on dragStart and
// cleared on dragEnd fills that gap for same-window drags. Cross-window drags
// leave `active` null; drop targets must fall back to payload validation at
// drop time.

export type ActiveTemplateDrag =
  | { readonly kind: "instance"; readonly name: string; readonly className: string }
  | { readonly kind: "member"; readonly templateType: string; readonly fieldPath: string };

let active: ActiveTemplateDrag | null = null;

export function beginTemplateDrag(info: ActiveTemplateDrag): void {
  active = info;
}

export function endTemplateDrag(): void {
  active = null;
}

export function getActiveTemplateDrag(): ActiveTemplateDrag | null {
  return active;
}
