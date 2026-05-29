# Tagged-string authoring

Status: **verified** (in-game smoke test, voymastina cloned bark plays
with full structured-nodes authoring).

KDL composites against MENACE's `m_Ser*`-named string fields pack to
`"DISCRIMINATOR|{json}"` storage at apply time. The authoring shape
uses the existing 5 ops plus `type=` and `index=`. No new
directives.

## The pattern

MENACE stores certain polymorphic typed values as tagged strings inside
a `string` or `List<string>` field, with a sibling typed field that
`OnAfterDeserialize` rebuilds. Unity's serialiser can't natively express
polymorphic trees, so the game serialises them through this string-tag
convention.

Three sites in MENACE today:

| Tagged field | Sibling typed field | Polymorphic base |
| --- | --- | --- |
| `ConversationNodeContainer.m_SerializedNodes : List<string>` | `m_Nodes : List<BaseConversationNode>` | `BaseConversationNode` |
| `Role.m_SerializedRequirements : List<string>` | `Requirements : List<BaseRoleRequirement>` | `BaseRoleRequirement` |
| `ActionConversationNode.m_SerAction : string` | `m_Action : BaseConversationNodeAction` | `BaseConversationNodeAction` |

Detection is structural. `TemplateTypeCatalog.EnrichMembers` pairs
fields named `m_Ser*` or `m_Serialized*` with a sibling that holds the
typed equivalent, and marks the string-side field with
`TaggedPolymorphicBase` set to the polymorphic base type. The same
detection covers any future site that follows the convention.

## Authoring shape

Modder writes a normal `type="X"` op against the tagged-string
field. The `X` is the discriminator from the asset's stored
`"X|{json}"` form, not the CLR class name.

```kdl
clone "ConversationTemplate" from="JeanSy/click_bark" id="Voymastina/click_bark" {
    set "Roles" index=0 {
        set "m_SerializedRequirements" index=2 type="HasOneTag" {
            set "Tags" "voymastina"
        }
    }
    set "Nodes" {
        append "m_SerializedNodes" type="ACTION" {
            set "m_SerAction" type="SetFlag" {
                set "FlagName" "click_bark_voymastina_test"
                set "FlagValue" #true
            }
        }
        append "m_SerializedNodes" type="VARIATION" {
            append "Variations" {
                append "m_SerializedNodes" type="SAY" {
                    set "Sound" {
                        set "bankId" "tactical_barks_voymastina_va"
                        set "itemId" "voymastina_click_bark_test"
                    }
                    set "RoleGuid" "Entity"
                    set "Text" "TEST BARK"
                }
            }
        }
        append "m_SerializedNodes" type="EMPTY" {}
    }
}
```

The raw string form
(`set "F" "TYPE|{\"...\":\"...\"}"`) still works as an escape hatch
when the modder wants to paste a JSON blob from a decompiled asset, or
references a discriminator the SDK doesn't know about yet.

## Discriminator resolution

`TemplateCatalogValidator` calls
`TemplateTypeCatalog.ResolveTaggedDiscriminator` to map the modder's
`type="X"` string to a concrete subtype of the destination's
`TaggedPolymorphicBase`. Three candidates are tried per concrete
subtype:

1. Subtype short name itself (`"ActionConversationNode"`).
2. The subtype's name with the longest PascalCase suffix shared with the
   base class's de-Base'd residue stripped (`Action` from
   `ActionConversationNode` against base residue `ConversationNode`).
3. Same as (2) uppercased (`ACTION`) and screaming-snake'd
   (`ACTION_CONVERSATION_NODE`).

The longest-suffix rule matters for `BaseConversationNodeAction` where
the family suffix is `Action`, not the full `ConversationNodeAction`
residue (`SetFlagAction` ends with `Action`, not
`ConversationNodeAction`). The candidate set covers vanilla MENACE's
three observed conventions:

- `BaseConversationNode` → UPPER form (`ACTION`, `SAY`, ...). The
  underlying `ConversationNodeType` enum uses screaming-snake too.
- `BaseRoleRequirement` → PascalCase (`HasOneTag`,
  `IsOnBattlefield`).
- `BaseConversationNodeAction` → PascalCase (`SetFlag`).

Subtype enumeration uses `EnumerateNonAbstractSubtypes`, broader than
`EnumerateConcreteSubtypes`. The leaves-only filter is wrong for tagged
strings: intermediate concrete classes like `SayConversationNode`
(parent of `ChoiceConversationNode` and `RoleChoiceConversationNode`)
still appear as `"SAY"` entries in vanilla data.

The validator preserves the modder's discriminator on
`CompiledTemplateComposite.TaggedDiscriminator` and rewrites
`TypeName` to the resolved CLR FQN. The applier reads
`TaggedDiscriminator` to emit the `"DISCRIMINATOR|"` prefix.

## Runtime pack

`TemplatePatchApplier.TryConstructComposite` detects
`TaggedDiscriminator` and routes the composite through a pack step:

1. Allocate the typed instance and apply inner ops as usual.
2. Call `UnityEngine.JsonUtility.ToJson(instance)` to serialise the
   typed object. Matches the format vanilla
   `OnAfterDeserialize` consumers expect (public fields plus
   `[SerializeField]` privates).
3. Prefix the discriminator and `|` to produce
   `"<discriminator>|{json}"`.
4. Return the string for the `List<string>` or string destination.

Recursion is automatic. A `type="ACTION" { set "m_SerAction"
type="SetFlag" { ... } }` packs the inner `SetFlag` first
(`"SetFlag|{...}"`), assigns it to the typed `ActionConversationNode`'s
`m_SerAction` field, then JsonUtility serialises the outer instance.
The result is `"ACTION|{\"Guid\":...,\"m_SerAction\":\"SetFlag|{...}\"}"` —
exactly the vanilla shape.

## Auto-fills

Four ergonomic fillers reduce boilerplate around tagged-string
authoring. All idempotent and bypassed if the modder set the field
explicitly.

### Node Guids (`NodeGuidAutoFiller`, post-validation)

Every `BaseConversationNode` subtype and every
`ConversationNodeContainer` carries a `Guid: int` used by MENACE's
playback engine for GOTO/IF jumps and save-state references. The
filler injects `set "Guid"` ops on composites of those types when
omitted. Value is `FNV-1a("{patchId}#node_{counter}")` where the counter
increments per composite within a patch — stable across rebuilds,
distinct from vanilla.

### Clone identity (`CompositeAutoFillers.FillCloneIdentity`, pre-validation)

The cloneId always equals the asset's identity field. Two types use a
named field other than `Object.name`:

- `SoundBank.bankId` (string-FNV'd to int).
- `ConversationTemplate.Path` (string).

When `clone "X" id="Y"` is parsed, the filler injects `set "<identity>"
"Y"` if absent. Modders no longer need to repeat the cloneId.

### `Stem.Sound.id` from `Stem.Sound.name` (`FillSoundId`, post-validation)

When a `Stem.Sound` composite carries `set "name" "X"` but no `set
"id"`, the filler appends `set "id" FNV-1a("X")` as an Int32 (the
loader can't FNV strings at apply time, so the filler hashes
directly). Within a SoundBank, uniqueness only requires distinct
names.

### `VariationCopyCount` parallel-array sync (`FillVariationCopyCount`, post-validation)

`VariationConversationNode` carries two parallel arrays:
`Variations: List<ConversationNodeContainer>` and
`VariationCopyCount: List<int>`. Vanilla data has one copy-count entry
per variation (default `1`). When the modder appends Variations
without matching VariationCopyCount appends, the playback path silently
skips the unbacked branches. The filler counts `append "Variations"`
ops on the composite and pads `VariationCopyCount` with `1`s to match.
Skipped if the modder cleared or set the array explicitly.

## ID indexing

The asset indexer (`AssetPipelineService`) records per-asset metadata
that the compile-time symbolic resolvers consume:

- `AssetEntry.BankId` on Stem.SoundBank entries — the bank's own
  `m_Structure.bankId` int. Lets `Sound.bankId` references use the
  bank's name as a string; the validator FNV-resolves via
  `InMemoryBankIdResolver`.
- `AssetEntry.Roles` on ConversationTemplate entries — the
  conversation's `Roles[]` flattened to `(RoleName, Guid)` pairs.
  Drives `RoleGuidResolver`, which turns
  `set "RoleGuid" "Entity"` into the int Guid of the matching role.
  Follows the clone → source chain so a cloned conversation resolves
  against its source's roles.

Both bumped the asset-index format version (v5, v6). Stale caches are
flagged at compile start.

## Out of scope at this layer

- **Modder-defined roles in the same patch.** The
  RoleGuid resolver looks up against the source asset's Roles only.
  A modder adding a brand-new Role to the cloned conversation gets
  the auto-generated Guid via `NodeGuidAutoFiller`-style emission
  later, but the SAY's `RoleGuid` can't symbolically reference the
  new role yet. Workaround: write the int Guid explicitly.
- **Cross-mod SoundBank cross-references.** `CompilationService`
  registers each mod-defined SoundBank's `cloneId` →
  `FNV-1a(cloneId)` for the current compile only. A SAY node in mod
  A pointing at a SoundBank cloned in mod B isn't supported.

## See also

- [`conversation-cloning.md`](conversation-cloning.md). Runtime
  injection of cloned `ConversationTemplate`s into live conversation
  managers, the layer that picks up the tagged-string-authored
  payload.
- [`soundbank-runtime-registration.md`](soundbank-runtime-registration.md).
  `Stem.SoundManager.RegisterBank` for cloned banks.
- [`audio-bank-routing.md`](audio-bank-routing.md). The
  `Stem.ID{bankId, itemId}` indirection that SAY nodes use to reach
  AudioClips.
