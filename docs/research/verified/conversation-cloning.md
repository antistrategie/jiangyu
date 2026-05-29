# Conversation Cloning

Status: **verified** (in-game smoke test, 2026-05-22). Clones of
`ConversationTemplate` register into a live `BaseConversationManager`'s
per-trigger bucket dictionary and fire from the matcher with patched
subtitle text and audio for the right speaker only.

## Contract

A modder declares a `ConversationTemplate` clone via the standard
`clone` block. Jiangyu's loader produces a runtime `Object.Instantiate`
copy, applies any inline patches, refreshes the typed-vs-serialised
state, and injects the clone into each live conversation manager whose
`ConversationType` filter matches. The clone fires from the matcher the
next time a relevant trigger lands.

The cloning operation is uniform regardless of the source conversation's
shape (single or multi-speaker, plain or VARIATION/CHOICE node tree),
because the four runtime layers are content-agnostic.

## Identification surface

Top-level template lookup uses Unity object names. For most
non-`DataTemplate` ScriptableObjects (`SoundBank`, `PerkTreeTemplate`,
...), the asset's `Object.name` is the modder-facing identifier and a
plain `Resources.FindObjectsOfTypeAll` filter finds the source.

`ConversationTemplate` is the exception: every speaker has a
`click_bark`-named conversation, so `Object.name` is non-unique. The
unique identifier is the template's `Path` field
(`JeanSy/click_bark`, `Bog/click_bark`, ...). Jiangyu's
`NonDataTemplateIdentityRegistry` records this override:

```
"ConversationTemplate" -> "Path"
"Il2CppMenace.Conversations.ConversationTemplate" -> "Path"
```

When the clone applier resolves a `ConversationTemplate` `sourceId` /
`cloneId`, it first tries the standard `Object.name` match, then falls
back to the registered identity field. After `Object.Instantiate` the
new clone has the source's `Path` value. The applier sets the
identity-field on the clone to its KDL `cloneId` so subsequent
lookups by `cloneId` resolve. Modder-side `set "Path" "..."` patches
overwrite this with the same value harmlessly.

## Runtime injection

Each concrete `BaseConversationManager` subclass
(`TacticalBarksManager`, `StrategyConversationsManager`,
`BlackMarketBarksManager`, `EventManager`) builds two immutable indexes
in its constructor:

- `m_ConversationTemplates: Il2CppReferenceArray<ConversationTemplate>`.
  A flat snapshot filtered by the manager's `ConversationType`.
- `m_AvailableTemplatesByTriggerType: Dictionary<ConversationTriggerType, List<ConversationTemplate>>`.
  Pre-bucketed candidate list per trigger, consulted by the matcher's
  `GetAvailableConversationTemplates(trigger)` hot path.

A clone created at runtime, after a manager's constructor has run, is
invisible to those indexes. Cache-invalidating
`ConversationTemplate.s_AllConversationTemplates` doesn't help: each
manager owns its own snapshot.

