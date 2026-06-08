import type { UiNode } from "@features/bridge/bridge";

/** The Game.UI selector snippets for a node, most specific first: name, then type, then each class. */
export function selectorsOf(node: UiNode): string[] {
  const out: string[] = [];
  if (node.name) out.push(`UiSelector.Name("${node.name}")`);
  if (node.type) out.push(`UiSelector.TypeName("${node.type}")`);
  for (const c of node.classes ?? []) out.push(`UiSelector.Class("${c}")`);
  return out;
}

/** The single best selector to offer for a node (its name, else its type), or null. */
export function bestSelector(node: UiNode): string | null {
  return selectorsOf(node)[0] ?? null;
}

/** Whether the node's own type, name, text, or a class contains the query (case-insensitive). */
export function nodeMatches(node: UiNode, query: string): boolean {
  const q = query.toLowerCase();
  if (q === "") return true;
  if ((node.type ?? "").toLowerCase().includes(q)) return true;
  if ((node.name ?? "").toLowerCase().includes(q)) return true;
  if ((node.text ?? "").toLowerCase().includes(q)) return true;
  return (node.classes ?? []).some((c) => c.toLowerCase().includes(q));
}

/** Collapse whitespace and cap a node's text for inline display. */
export function truncate(text: string): string {
  const flat = text.replace(/\s+/g, " ").trim();
  return flat.length > 40 ? `${flat.slice(0, 40)}…` : flat;
}

/** One row of the style detail panel: the property, its display value, and a colour for the swatch. */
export interface StyleEntry {
  readonly key: string;
  readonly value: string;
  readonly color: string | null;
}

// The order to show known style keys in: layout state, geometry, paint, then the image bits.
// Keys the probe adds later that are not listed fall to the end, alphabetically.
const STYLE_ORDER = [
  "display",
  "opacity",
  "x",
  "y",
  "w",
  "h",
  "flexDirection",
  "bg",
  "color",
  "fontSize",
  "borderWidth",
  "borderColor",
  "bgImage",
  "bgTint",
  "slice",
];

/** A node's computed styles as ordered display rows, with a colour flagged for a swatch. */
export function styleEntries(node: UiNode): StyleEntry[] {
  const style = node.style;
  // The bridge may omit a null style entirely, so guard undefined as well as null.
  if (style == null) return [];
  const rank = (k: string): number => {
    const i = STYLE_ORDER.indexOf(k);
    return i === -1 ? STYLE_ORDER.length : i;
  };
  return Object.keys(style)
    .sort((a, b) => rank(a) - rank(b) || a.localeCompare(b))
    .map((key) => {
      const raw = style[key];
      const value = typeof raw === "string" ? raw : String(raw);
      const color = typeof raw === "string" && /^rgba?\(/i.test(raw) ? raw : null;
      return { key, value, color };
    });
}
