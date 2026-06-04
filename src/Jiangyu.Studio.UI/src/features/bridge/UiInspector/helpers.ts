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
