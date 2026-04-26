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
  | "Composite";

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
  compositeFields?: Record<string, EditorValue>;
}

export type DirectiveOp = "Set" | "Append" | "Insert" | "Remove";

export interface EditorDirective {
  op: DirectiveOp;
  fieldPath: string;
  index?: number;
  value?: EditorValue;
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
