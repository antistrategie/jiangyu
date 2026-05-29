# Event Handler Field Patching

Status: **verified** (Jiangyu live-game readback, 2026-05-01).

## Contract

Modders fully manage `SkillEventHandlerTemplate` / `PerkEventHandlerTemplate`
instances on a skill or perk via a parent-anchored authoring surface. Four
authoring shapes cover the lifecycle:

- **Edit** an existing handler via descent (`set "EventHandlers" index=N
  { ... }`, no `type=`): the live element at slot N is edited in place,
  sibling fields preserved. Its concrete subtype is inferred from the live
  element, so no annotation is needed.
- **Replace** an existing handler (`set "EventHandlers" index=N type="X"
  { ... }`): construct a fresh X and overwrite slot N. `type=` always builds
  a fresh instance.
- **Add** a freshly-constructed handler via append/insert
  (`append "EventHandlers" type="X" { ... }` and friends).
- **Remove** a handler outright via `remove "EventHandlers" index=N` or
  `clear "EventHandlers"`.

The applier resolves the parent template by `m_ID`, walks into the array,
and dispatches by op kind. For edit, it reads the live element's concrete
type, casts the indexed PPtr-resolved wrapper to it, and writes via
reflection. For replace and add, it allocates a fresh `ScriptableObject` of
the named subtype, populates fields on the concrete-typed wrapper, marks
`hideFlags = DontUnloadUnusedAsset`, and writes/pushes/inserts the PPtr in
the parent's list. For remove and clear, the existing collection-mutation
path operates without construction.

Authoring example covering edit and add:

```kdl
patch "PerkTemplate" "perk.unique_darby_high_value_targets" {
    // Edit: flip a scalar on the existing handler at index 0.
    set "EventHandlers" index=0 {
        set "ShowHUDText" #true
    }

    // Add: append a brand-new AddSkill handler.
    append "EventHandlers" type="AddSkill" {
        set "Event" enum="AddEvent" "OnAttack"
        set "SkillToAdd" ref="SkillTemplate" "effect.bleeding"
        set "ShowHUDText" #true
    }
}
```

The parent-anchored surface is required because:

- Handlers are not `DataTemplate` subclasses, so they have no `m_ID` and
  cannot be addressed via `DataTemplateLoader.TryGet<T>`. The only access
  path is through the parent skill.
- The array's declared element type is the abstract base
  (`SkillEventHandlerTemplate`); concrete fields like `ShowHUDText` live on
  the subclass (`AddSkill`). When constructing (`type=`), the modder names the
  subtype. When editing, the runtime reads the live element's concrete type so
  the Il2CppInterop wrapper exposes the subclass fields, and the compiler
  validates inner fields against the union of the base's concrete subtypes.

## Authoring

| Form | Meaning |
| --- | --- |
| `set "EventHandlers" index=N { set "field" v }` | **Edit** element N in place. The concrete subtype is inferred, sibling fields preserved. |
| `set "EventHandlers" index=N type="X" { set "field" v }` | **Replace** element N with a fresh X. Inner ops populate it, every other field defaults. |
| `append "EventHandlers" type="X" { set "field" v }` | **Add**: construct a new X handler and push it onto the list. |
| `insert "EventHandlers" index=N type="X" { set "field" v }` | **Add** at a specific position. |
| `remove "EventHandlers" index=N` | **Remove** element N. |
| `clear "EventHandlers"` | **Remove** all elements. |

`type=` is the single construction keyword and always builds a fresh
instance. Bracket indexers (`Foo[0].Bar`) are rejected in modder-authored
fieldPath strings; descent and construction blocks are the only way to
address an element. Inner directives in a construction block accept the full
`set` / `append` / `insert` / `remove` / `clear` vocabulary against the new
instance.

The compile-time validator errors loudly when:

- `type=` is missing on append/insert/replace into a polymorphic
  destination (omittable when monomorphic),
- the named subtype doesn't exist in the assembly,
- the named subtype isn't assignable to the array's element type,
- an inner field doesn't exist on the named subtype (construction) or on any
  concrete subtype of the element base (edit).

## Runtime steps

Implemented in `src/Jiangyu.Loader/Templates/TemplatePatchApplier.cs`:

### Descent (edit existing handler)

1. `TemplateRuntimeAccess.GetAllTemplates(parentTemplateType)` — forces the
   per-type cache to materialise. Empty result means the cache isn't ready;
   the scheduled apply coroutine retries on the next scene load.
2. `TemplateRuntimeAccess.TryGetTemplateById(parentType, m_ID)` — fetches
   the live parent skill or perk by its serialised id.
3. Walk `EventHandlers` to read the list, then index it via the existing
   `TryIndexInto` helper. The result is an Il2CppInterop wrapper of the
   abstract base type (e.g. `Il2CppMenace.Tactical.Skills.SkillEventHandlerTemplate`).
