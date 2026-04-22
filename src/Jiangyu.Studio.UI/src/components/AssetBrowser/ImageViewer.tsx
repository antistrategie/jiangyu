import { useCallback, useEffect, useRef, useState } from "react";
import { clampZoom, zoomTowardsCursor } from "../../lib/zoomMath.ts";
import styles from "./AssetBrowser.module.css";

interface Props {
  src: string;
  alt: string;
}

const MIN_ZOOM = 0.1;
const MAX_ZOOM = 32;
const ZOOM_STEP = 1.15;

/** Read the current fitted position from the DOM layout. */
function readFittedState(container: HTMLDivElement) {
  const img = container.querySelector("img");
  if (!img || img.naturalWidth === 0) return null;
  const cRect = container.getBoundingClientRect();
  const iRect = img.getBoundingClientRect();
  const fitScale = iRect.width / img.naturalWidth;
  const ox = iRect.left - cRect.left;
  const oy = iRect.top - cRect.top;
  return { fitScale, ox, oy };
}

export function ImageViewer({ src, alt }: Props) {
  const containerRef = useRef<HTMLDivElement>(null);
  const [zoom, setZoom] = useState(1);
  const [offset, setOffset] = useState({ x: 0, y: 0 });
  const [fitted, setFitted] = useState(true);

  // Reset when image changes.
  useEffect(() => {
    setZoom(1);
    setOffset({ x: 0, y: 0 });
    setFitted(true);
  }, [src]);

  const handleWheel = useCallback((e: React.WheelEvent) => {
    e.preventDefault();
    const container = containerRef.current;
    if (!container) return;

    const rect = container.getBoundingClientRect();
    const cursorX = e.clientX - rect.left;
    const cursorY = e.clientY - rect.top;

    setFitted((wasFitted) => {
      if (wasFitted) {
        const fs = readFittedState(container);
        if (!fs) return false;
        const factor = e.deltaY < 0 ? ZOOM_STEP : 1 / ZOOM_STEP;
        const next = clampZoom(fs.fitScale * factor, MIN_ZOOM, MAX_ZOOM);
        setZoom(next);
        setOffset({
          x: zoomTowardsCursor(cursorX, fs.ox, fs.fitScale, next),
          y: zoomTowardsCursor(cursorY, fs.oy, fs.fitScale, next),
        });
      } else {
        setZoom((prev) => {
          const factor = e.deltaY < 0 ? ZOOM_STEP : 1 / ZOOM_STEP;
          const next = clampZoom(prev * factor, MIN_ZOOM, MAX_ZOOM);
          setOffset((o) => ({
            x: zoomTowardsCursor(cursorX, o.x, prev, next),
            y: zoomTowardsCursor(cursorY, o.y, prev, next),
          }));
          return next;
        });
      }
      return false;
    });
  }, []);

  const handleMouseDown = useCallback(
    (e: React.MouseEvent) => {
      if (e.button !== 0) return;
      e.preventDefault();
      const container = containerRef.current;
      if (!container) return;
      const startX = e.clientX;
      const startY = e.clientY;

      // Snapshot the visual position before switching away from fitted.
      let startOffset: { x: number; y: number };
      let startZoom: number;
      if (fitted) {
        const fs = readFittedState(container);
        if (fs) {
          startOffset = { x: fs.ox, y: fs.oy };
          startZoom = fs.fitScale;
        } else {
          startOffset = { x: 0, y: 0 };
          startZoom = 1;
        }
        setZoom(startZoom);
        setOffset(startOffset);
        setFitted(false);
      } else {
        startOffset = { ...offset };
        startZoom = zoom;
      }

      const prevCursor = document.body.style.cursor;
      const prevSelect = document.body.style.userSelect;
      document.body.style.cursor = "grabbing";
      document.body.style.userSelect = "none";

      const onMove = (ev: MouseEvent) => {
        setOffset({
          x: startOffset.x + (ev.clientX - startX),
          y: startOffset.y + (ev.clientY - startY),
        });
      };
      const onUp = () => {
        document.removeEventListener("mousemove", onMove);
        document.removeEventListener("mouseup", onUp);
        document.body.style.cursor = prevCursor;
        document.body.style.userSelect = prevSelect;
      };
      document.addEventListener("mousemove", onMove);
      document.addEventListener("mouseup", onUp);
    },
    [offset, zoom, fitted],
  );

  const handleDoubleClick = useCallback(() => {
    setZoom(1);
    setOffset({ x: 0, y: 0 });
    setFitted(true);
  }, []);

  return (
    <div
      ref={containerRef}
      className={styles.imageViewer}
      onWheel={handleWheel}
      onMouseDown={handleMouseDown}
      onDoubleClick={handleDoubleClick}
    >
      <img
        className={fitted ? styles.imageViewerFitted : styles.imageViewerImg}
        src={src}
        alt={alt}
        draggable={false}
        style={
          fitted
            ? undefined
            : { transform: `translate(${offset.x}px, ${offset.y}px) scale(${zoom})` }
        }
      />
      {!fitted && <span className={styles.imageViewerZoom}>{Math.round(zoom * 100)}%</span>}
    </div>
  );
}
