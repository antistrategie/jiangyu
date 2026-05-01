# Event Handler Patching Model

Date: 2026-05-01

## Goal

Decide how Jiangyu should let modders edit, add, and remove
`SkillEventHandlerTemplate` / `PerkEventHandlerTemplate` instances. This
synthesises the prior structural-validation work (`2026-04-15-skilleventhandlertemplate-structural-spot-check.md`,
`2026-04-15-broader-handler-survey.md`, verified `polymorphic-reference-arrays.md`)
with a code audit of the current patcher, applier, catalogue, and clone path,
and identifies what is already in place vs. what is missing.

## Context

Past surveys validated the structural model for the handler family: 15 of 119+
concrete handler subclass types inspected, polymorphic ScriptableObject
reference array pattern confirmed, Odin substitution pattern documented for
interface-typed fields. That work is solid. What was not yet decided is the
*modder-facing* surface: how a `jiangyu.json` patch addresses an individual
handler, what authoring operations the KDL grammar should accept, and where the
existing infrastructure already covers it.

## Findings

### 1. Identity is `(collection, pathId)`, not `m_ID`

Concrete handler subclasses inherit from `SerializedScriptableObject`, which
inherits from `ScriptableObject`, not from `Menace.Tools.DataTemplate`. They
are not registered in `DataTemplateLoader.GetSingleton().m_TemplateMaps`, do
not carry `m_ID`, and cannot be reached via `TryGet<T>(id)`.

Confirmed against the on-disk index (`/home/justin/.local/share/jiangyu/cache/template-index.json`):

```
{"className": "AddSkill", "count": 228, "classifiedVia": "inheritance", "templateAncestor": "SkillEventHandlerTemplate"}
```

```
{"name": "AddSkill", "className": "AddSkill",
 "identity": {"collection": "resources.assets", "pathId": 106719}, ...}
```

Every handler instance carries an `identity` object with `(collection,
pathId)`. No instance carries `m_ID`. The index already classifies handler
types via the `inheritance` path (climbing to the `SkillEventHandlerTemplate`
ancestor), distinct from the `suffix` path used for `*Template` types.

The runtime patch applier
(`src/Jiangyu.Loader/Templates/TemplatePatchApplier.cs:105`) addresses
templates by `m_ID`. **It cannot reach a handler instance directly.** Handlers
are reachable only by walking from a parent skill: load `SkillTemplate` by
`m_ID`, navigate `EventHandlers`, dereference the PPtr at the chosen index.

This decides the addressing model: **handler patches must be authored against
their parent skill, not as standalone templates.**

### 2. Path syntax already accepts handler-shaped expressions

`TemplatePatchPathValidator` accepts dotted paths with `[N]` indexers including
nested forms like `Skills[0].DamageBonus` and `a.b[0].c.d[12].e`
(`tests/Jiangyu.Loader.Tests/TemplatePatchPathValidatorTests.cs:17-21`). A
path like `EventHandlers[2].DamageMult` is structurally valid today.

The applier (`TemplatePatchApplier.cs:156-216`) walks intermediate segments
including indexed ones. For a reference-type element (which a PPtr-resolved
handler is), the comment at line 206-208 reads: *"Indexed elements can't be
written back into the collection on this slice, but mutations on a
reference-type element propagate naturally, so we continue without a chain
entry."*

In other words: setting a scalar field on an existing handler should already
work end-to-end via the existing applier. **Verified in code; not yet
verified in-game.**

### 3. Catalogue already classifies handlers as polymorphic reference targets

`TemplateTypeCatalog`
(`src/Jiangyu.Core/Templates/TemplateTypeCatalog.cs:274`) treats anything
descending from `ScriptableObject` or `DataTemplate` as a template reference
target. `SkillEventHandlerTemplate` and its concrete subclasses pass that
test. `HasReferenceSubtype(baseType)` (line 263) returns true for
`SkillEventHandlerTemplate` because the assembly carries 119+ strict
descendants that are reference targets.