4. Read the live element's concrete class name via
   `IL2CPP.il2cpp_object_get_class` + `il2cpp_class_get_name`, resolve the
   wrapper type via `Il2CppSubtypeResolver`, and cast with
   `Il2CppReflectiveCast` (`TryCastToLiveConcreteType`). The cast succeeds
   because the underlying IL2CPP object is the concrete subclass; only the
   wrapper was reporting the base. Without it, reflection on the wrapper sees
   only the abstract base's own members (typically zero). The edit needs no
   `type=` because the live element supplies the subtype.
5. Reflection on the cast wrapper finds the named field on the subclass.
   Write via the existing scalar-set path; readback via the existing
   `ApplyAndVerify` helper confirms the write took.

### Construction (add new handler)

1. Resolve the named subtype via `TemplateRuntimeAccess.ResolveTemplateType`.
2. `UnityEngine.ScriptableObject.CreateInstance(Il2CppType.From(subtype))` —
   produces a fresh handler asset of the concrete subclass. Il2CppInterop
   returns the instance wrapped at the **base** type
   (`UnityEngine.ScriptableObject`), not the concrete subclass; same
   wrapper-narrowing problem as the descent path.
3. Cast the wrapper to the concrete subtype via the same
   `Il2CppObjectBase.Cast<T>` reflective invoke so reflection sees subclass
   fields.
4. Populate each authored inner field via `TryGetWritableMember` +
   `TryConvertScalar` on the concrete-typed wrapper.
5. Set `hideFlags = HideFlags.DontUnloadUnusedAsset` so scene-change GC
   doesn't sweep the freshly-allocated handler. Set `name` to the subtype
   name to match the vanilla naming convention (vanilla handlers each have
   `name == "AddSkill"` / `"ChangeProperty"` / etc., so inspector dumps
   show our constructed handlers consistently).
6. Route the constructed instance through the existing collection-mutation
   path, which handles append / insert at index / replace at index.

### Wire format

A `type="X"` construction compiles to a `CompiledTemplateValueKind.TypeConstruction`
value whose payload mirrors the `Composite` field-bag shape: a type name plus
the inner ops. On an indexed `set` the value carries `Index`, so the applier
overwrites that slot. The runtime applier dispatches on
`Kind == TypeConstruction` to take the ScriptableObject construction path; the
validator demotes value-type and tagged-string destinations to `Composite` at
compile so the loader builds them inline or packs the tagged string.

An edit descent compiles to a `TemplateDescentStep` carrying just the field and
the index, with no subtype. The absence of a constructed value on the op is what
tells the applier to read the live element's concrete type rather than
constructing a fresh one. Multi-level descent chains a step per boundary.

## Verification

Confirmed by Jiangyu against the live game, 2026-05-01.

### Descent

`WOMENACE/templates/perk-darby_high_value_targets-handler-smoke.kdl`
patched `PerkTemplate:perk.unique_darby_high_value_targets`'s
`EventHandlers[0].ShowHUDText` from `false` to `true` with a plain edit
descent, the applier inferring the live element's `AddSkill` type. Loader log:

```
Template patch 'PerkTemplate:perk.unique_darby_high_value_targets.EventHandlers[0].ShowHUDText'
    (mod 'WOMENACE'): set to True, readback matches.
```

Generalising to other handler subtypes is mechanical because the applier
path is type-agnostic; the same code reaches all 119+ documented handler
types.

### Construction

The same smoke also `append`-ed a brand-new `AddSkill` handler to the
perk's `EventHandlers` list, configured with `Event="OnAttack"`,
`SkillToAdd=ref="SkillTemplate" "effect.bleeding"`, and `ShowHUDText=#true`.
Loader log:

```
Template patch 'PerkTemplate:perk.unique_darby_high_value_targets.EventHandlers'
    (mod 'WOMENACE'): appended AddSkill, readback matches.
Applied 2 PerkTemplate patch op(s).
    [skipped: missingTemplate=0 missingMember=0 conversion=0]
```

Live runtime dump after apply showed Darby's perk with two
`EventHandlers`, distinct native pointers for the original and the
constructed handler. The constructed handler carries `name == "AddSkill"`
and the same `hideFlags == DontUnloadUnusedAsset` the clone applier uses,
keeping it alive across scene changes.

### Remove and clear

A separate (now-removed) `effect.bleeding`-clone smoke verified
`remove "EventHandlers" index=0` and `clear "EventHandlers"` against a
polymorphic-reference array on a cloned skill. Loader log:

```
Template patch 'SkillTemplate:effect.bleeding_remove_smoke.EventHandlers'
    (mod 'WOMENACE'): removed element at 0 from List`1.
Template patch 'SkillTemplate:effect.bleeding_clear_smoke.EventHandlers'
    (mod 'WOMENACE'): cleared List`1.
```

Live state confirmed: source kept its 4 handlers, the remove-smoke clone
had 3 (with deep-copied `(Clone)`-suffixed elements per the clone applier
contract), the clear-smoke clone had 0.

