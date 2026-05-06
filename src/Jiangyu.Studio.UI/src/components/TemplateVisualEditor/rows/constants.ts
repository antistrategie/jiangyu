import type { DirectiveOp, EditorValueKind } from "../types";

export const SCALAR_OPS: DirectiveOp[] = ["Set"];
export const COLLECTION_OPS: DirectiveOp[] = ["Set", "Append", "Insert", "Remove", "Clear"];
// Named arrays are fixed-size enum-indexed; only per-slot Set makes sense.
export const NAMED_ARRAY_OPS: DirectiveOp[] = ["Set"];
export const OP_LABELS: Record<DirectiveOp, string> = {
  Set: "set",
  Append: "append",
  Insert: "insert",
  Remove: "remove",
  Clear: "clear",
};

/**
 * Display-only labels for value kinds in the row's right-side chip. The
 * canonical wire-format names (TemplateReference, etc.) are too long /
 * jargon-y for a tiny UI label; this mapping is a single source of truth so
 * any future renames stay in one place. Storage / serialisation are
 * unaffected.
 */
export const VALUE_KIND_LABELS: Record<EditorValueKind, string> = {
  Boolean: "Boolean",
  Byte: "Byte",
  Int32: "Int32",
  Single: "Single",
  String: "String",
  Enum: "Enum",
  TemplateReference: "Ref",
  Composite: "Composite",
  HandlerConstruction: "Handler",
  AssetReference: "Asset",
};
