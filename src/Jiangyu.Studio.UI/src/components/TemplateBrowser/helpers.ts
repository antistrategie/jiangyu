// Pure helpers extracted from TemplateBrowser.tsx so the JSX module only
// exports React components — keeps Vite fast-refresh working and gives the
// unit tests a stable, side-effect-free import surface.

/**
 * Looks up the enum member name for a numeric leaf value. Returns null when
 * the value isn't a finite integer or isn't a defined member of the enum;
 * callers fall back to displaying the raw value so unusual values stay
 * visible rather than disappearing into "?".
 */
export function resolveEnumLeafLabel(
  rawValue: unknown,
  labelMap: Record<number, string> | null,
): string | null {
  if (!labelMap) return null;
  if (typeof rawValue !== "number" || !Number.isFinite(rawValue)) return null;
  return labelMap[rawValue] ?? null;
}
