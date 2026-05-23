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
  | "AssetReference"
  // Explicit `#null` literal. Clears a scalar reference field; the
  // destination type is checked at apply time. The wire format carries
  // no payload other than the kind tag itself.
  | "Null";

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
  /** Optional prototype-source key for Composite values. When set, the
   *  applier looks up an existing element in the destination collection
   *  whose `name` property matches this string, deep-copies it, and applies
   *  compositeDirectives on the copy. Lets modders inherit Inspector-baked
   *  defaults without enumerating every field. */
  compositeFrom?: string;
  assetName?: string;
}

export type DirectiveOp = "Set" | "Append" | "Insert" | "Remove" | "Clear";

/** One descent step along a template patch path. Mirrors the KDL syntax
 *  `set "Field" index=N type="Subtype" { ... }` (or `set "Field" type="X"
 *  { ... }` for scalar polymorphic descent, where `index` is null). The
 *  validator switches to `subtype` when the destination is polymorphic.
 *  Mirrors the host `Jiangyu.Shared.Templates.TemplateDescentStep`. */
export interface DescentStep {
  field: string;
  /** Null = scalar polymorphic descent (no collection index). */
  index?: number | null;
  subtype?: string;
}

export interface EditorDirective {
  op: DirectiveOp;
  /** Inner-relative member path: a bare member name in the common case.
   *  Descent context (if any) lives in `descent`, never in this string. */
  fieldPath: string;
  index?: number;
  /** Multi-dimensional cell address for `Set` ops against an N-dim
   *  Odin-routed array (e.g. AOETiles' bool[,]). Mutually exclusive with
   *  `index` (which is for 1D collections). Mirrors the host
   *  `CompiledTemplateSetOperation.IndexPath`. */
  indexPath?: number[];
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
