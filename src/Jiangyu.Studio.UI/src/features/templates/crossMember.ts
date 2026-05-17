// Cross-window member drag: used when a template browser member row is dragged
// into a visual editor (possibly in another window). Rides on text/plain
// because custom mimetypes don't bridge WebKitGTK's X11 DnD.

export const CROSS_MEMBER_MIME = "text/plain";
const MARKER = "jiangyu-member-drag/1";

export interface CrossMemberPayload {
  readonly m: typeof MARKER;
  readonly templateType: string;
  readonly fieldPath: string;
  /**
   * Schema hints copied from the source TemplateMember at drag-start so the
   * drop site can build a correctly-typed default directive without a
   * round-trip to the catalog. Drops that arrive in a window with no catalog
   * still get the right value kind.
   */
  readonly typeName?: string;
  readonly patchScalarKind?: string;
  readonly elementTypeName?: string;
  readonly enumTypeName?: string;
  readonly referenceTypeName?: string;
  readonly isCollection?: boolean;
  readonly isScalar?: boolean;
  readonly isTemplateReference?: boolean;
  readonly namedArrayEnumTypeName?: string;
}

export function encodeCrossMemberPayload(payload: Omit<CrossMemberPayload, "m">): string {
  return JSON.stringify({ m: MARKER, ...payload });
}

export function parseCrossMemberPayload(raw: string): CrossMemberPayload | null {
  if (raw.length === 0) return null;
  try {
    const parsed = JSON.parse(raw) as Partial<CrossMemberPayload>;
    if (parsed.m !== MARKER) return null;
    if (typeof parsed.templateType !== "string" || typeof parsed.fieldPath !== "string")
      return null;
    return parsed as CrossMemberPayload;
  } catch {
    return null;
  }
}
