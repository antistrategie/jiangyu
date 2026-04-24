export type DropZone = "left" | "right" | "top" | "bottom" | "centre";

export function zoneFor(x: number, y: number, w: number, h: number): DropZone {
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