Jiangyu's `ConversationManagerRegistry` solves this by tracking live
managers and injecting clones into their per-trigger bucket dictionary.
The bucket dict is mutable (it's an Il2Cpp `Dictionary` reference),
which is the matcher's actual hot read. The master
`m_ConversationTemplates` array is not touched. Replacing it
post-construction has downstream side effects that break the matcher
for unrelated speakers.

Manager discovery is via a Harmony prefix on
`BaseConversationManager.GetAvailableConversationTemplates` (an instance
method every subclass inherits unchanged). Each time the matcher
queries a manager for the candidate list at a given trigger, the prefix
runs before the body, captures `__instance`, and hands it to the
registry. The registry deduplicates by native pointer so the per-trigger
hot path doesn't pay registration cost beyond the first invocation per
manager. Patching at the per-trigger entry point is also the earliest
reliable hook in the dispatch chain. Anything later (e.g.
`TryFindSpeakerForRole`) runs after the candidate list has already been
snapshot, so injection there would miss the first dispatch.

This single Harmony patch covers every subclass uniformly. Direct
constructor patching is unworkable because Il2CppInterop's IL2CPP-side
patch backend rejects ctor patches on derived types
([Il2CppInterop #87](https://github.com/BepInEx/Il2CppInterop/issues/87)).
The lazy-on-first-use approach captures every manager that ever serves
a bark.

## Typed-vs-serialised refresh

`ConversationTemplate.Roles[i]` and
`ConversationTemplate.Nodes` each carry their content in two parallel
forms:

- A string list (`m_SerializedRequirements`, `m_SerializedNodes`) that
  Sirenix/Unity serialises to disk.
- A typed list (`Requirements: List<BaseRoleRequirement>`,
  `m_Nodes: List<BaseConversationNode>`) that the matcher reads at
  runtime.

`OnAfterDeserialize` is what rebuilds the typed lists from the strings.
Unity calls it automatically at asset load. A runtime-patched clone
skips that callback, so its typed lists are stale (still hold sy's
parsed requirements / nodes) or empty (after a composite-construction
replacement of `Nodes`). The matcher then either tests against sy's
old data or NREs.

Before injecting a clone into the bucket dict, the registry walks the
clone's `Roles` collection and its `Nodes` container, invoking
`OnAfterDeserialize(ConversationTemplate _conv)` on each. This rebuilds
the typed forms from whatever the modder's patches landed in the
serialised forms. After this refresh, the matcher sees consistent
state.

## ConversationType filter

Each manager filters its candidate set by `ConversationType`
(`StrategyConversation`, `TacticalBark`, `BlackMarketBark`, `Event`).
The clone applier reads the cloned conversation's `ConversationType`
and matches it against the target manager. The manager doesn't retain
its filter as a public field on the wrapper, so the registry infers
the filter by sampling an existing template from the manager's
`m_ConversationTemplates`. Every template in a single manager shares
the same `ConversationType`, so a single read is sufficient.

A clone whose `ConversationType` doesn't match a given manager is
silently skipped for that manager. If it matches a later manager's
filter, that one gets the clone.

## Speaker tag filtering

`Roles[i].m_SerializedRequirements` carries the `Type|{json}` form of
each requirement. The matcher's speaker filter consults
`HasOneTagRoleRequirement`, which compares the candidate's
`SpeakerTemplate.Tags` string against the requirement's tag.

To make a cloned conversation match a specific speaker only, the
modder rewrites the appropriate requirement entry and adjusts the
target speaker's `Tags` accordingly. Example: cloning a sy
conversation for a new character `voymastina_speaker` requires:

- `set` `voymastina_speaker.Tags` to drop `"sy"` and add
  `"voymastina"`.
- `set` the cloned conversation's role with the `"sy"` tag
  requirement to require `"voymastina"` instead. Index-based replace
  on `m_SerializedRequirements` works. The typed `Requirements` list
  is rebuilt by the refresh pass before the matcher reads it.

## Authoring example

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

The `Nodes` block uses inferred-composite construction: a fresh
`ConversationNodeContainer` is built and its `m_SerializedNodes`
populated from the inner ops. The refresh pass calls
`OnAfterDeserialize` on the new container to rebuild typed `m_Nodes`
before the matcher sees the clone.

`type="ACTION"` and friends use the tagged-string authoring path
(see [`tagged-string-authoring.md`](tagged-string-authoring.md)). The
modder's discriminator string resolves to the concrete
`BaseConversationNode` subtype, inner ops apply against that type, and
the loader JSON-serialises and prefixes
`"DISCRIMINATOR|"` before writing to `m_SerializedNodes`. Recursion
through `Variations[].m_SerializedNodes` and `ActionConversationNode.m_SerAction`
is automatic.

Auto-fills cover the typical omissions:

- `Path` defaults to the cloneId.
- `RoleGuid "Entity"` resolves against the source's
  `Roles[].RoleName` via `AssetEntry.Roles` populated at index time.
- Node `Guid` values are deterministically generated per composite.
- `VariationCopyCount` is padded to match the number of `Variations`
  appends.

## Out of scope at this layer

- **`ConversationTemplate.m_ConversationTemplates` master array.**
  Updating only the per-trigger bucket dict is sufficient for the
  matcher's hot path. Mutating the master array post-construction
  breaks the matcher for unrelated speakers and is deliberately
  avoided.

## See also

- [`audio-bank-routing.md`](audio-bank-routing.md). The
  `Stem.ID{bankId, itemId}` indirection that SAY nodes use to reach
  AudioClips, and SoundBank patching.
- [`soundbank-runtime-registration.md`](soundbank-runtime-registration.md).
  Registering cloned SoundBanks with Stem's runtime so cloned
  conversations' SAY nodes resolve to real audio.
- [`template-cloning.md`](template-cloning.md). The underlying
  `DataTemplate` clone primitive. ConversationTemplate is not a
  DataTemplate so it goes through the non-DataTemplate clone path.
