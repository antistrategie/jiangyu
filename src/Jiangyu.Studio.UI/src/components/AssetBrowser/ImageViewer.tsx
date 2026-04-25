import { useCallback, useEffect, useRef, useState } from "react";
import { clampZoom, zoomTowardsCursor } from "@lib/ui/zoomMath.ts";
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
  const [prevSrc, setPrevSrc] = useState(src);
  if (prevSrc !== src) {
    setPrevSrc(src);
    setZoom(1);
    setOffset({ x: 0, y: 0 });
    setFitted(true);
  }

  // Native non-passive wheel listener so preventDefault actually blocks the
  // browser's default Ctrl-wheel behaviour (page zoom). React's synthetic
  // onWheel is passive and calling e.preventDefault() there is a no-op.
  useEffect(() => {
    const container = containerRef.current;
    if (!container) return;

    const onWheel = (e: WheelEvent) => {
      // Hold Ctrl/Cmd to zoom; plain wheel bubbles so the surrounding
      // scroll container can scroll even when the viewer fills the viewport.
      if (!e.ctrlKey && !e.metaKey) return;
      e.preventDefault();

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
    };

    container.addEventListener("wheel", onWheel, { passive: false });
    return () => container.removeEventListener("wheel", onWheel);
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
      if (fitted) {
        const fs = readFittedState(container);
        const startZoom = fs ? fs.fitScale : 1;
        startOffset = fs ? { x: fs.ox, y: fs.oy } : { x: 0, y: 0 };
        setZoom(startZoom);
        setOffset(startOffset);
        setFitted(false);
      } else {
        startOffset = { ...offset };
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
    [offset, fitted],
  );

  const handleDoubleClick = useCallback(() => {
    setZoom(1);
    setOffset({ x: 0, y: 0 });
    setFitted(true);
  }, []);

  // application role intentionally takes keyboard focus over to the embedded
  // viewer; pan/double-click reset are pointer-only.
  return (
    // eslint-disable-next-line jsx-a11y/no-noninteractive-element-interactions
    <div
      ref={containerRef}
      className={styles.imageViewer}
      role="application"
      aria-label={`${alt} — pannable, double-click to reset zoom`}
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
      <div className={styles.viewerHint}>
        <span className={styles.viewerHintAction}>Pan</span>
        <span className={styles.viewerHintControl}>click + drag</span>
        <span className={styles.viewerHintAction}>Zoom</span>
        <span className={styles.viewerHintControl}>ctrl + wheel</span>
        <span className={styles.viewerHintAction}>Fit</span>
        <span className={styles.viewerHintControl}>double-click</span>
      </div>
    </div>
  );
}
