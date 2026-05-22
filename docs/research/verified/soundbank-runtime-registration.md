# SoundBank Runtime Registration

Status: **verified** (in-game smoke test, 2026-05-22). A cloned
`Stem.SoundBank` becomes addressable through `Stem.ID{bankId, itemId}`
after Jiangyu registers it with `Stem.SoundManager` in a post-patch
pass.

## Contract

`Stem.SoundManager` holds a static bank-by-id index that
`Sounds.GetSound(ID)` consults whenever a SAY node, skill audio
field, or any other `Stem.ID`-typed reference resolves an audio
event. Vanilla banks land in this index via Unity's serialisation
lifecycle when the game loads its assets.

A clone produced by `Object.Instantiate` does not. The clone exists
as a live `ScriptableObject` (visible to `Resources.FindObjectsOfTypeAll`
and walkable through Jiangyu's runtime-template machinery) but
`SoundManager.GetBank(id)` doesn't see it. Playback through any
`Stem.ID` reference targeting the clone fails silently.

Jiangyu's loader closes the gap by calling
`Il2CppStem.SoundManager.RegisterBank(SoundBank)` on each cloned
SoundBank during the post-patch hook pass. After that call,
`Stem.ID{bankId, itemId}` references resolve normally and SAY-node
playback fires the modder's clips.

## Ordering: registration must happen post-patches

`Object.Instantiate` shallow-copies the source bank's
`m_Structure.bankId`. Modder patches that set `bankId` to the new
bank's name take effect on the in-memory clone, but
`SoundManager.RegisterBank` indexes the bank by reading the field
value at the time of the call. Registering at clone time, before
patches apply, would index the clone under the source's `bankId`
(stale) and leave the modder's chosen `bankId` orphaned.

The loader records each SoundBank clone in
`TemplateCloneApplier._pendingSoundBankRegistrations` during
`TryApplyScriptableObjectType`. After the patch applier finishes its
pass, `ReplacementCoordinator.ApplyReplacements` calls
`TemplateCloneApplier.RunPostPatchHooks`, which iterates the pending
list and registers each clone. At that point `bankId` has its final
value and Stem's index matches the data in the bank.

## bankId values: not hardcoded, asset-index-sourced

`HashableIdFieldRegistry` resolves a string-keyed `Stem.ID.bankId`
field to its integer value through an `IBankIdResolver` installed
at compile start. The production resolver
(`InMemoryBankIdResolver`) is populated from the
`AssetIndex.AssetEntry.BankId` field, which the asset indexer
extracts from each SoundBank's `m_Structure.bankId` during the
`jiangyu assets index` pass. No hardcoded table ships.

Two consequences:

- Adding a new SoundBank to MENACE (vanilla update or DLC) requires
  rebuilding the index. No Jiangyu code change.
- The indexer's SoundBank-detection filter covers both naming
  conventions: `*_soundbank` for the 15 generic banks, plus
  `tactical_barks_*` / `strategic_barks_*` for the 29 character-
  specific bark banks. All 44 banks (in MENACE 6000.0.72f1) ship
  with `BankId` populated on their `AssetEntry`.

## Authoring example

```kdl
clone "SoundBank" from="tactical_barks_jeansy_va" id="tactical_barks_voymastina_va" {
    clear "sounds"
    append "sounds" {
        set "name" "voymastina_click_bark_1"
        append "variations" {
            set "clip" asset="voymastina/click_bark_1"
        }
    }
}
```

Four auto-fills cover the boilerplate:

- `bankId` defaults to the cloneId (FNV-1a-hashed at apply). Same hash
  `Stem.ID.bankId` references resolve to.
- `Sound.id` defaults to the modder-set `Sound.name` (FNV-1a-hashed at
  compile time). Within-bank uniqueness only needs distinct names.
- `Sound.fixedVolume`, `dopplerLevel`, `minDistance`, `maxDistance`
  are seeded from `TypeDefaultsRegistry`. Without them a freshly-
  allocated `Stem.Sound` plays silent.
- `SoundVariation.fixedVolume`, `fixedPitch` are seeded the same way.

`Stem.ID` references elsewhere (typically SAY nodes inside a cloned
`ConversationTemplate`) use the same name strings:

```kdl
set "Sound" {
    set "bankId" "tactical_barks_voymastina_va"
    set "itemId" "voymastina_click_bark_1"
}
```

Both sides FNV-hash to the same ints, so the indirection through the
asset index's `AssetEntry.BankId` and the Sound.id auto-fill keep the
references linked deterministically.

## Out of scope at this layer

- **`busIndices` parallel-array auto-extend.** A freshly-appended
  Sound needs the bank's `busIndices: Int32[]` to grow in lockstep
  so the default bus is picked up. Otherwise playback is silent.
  Documented in the soundbank-construction-gotchas agent memory but
  not enforced by Jiangyu yet. The smoke test working without an
  explicit auto-extend suggests append paths through Jiangyu's
  collection helpers handle it implicitly. Worth verifying when
  authoring banks with non-default bus routing.
- **Mod-defined bank cross-references at compile time.** A modder
  declaring a brand-new bank in their KDL gets the bank's own
  `bankId` resolved via FNV automatically. Cross-references to that
  bank's name from `Stem.ID.bankId` in the *same compile* aren't
  auto-registered in the resolver yet. The modder either uses the
  literal int (via a generator script) or waits for the resolver to
  pick up the freshly-built `cloneId` declaration.

## See also

- [`audio-bank-routing.md`](audio-bank-routing.md). The
  `Stem.ID{bankId, itemId}` indirection chain.
- [`audio-replacement.md`](audio-replacement.md). Harmony-prefix
  mechanism for replacing existing clips by name, which targets a
  different layer (Unity `AudioSource.Play*`) and does not need
  Stem registration.
- [`conversation-cloning.md`](conversation-cloning.md). Runtime
  registration of cloned `ConversationTemplate`s, the consumer of
  this Stem-registration capability.
