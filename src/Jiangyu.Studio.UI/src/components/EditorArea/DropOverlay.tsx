import { useState } from "react";
import styles from "./EditorArea.module.css";

export type DropZone = "left" | "right" | "top" | "bottom" | "centre";

interface DropOverlayProps {
  active: boolean;
  acceptedMimes: readonly string[];
  onDrop: (zone: DropZone, e: React.DragEvent) => void;
}

function zoneFor(x: number, y: number, w: number, h: number): DropZone {
  // Whichever edge is closest wins; the central 50% of the area is "centre".
  const EDGE_FRACTION = 0.25;
  const fx = x / w;
  const fy = y / h;
  const distL = fx;
  const distR = 1 - fx;
  const distT = fy;
  const distB = 1 - fy;
  const minH = Math.min(distL, distR);
  const minV = Math.min(distT, distB);
  if (minH > EDGE_FRACTION && minV > EDGE_FRACTION) return "centre";
  if (minH < minV) return distL < distR ? "left" : "right";
  return distT < distB ? "top" : "bottom";
}

function hasAcceptedMime(types: readonly string[], accepted: readonly string[]): boolean {
  for (const t of accepted) if (types.includes(t)) return true;
  return false;
}

export function DropOverlay({ active, acceptedMimes, onDrop }: DropOverlayProps) {
  const [zone, setZone] = useState<DropZone | null>(null);

  if (!active) return null;

  const handleDragOver = (e: React.DragEvent) => {
    if (!hasAcceptedMime(e.dataTransfer.types, acceptedMimes)) return;
    e.preventDefault();
    e.dataTransfer.dropEffect = "move";
    const rect = e.currentTarget.getBoundingClientRect();
    const next = zoneFor(e.clientX - rect.left, e.clientY - rect.top, rect.width, rect.height);
    if (next !== zone) setZone(next);
  };

  const handleDragLeave = (e: React.DragEvent) => {
    if (e.currentTarget.contains(e.relatedTarget as Node | null)) return;
    setZone(null);
  };

  const handleDrop = (e: React.DragEvent) => {
    e.preventDefault();
    const final = zone;
    setZone(null);
    if (final !== null) onDrop(final, e);
  };

  return (
    <div
      className={styles.dropOverlay}
      onDragOver={handleDragOver}
      onDragLeave={handleDragLeave}
      onDrop={handleDrop}
    >
      {zone !== null && <div className={`${styles.dropZone} ${styles[`dropZone_${zone}`]}`} />}
    </div>
  );
}