## Scope limits

- **Inner construction fields are flat.** Inside a `type="X" { ... }`
  block, each inner `set` configures one top-level field on the constructed
  handler. Indexed writes (`set "TargetTags" index=0 ...`) and descent
  (`set "Sub" index=0 type="Y" { ... }`) are not supported inside
  construction. To populate a constructed handler's list-typed field, use
  a follow-up patch with descent on the now-existing element.
- **Odin-routed fields stay out of scope.** Six of fifteen validated
  handler types carry an interface-typed field (`ITacticalCondition`,
  `IValueProvider`) whose data lives in the Odin `serializationData` blob.
  Patching those requires Odin payload write support that Jiangyu does not
  implement. The compile-time validator marks these fields as Odin-only
  and rejects writes; modders who need to touch them use the SDK code
  path.
- **Brand-new handler subclass types are SDK-only.** Concrete handler
  types are compiled IL2CPP classes; new types cannot be synthesised from
  data. The SDK roadmap covers `ClassInjector.RegisterTypeInIl2Cpp<T>` for
  custom handler classes; that's outside the data-only patching path.
- **Cloning a parent skill or perk deep-copies its handlers.** Verified
  in-game on 2026-05-01: `Object.Instantiate` shallow-copies PPtr lists,
  so without intervention the clone's `EventHandlers` would point at the
  source's handler assets and patches through the clone would leak into
  the source. `TemplateCloneApplier` runs an
  [owned-reference deep-copy pass](template-cloning.md#owned-reference-deep-copy)
  immediately after `Instantiate` that detects this shape (collection
  whose element type is an abstract-polymorphic non-DataTemplate
  `ScriptableObject`, e.g. `SkillEventHandlerTemplate`) and
  `Object.Instantiate`s each element so the clone owns its handlers.
  Clone-then-edit-handlers is therefore safe.
- **Handler index stability across game updates.** Mods author against a
  specific `EventHandlers[N]` slot. If MENACE patches reorder or
  insert/remove handlers in vanilla skills, mods may silently target a
  different handler. There's no defensive rebinding today; modders re-
  author after a game update if they hit a regression.

## Jiangyu Implementation

### Wire format
- `CompiledTemplateValueKind.TypeConstruction` in
  `src/Jiangyu.Shared/Templates/CompiledTemplatePatchManifest.cs`; the
  edit-descent step is `TemplateDescentStep` (field + index, no subtype) in the
  same folder.

### KDL grammar
- Descent block parsing:
  `src/Jiangyu.Core/Templates/KdlTemplateParser.cs:TryParseDescentBlock`.
- Construction value parsing (type= on append/insert/set):
  `src/Jiangyu.Core/Templates/KdlTemplateParser.cs:TryParseTypeConstructionValue`.
- Hard-cut on bracket indexers in modder-authored fieldPaths:
  `src/Jiangyu.Core/Templates/KdlTemplateParser.cs:TryParseOperation`.
- Round-trip serialisation (descent-block grouping + type= field-bag):
  `src/Jiangyu.Core/Templates/KdlTemplateSerialiser.cs`.

### Validation
- Compile-time subtype dispatch (descent navigation through polymorphic-
  abstract bases):
  `src/Jiangyu.Core/Templates/TemplateMemberQuery.cs:NavigateFieldPath`.
- Construction validation (subtype assignability + inner-field check):
  `src/Jiangyu.Core/Templates/TemplateCatalogValidator.cs:ValidateTypeConstruction`.

### Compile/loader plumbing
- Manifest emission preserving descent steps and construction values:
  `src/Jiangyu.Core/Compile/TemplatePatchEmitter.cs:TryEmitOperation`.
- Loader catalogue carrying descent steps:
  `src/Jiangyu.Loader/Templates/TemplatePatchCatalog.cs:LoadedPatchOperation`.

### Runtime
- Edit-descent cast to the live concrete subtype:
  `src/Jiangyu.Loader/Templates/TemplatePatchApplier.cs:TryCastToLiveConcreteType`,
  resolving the wrapper via `Il2CppSubtypeResolver`.
- Construction (CreateInstance + cast + populate + hideFlags + name):
  `src/Jiangyu.Loader/Templates/TemplatePatchApplier.cs:TryConstructPolymorphic`,
  reusing `TryConstructComposite`'s field-population path.

## Investigation notes

- `legacy/2026-04-15-skilleventhandlertemplate-structural-spot-check.md` —
  initial 5-type spot check of the polymorphic ScriptableObject reference
  array pattern under `SkillTemplate.EventHandlers`.
- `legacy/2026-04-15-broader-handler-survey.md` — broader survey covering
  15 of 119+ concrete handler types; Odin substitution pattern documented;
  zero structural mismatches.
- `legacy/2026-05-01-event-handler-patching-model.md` — patching model
  synthesis, identity-by-pathId finding, code-path audit, and the slice
  decomposition that produced this contract.