The Studio-side polymorphism heuristic
(`src/Jiangyu.Studio.Host/RpcDispatcher.Templates.cs:377-400,
`ComputeReferenceTypeIsPolymorphic`) refines the structural check by also
requiring the leaf type to have **zero direct instances in the index**, which
is exactly the case for the abstract `SkillEventHandlerTemplate` base. So the
catalogue already flags `EventHandlers` as polymorphic for editor consumption.

Two gaps remain on the catalogue side:

- **No subtype enumeration method.** `HasReferenceSubtype` is a boolean. The
  Studio editor needs an enumerate-subtypes call to populate the
  "pick a concrete handler type" combobox. Suggested addition:
  `IReadOnlyList<Type> GetReferenceSubtypes(Type baseType)` plus an RPC handler
  + `[RpcType]` DTO.
- **No exposed prototype hint.** When the modder picks a subtype, the editor
  should know what fields that subtype carries — already covered by the
  existing `templatesQuery` flow once a concrete type is named, so this is
  free.

### 4. The existing template clone path does not cover handlers

Verified clone (`docs/research/verified/template-cloning.md`) is hard-wired
to `DataTemplate`-derived registry semantics: it walks the IL2CPP class
hierarchy to find `m_ID`, mutates that field on the `Instantiate` copy, and
inserts into `m_TemplateMaps[type][cloneId]` plus its ancestor mirrors. None
of that applies to handlers, which have no `m_ID` and no `m_TemplateMaps`
slot.

A new handler instance therefore needs a different runtime construction path:

1. `UnityEngine.Object.Instantiate(sourceHandler)` to deep-copy a prototype
   handler of the desired concrete type (Unity's Instantiate copies serialised
   data within the asset). The source can be a vanilla handler from any
   skill — content doesn't matter; only the type does.
2. Optionally rewrite scalar fields on the new instance via the same
   reflection path the patcher already uses.
3. Append the new PPtr to the parent skill's `EventHandlers` list. The
   applier already supports `Append` / `InsertAt` operations on collections;
   the question is whether `TryBindCollectionMutation` can append into an
   `Il2CppSystem.Collections.Generic.List<SkillEventHandlerTemplate>` whose
   element is a managed-side ScriptableObject reference. Not yet
   verified.

`m_TemplateMaps` does not need to be touched. The PPtr in the parent skill's
list is the only reference path the runtime uses.

### 5. The PPtr-shallow-copy hazard for parent-skill clones

When `TemplateCloneApplier` clones a skill (`Instantiate` on the `Cast<UnityEngine.Object>()`),
the new skill's `EventHandlers` list is populated with the **same PPtrs** as
the source. Mutating `clonedSkill.EventHandlers[2].DamageMult` would mutate
the *shared* handler asset, affecting the original skill too.

This is a property of `UnityEngine.Object.Instantiate`: it deep-copies the
direct asset's serialised data (including the PPtr *list*) but does not follow
the PPtrs to clone what they reference. The verified clone doc does not
address this case because no validated mod has yet patched a cloned skill's
handlers.

**Status:** unverified hazard. Listed here so it gets validated before the
authoring grammar promises "clone a skill, edit its handlers" as a
load-bearing path.

The fix, when needed, is: when cloning a skill, also `Instantiate` each
handler PPtr in the source's list and replace the entries in the clone with
references to the new copies. This can be a flag on the clone directive
(`deepCopyHandlers: true` or similar) rather than a default, since most
clones probably want to share handlers with the source.

### 6. Odin-routed handler fields stay out of scope

Six of fifteen validated handler types carry an interface-typed field
(`ITacticalCondition` or `IValueProvider`) whose data lives in the
Odin-serialised `serializationData` blob, not in the Unity-native member
tree. Examples: `Attack.DamageFilterCondition`, `AddSkill.Condition`,
`ChangeProperty.ValueProvider`.

The current patcher cannot read or write these fields because the modder-facing
member tree (built by `TemplateTypeCatalog.GetMembers`) excludes them, and
even if exposed, writing them would require Odin payload serialisation that
Jiangyu does not implement.

**Decision:** these fields are explicitly out of scope for the data-only
patching path. The KDL surface should reject path expressions targeting
Odin-routed members with a clear error (the catalogue already marks them
`IsLikelyOdinOnly` per `TemplateTypeCatalog.cs:446`). Modders who need to
modify them must use the SDK code path.

### 7. Brand-new handler types are SDK-only

Concrete handler types are compiled IL2CPP classes. New types cannot be
synthesised from JSON or KDL — the behaviour is in compiled methods, not
data. A modder who wants a genuinely new handler type (`OnDoubleKillHandler`,
say) must:

1. Write a managed C# class extending `SkillEventHandlerTemplate` (or a
   suitable concrete base) with the appropriate virtual methods.
2. Register the type with Il2CppInterop via
   `Il2CppInterop.Runtime.Injection.ClassInjector.RegisterTypeInIl2Cpp<T>()`
   at mod load time.
3. Construct an instance via `ScriptableObject.CreateInstance<T>()` and
   append it to the target skill's `EventHandlers` list at runtime.

Two unverified questions for that path:

- **Dispatch shape.** Does the game's runtime invoke handlers via virtual
  method override (in which case `ClassInjector` is sufficient) or via a
  switch / factory keyed on a hardcoded type list (in which case the factory
  must be Harmony-patched to recognise the new type)? Resolving this requires
  reading the dispatcher in IL2CPP. Likely answer is virtual based on the
  per-subclass method tables observed in MAP's analysis, but not verified.
- **Persistence.** A custom handler type appended to a vanilla skill's list
  would need to survive scene reloads and saves. The clone path's session
  re-registration solves this for clones; an analogous re-attach pass would
  be needed for SDK-injected handlers.

This is squarely SDK-roadmap territory and is outside the scope of the
data-only patching slice covered by this note.

## Status against principles

`docs/PRINCIPLES.md` strict-correctness rule applies once code depends on a
finding. Of the seven findings above:

| # | Finding | Foundation-critical? | Status |
|---|---|---|---|
| 1 | Identity is pathId, not m_ID | Yes | Verified via on-disk index + structural surveys |
| 2 | Path syntax accepts handler paths | Yes | Verified via tests; live applier path not yet smoke-tested |
| 3 | Catalogue classifies handlers polymorphic | No (editor-only) | Verified via code read |
| 4 | Clone path does not cover handlers | Yes | Verified via code read of clone applier |
| 5 | Clone-of-parent shares handler PPtrs | Yes (if used) | **Unverified** — needs in-game smoke test before anyone ships a "clone skill, edit handlers" mod |
| 6 | Odin-routed fields out of scope | Yes | Verified structurally; documented as a deliberate gap |
| 7 | New handler types are SDK-only | Roadmap | Documented; no contract claim |

Findings 1, 4, and 6 are ready for promotion to `docs/research/verified/` with
a small write-up. Finding 2 needs a live smoke run before promotion.
Finding 5 needs a live smoke run before any mod claims it works.

## Recommended next steps

Ordered by foundation-criticality, narrowest vertical first.

### Slice 1: scalar set on existing handlers, no new code

Goal: prove the existing applier reaches handler fields via path expressions.

1. Pick a vanilla skill with a concrete `Attack` handler (e.g.
   `active.fire_assault_rifle_tier1_556`, handler index 0 per the broader
   survey).
2. Author a smoke mod with one patch:
   `set "EventHandlers[0].DamageMult" 1.5` against that skill.
3. Run the mod against the live game, log readback, confirm the handler's
   field changed and the skill's behaviour reflects it.
4. If it works: promote finding 2 to verified, document this as the working
   contract for handler scalar edits. If it doesn't: file a follow-up
   investigation; the gap is in the applier's reference-type element walk.

This is the cheapest validation that unlocks the entire scalar-edit surface
for all 119+ handler types simultaneously, since the applier code path is
generic.

### Slice 2: PPtr-shallow-copy hazard

Goal: decide whether clone-then-edit-handlers is safe.

1. Clone a vanilla skill via the existing template-clones path.
2. Patch a handler scalar on the clone.
3. Read back both the clone's handler and the source's handler. If they show
   the same value, finding 5 is confirmed and the clone path needs a
   `deepCopyHandlers` extension before that authoring shape is documented as
   safe.
4. Promote finding 5 to verified either way (the result decides the
   contract).

### Slice 3: subtype enumeration for the editor

Goal: let Studio offer a "pick a concrete handler type" combobox.

1. Add `IReadOnlyList<Type> GetReferenceSubtypes(Type baseType)` to
   `TemplateTypeCatalog`.
2. Add an RPC handler in `RpcDispatcher.Templates.cs` exposing it; emit a
   `[RpcType]` DTO listing each subtype's friendly name and member count.
3. Wire the editor's `TemplateVisualEditor` to surface the picker for any
   field flagged `IsReferenceTypePolymorphic`.

No runtime impact; pure editor ergonomics. Can run in parallel with slices 1
and 2.

### Slice 4: append a handler instance

Goal: let a modder add a new event handler to a vanilla skill.

This is the largest slice and probably the highest-value one. Order:

1. Decide the authoring grammar. Strawman:
   ```kdl
   patch "SkillTemplate" id="active.foo" {
       append "EventHandlers" handler="AddSkill" {
           set "Event" "OnHit"
           set "SkillToAdd" "Burning"
           set "OnlyApplyOnHit" true
       }
   }
   ```
2. Compile-time: `TemplatePatchEmitter` validates that the handler subtype
   exists, that all `set` ops target real writable fields on that subtype, and
   that no `set` targets an Odin-routed field.
3. Runtime: applier resolves a prototype handler of the named subtype (any
   vanilla instance will do; the index gives us thousands of candidates),
   `Instantiate`s it, applies the inline `set` ops, and appends the PPtr to
   the target skill's `EventHandlers` list.
4. Validate against a live mission. Promote to verified.

### Slice 5 (deferred): SDK path for new handler types

Out of scope for this investigation. Picks up after the data-only path is
shipped and the SDK roadmap moves into focus.

## Open questions

- The applier's `TryBindCollectionMutation` for an
  `Il2CppSystem.Collections.Generic.List<SkillEventHandlerTemplate>` —
  unverified whether the existing wrapper indexer + Append support handles
  PPtr-element lists. Slice 4 will surface this.
- `PerkTemplate.EventHandlers` shares the same field type as
  `SkillTemplate.EventHandlers`. The verified polymorphic-reference-arrays
  contract already covers both. No additional structural work needed for
  perks; the patching surface should fall out of the same slices.
- Stability of handler index across game updates. If MENACE patches add or
  reorder handlers in a vanilla skill, mods authored against
  `EventHandlers[2]` may silently target the wrong handler. Mitigations:
  a stricter selector (subtype-name plus same-subtype-index) or a per-skill
  declared map, both of which are addressable in the authoring grammar
  later. Ignore for now — `[N]` is the simplest shape and the modder can
  always re-author after a game update if the layout shifts.
