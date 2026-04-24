import { useCallback } from "react";
import styles from "./Workspace.module.css";

export interface ResizeStart {
  readonly aPx: number;
  readonly bPx: number;
}

interface ResizeHandleProps {
  axis: "x" | "y";
  measure: () => ResizeStart | null;
  onResize: (deltaPx: number, start: ResizeStart) => void;
}

export function ResizeHandle({ axis, measure, onResize }: ResizeHandleProps) {
  const onMouseDown = useCallback(
    (e: React.MouseEvent) => {
      e.preventDefault();
      const start = measure();
      if (start === null) return;
      const startCoord = axis === "x" ? e.clientX : e.clientY;
      const cursor = axis === "x" ? "col-resize" : "row-resize";
      const previousBodyCursor = document.body.style.cursor;
      const previousUserSelect = document.body.style.userSelect;
      document.body.style.cursor = cursor;
      document.body.style.userSelect = "none";

      // Coalesce pixel-granular mousemoves into one rAF so we don't run
      // setLayout per pixel — saves a lot of work during active drags.
      let pendingDelta: number | null = null;
      let rafId: number | null = null;
      const flush = () => {
        rafId = null;
        if (pendingDelta === null) return;
        const delta = pendingDelta;
        pendingDelta = null;
        onResize(delta, start);
      };
      const onMove = (ev: MouseEvent) => {
        const cur = axis === "x" ? ev.clientX : ev.clientY;
        pendingDelta = cur - startCoord;
        rafId ??= requestAnimationFrame(flush);
      };
      const onUp = () => {
        document.removeEventListener("mousemove", onMove);
        document.removeEventListener("mouseup", onUp);
        if (rafId !== null) cancelAnimationFrame(rafId);
        flush();
        document.body.style.cursor = previousBodyCursor;
        document.body.style.userSelect = previousUserSelect;
      };
      document.addEventListener("mousemove", onMove);
      document.addEventListener("mouseup", onUp);
    },
    [axis, measure, onResize],
  );

  return (
    // separator is the correct ARIA role for a draggable resize handle; the
    // rule doesn't recognise the focusable variant of separator.
    // eslint-disable-next-line jsx-a11y/no-noninteractive-element-interactions
    <div
      className={axis === "x" ? styles.colResizer : styles.paneResizer}
      role="separator"
      aria-orientation={axis === "x" ? "vertical" : "horizontal"}
      aria-label="Resize panes"
      tabIndex={-1}
      onMouseDown={onMouseDown}
    />
  );
}
