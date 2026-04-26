import { isTemplateDragPayload } from "@lib/drag/templateDrag";

interface DragDataLike {
  readonly types: readonly string[];
  getData: (format: string) => string;
}

export function acceptsPaneDropDragData(data: DragDataLike, accepted: readonly string[]): boolean {
  const { types } = data;
  // Reject template-browser drags — they target the visual editor, not pane splits.
  if (isTemplateDragPayload(data)) return false;
  for (const t of accepted) if (types.includes(t)) return true;
  return false;
}
