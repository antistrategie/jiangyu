# Template Collection Operations

Status: **investigation** (Append already verified in-game on `WeaponTemplate.SkillsGranted` List and `UnitLeaderTemplate.PerkTrees` ref-array; InsertAt and Remove pending in-game confirmation via EntityPatchSmoke 2026-04-20 launch. Promote to `verified/template-collection-ops.md` after confirmation. Struct-array support is implemented but untested against a real target.)

## Contract

Each entry in `templatePatches[].set[]` carries an explicit `op`:

- `Set` *(default, pre-existing)*. Writes value at fieldPath. Terminal may be indexed (`Skills[3] = X`) or direct (`Description = "…"`).
- `Append`. Adds value as a new tail element on the named collection. FieldPath terminal must NOT be indexed.
- `InsertAt`. Inserts value at `index` in the named collection. FieldPath terminal must NOT be indexed; `index` is a required non-negative integer.
- `Remove`. Deletes the element identified by an indexed terminal fieldPath (`Skills[3]`). No value field.

```json
"set": [
  { "fieldPath": "HudYOffsetScale", "value": { "kind": "Single", "single": 5.0 } },
  { "op": "Append",   "fieldPath": "Skills",      "value": { "kind": "TemplateReference", "reference": {...} } },
  { "op": "InsertAt", "fieldPath": "Skills", "index": 0, "value": {...} },
  { "op": "Remove",   "fieldPath": "Skills[0]" }
]
```

## Compile-time validation

`TemplatePatchEmitter.ValidateOpShape` rejects op-shape mismatches at compile time (hand-authored mods get the same checks at load via `TemplatePatchCatalog`):

| Op | `index` field | Terminal `[N]` | `value` |
|---|---|---|---|
| Set | rejected | allowed | required |
| Append | rejected | rejected | required |
| InsertAt | required, `>= 0` | rejected | required |
| Remove | rejected | required | rejected |

## Runtime dispatch

Implemented in `src/Jiangyu.Loader/Templates/TemplatePatchApplier.cs`.

Collection shape detection (`TryGetCollectionShape`):
- Has instance `Add(T)` → **List** (covers `Il2CppSystem.Collections.Generic.List<T>` and any shape that duck-types).
- Full-name `Il2CppInterop.Runtime.InteropTypes.Arrays.Il2CppReferenceArray\`1` → **ReferenceArray**.
- Full-name `Il2CppInterop.Runtime.InteropTypes.Arrays.Il2CppStructArray\`1` → **StructArray**.

Per-shape mutations:
- **List**: `Add(value)` for Append, `Insert(index, value)` for InsertAt, `RemoveAt(index)` for Remove. Mutates in place; no field writeback on non-null.
- **Array** (ref or struct): rebuild a managed `T[]` at length ±1 with elements copied and the new one placed at target index, construct a fresh `Il2CppReferenceArray<T>` / `Il2CppStructArray<T>` via its managed-array ctor, then replace the parent's field via the typed property setter. The ctor iterates elements with correct GC write barriers; the field-setter uses `il2cpp_gc_wbarrier_set_field`.

## Null-init

If the terminal collection field is `null`:
- **List**: construct via parameterless ctor, writeback, then Add/Insert.
- **Array**: construct a 1-element array directly with the new value and writeback.
- Applies only to Append and InsertAt (the latter requires `index == 0` when collection is null/empty).
- Remove rejects null (nothing to remove).

## TemplateReference resolution

`TemplateRuntimeAccess.TryGetTemplateById` dispatches by managed base type:
- `DataTemplate` subtype → `DataTemplateLoader.TryGet<T>(m_ID)`. Sees both game-native templates and Jiangyu-registered clones.
- `ScriptableObject` subtype (e.g. `PerkTreeTemplate`) → `Resources.FindObjectsOfTypeAll` + `Object.name` match + `TryCast<T>()` to the resolved type (without the cast, storing into a typed managed array fails with "Object cannot be stored in an array of this type").

## Verification

**Verified in-game 2026-04-20:**

1. **List Append** on `WeaponTemplate.SkillsGranted`:
   `appended SkillTemplate 'active.aimed_shot', readback matches.`
2. **ReferenceArray Append** on `UnitLeaderTemplate.PerkTrees`:
   `appended PerkTreeTemplate 'perk_tree.greifinger', readback matches.`
3. **Pointer-based readback** (the `ReadbackMatches` switch) confirms per-wrapper-GC-handle false negatives are no longer surfacing.

**Pending in-game confirmation:**

4. InsertAt on `PerkTrees` at index 0 (EntityPatchSmoke deploys this alongside Append + Remove in a single launch).
5. Remove on `PerkTrees[0]`.
6. Struct-array rebuild for `Il2CppStructArray<T>` (no concrete target yet; code path untested against real data).
7. Null-init paths (no concrete target yet).

## Scope limits

- **Composite / construct-in-place values** are still out of scope. Appending a newly-constructed support-type (e.g. a new `Perk { Skill = X, Tier = 3 }`) requires Il2Cpp object construction from C# — a separate slice with its own factory investigation. Today's append primitive only takes references to existing live objects (via `TemplateReference`) or scalar values.
- **Cross-template cascade cloning** not supported. If an appended reference belongs to a template that also needs cloning, the modder writes both operations explicitly.
- **Value-type elements inside collections** can't currently be mutated in place (pre-existing limitation in the intermediate-segment walk). Set on `Skills[0]` for a value-type element is rejected; ref-type elements replace fine.

## Jiangyu Implementation

- Schema: `CompiledTemplateOp`, `CompiledTemplateSetOperation.Index` in
  `src/Jiangyu.Shared/Templates/CompiledTemplatePatchManifest.cs`.
- Compile-time validation: `TemplatePatchEmitter.ValidateOpShape` in
  `src/Jiangyu.Core/Compile/TemplatePatchEmitter.cs`.
- Shared path validator: `TemplatePatchPathValidator.TerminalSegmentIsIndexed`
  in `src/Jiangyu.Shared/Templates/TemplatePatchPathValidator.cs`.
- Runtime mutation binders: `TryBindCollectionMutation`, `BindListMutation`,
  `BindArrayMutation`, `TryApplyRemove` in
  `src/Jiangyu.Loader/Templates/TemplatePatchApplier.cs`.
- Identity resolution: `TemplateRuntimeAccess.TryGetTemplateById` in
  `src/Jiangyu.Loader/Templates/TemplateRuntimeAccess.cs`.
