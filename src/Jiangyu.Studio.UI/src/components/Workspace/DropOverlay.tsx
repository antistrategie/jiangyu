import { useState } from "react";
import { zoneFor, type DropZone } from "./dropZone.ts";
import styles from "./Workspace.module.css";

interface DropOverlayProps {
  active: boolean;
  acceptedMimes: readonly string[];
  onDrop: (zone: DropZone, e: React.DragEvent) => void;
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
