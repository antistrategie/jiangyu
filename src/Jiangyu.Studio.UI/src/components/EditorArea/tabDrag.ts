export const TAB_DRAG_MIME = "application/x-jiangyu-tab";
export const PANE_DRAG_MIME = "application/x-jiangyu-pane";

export interface TabDragPayload {
  readonly fromPaneId: string;
  readonly path: string;
}

export interface PaneDragPayload {
  readonly paneId: string;
}
