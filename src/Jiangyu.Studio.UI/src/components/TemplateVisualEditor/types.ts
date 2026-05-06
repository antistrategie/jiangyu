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
  | "HandlerConstruction"
  // Unity asset reference: a single name string the loader resolves
  // against the mod-bundle catalog (assets/additions/<category>/<name>) or
  // the live game-asset registry. The category is derived from the
  // destination field's declared Unity type, so the editor only stores
  // the name on assetName.
  | "AssetReference";

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
  assetName?: string;
}

export type DirectiveOp = "Set" | "Append" | "Insert" | "Remove" | "Clear";

/** One descent step along a template patch path. Mirrors the KDL syntax
 *  `set "Field" index=N type="Subtype" { ... }`: navigate into element
 *  `index` of collection `field`, switching the validated type to `subtype`
 *  when the destination is polymorphic. Mirrors the host
 *  `Jiangyu.Shared.Templates.TemplateDescentStep`. */
export interface DescentStep {
  field: string;
  index: number;
  subtype?: string;
}

export interface EditorDirective {
  op: DirectiveOp;
  /** Inner-relative member path: a bare member name in the common case.
   *  Descent context (if any) lives in `descent`, never in this string. */
  fieldPath: string;
  index?: number;
  value?: EditorValue;
  /** Outer descent prefix as a structural step list. Mirrors
   *  KdlEditorDirective.Descent on the host. The serialiser groups
   *  consecutive directives sharing the same descent prefix back into one
   *  outer `set "Field" index=N type="X" { ... }` block. */
  descent?: DescentStep[];
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
