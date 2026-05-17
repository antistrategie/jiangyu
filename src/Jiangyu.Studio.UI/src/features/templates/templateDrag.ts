import {
  TEMPLATE_DRAG_TAG,
  INSTANCE_DRAG_TAG,
  MEMBER_DRAG_TAG,
  parseCrossInstancePayload,
} from "./crossInstance";
import { parseCrossMemberPayload } from "./crossMember";

interface DragDataLike {
  readonly types: readonly string[];
  getData: (format: string) => string;
}

export function isTemplateDragPayload(data: DragDataLike): boolean {
  const types = data.types;
  if (
    types.includes(TEMPLATE_DRAG_TAG) ||
    types.includes(INSTANCE_DRAG_TAG) ||
    types.includes(MEMBER_DRAG_TAG)
  )
    return true;
  if (!types.includes("text/plain")) return false;

  try {
    const raw = data.getData("text/plain");
    return parseCrossInstancePayload(raw) !== null || parseCrossMemberPayload(raw) !== null;
  } catch {
    return false;
  }
}
