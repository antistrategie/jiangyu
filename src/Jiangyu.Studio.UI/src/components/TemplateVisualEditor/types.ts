// VisualNode model — mirrors the KdlEditorDocument shape from the host RPC.
// Parse and serialise happen server-side; the client operates on this model.

export type EditorValueKind =
  | "Boolean"
  | "Byte"
  | "Int32"
  | "Single"
  | "String"
  | "Enum"
  | "TemplateReference"
  | "Composite"
  // ScriptableObject construction for a polymorphic-reference array element
  // (e.g. EventHandlers). Same shape as Composite — compositeType names the
  // concrete subtype, compositeDirectives hold the patch operations applied
  // to the freshly-constructed instance.
  | "HandlerConstruction";

export interface EditorValue {
  kind: EditorValueKind;
  boolean?: boolean;
  int32?: number;
  single?: number;
  string?: string;
  enumType?: string;
  enumValue?: string;
  referenceType?: string;
  referenceId?: string;
  compositeType?: string;
  /** Patch operations applied to the constructed composite/handler instance.
   *  Mirrors the outer EditorDirective shape — every op (Set/Append/Insert/
   *  Remove/Clear) is allowed inside, with nested composite/handler values
   *  authored the same way as outer directives. */
  compositeDirectives?: EditorDirective[];
}

export type DirectiveOp = "Set" | "Append" | "Insert" | "Remove" | "Clear";

export interface EditorDirective {
  op: DirectiveOp;
  fieldPath: string;
  index?: number;
  value?: EditorValue;
  /** Concrete subtype names for polymorphic-abstract descent points along
   *  fieldPath. Mirrors KdlEditorDirective.SubtypeHints on the host: keys
   *  are 0-based segment indices, values are concrete subtype short names. */
  subtypeHints?: Record<number, string>;
  /** UI-only stable identity for drag/reorder. Not serialised. */
  _uiId?: string;
}

export type EditorNodeKind = "Patch" | "Clone";

export interface EditorNode {
  kind: EditorNodeKind;
  templateType: string;
  templateId?: string;
  sourceId?: string;
  cloneId?: string;
  directives: EditorDirective[];
  /** UI-only stable identity for drag/reorder. Not serialised. */
  _uiId?: string;
}

export interface EditorError {
  message: string;
  line?: number;
}

export interface EditorDocument {
  nodes: EditorNode[];
  errors: EditorError[];
}
