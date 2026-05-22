# Investigation: Cloning a Leader's Voice (sy → voymastina)

Date: 2026-05-22

## Goal

Characterise the full surface for cloning a vocal leader end-to-end: the per-character SoundBank, the ConversationTemplates that drive each spoken line, the speaker-binding mechanism, and the subtitle path. Anchored on WOMENACE's voymastina clone of squad_leader.sy, but the findings generalise to any named MENACE leader.

The existing [`verified/audio-bank-routing.md`](../verified/audio-bank-routing.md) covers the bank-to-clip chain for skill and weapon audio at the level of `weapons_soundbank`. This investigation extends that picture to the character voice surface, which uses a separate set of per-character banks and an additional ConversationTemplate layer that audio-bank-routing.md does not cover.

## Method

1. `jiangyu templates search` / `jiangyu templates inspect` to enumerate ConversationTemplates by `Path` prefix and SoundBanks by name pattern.
2. `jiangyu assets inspect object` to read each bank's `m_Structure.bankId` directly from `sharedassets0.assets`.
3. Direct grep of the cached `template-values.json` (~207MB) for cross-template matches: speaker tag values, conversation paths, Sound IDs.
4. Python-based parse of the ConversationTemplate.Nodes.m_SerializedNodes JSON-string list to identify the speaker role per conversation (SAY.RoleGuid → Roles[].Guid match).
5. Compile-time smoke tests in WOMENACE to verify the `clone "SoundBank"` path lands without modification to Jiangyu.

## Results

### Bank inventory

44 SoundBank assets exist in `sharedassets0.assets`, pathIds 19286-19329 (contiguous). Three categories:

| Category | Count | Examples |
| --- | ---: | --- |
| Generic | 15 | `weapons_soundbank`, `human_soundbank`, `ui_soundbank`, ... |
| Strategic per-character | 2 | `strategic_barks_fmzama_va`, `strategic_barks_hayflick_va` |
| Tactical per-character | 27 | `tactical_barks_jeansy_va`, `tactical_barks_bog_va`, `tactical_barks_pirates_va`, `tactical_barks_carda_va_early/full_mid/late`, ... |

`HashableIdFieldRegistry.BankIds` currently lists only the 15 generic banks. The 29 character banks are absent, so `Stem.ID.bankId="tactical_barks_jeansy_va"` does not resolve to the correct int (1924654305) through the existing string-to-int registry path. Modders today can only reference them by raw int.

Each tactical bark bank holds 200-400 distinct `Sound` entries, each with multiple `SoundVariation.clip` references. `tactical_barks_jeansy_va` carries 315 sounds.

### Sy's voice surface

| Channel | Count | Where it lives |
| --- | ---: | --- |
| Tactical bark conversations | 188 | `JeanSy/*` ConversationTemplate paths in resources.assets |
| Voiced sound entries | 315 | `tactical_barks_jeansy_va.sounds[]` (bankId 1924654305) |
| Direct hire/promotion sound IDs | 2 + 4 slots | `UnitLeaderTemplate.HiringSelectBarkSound`, `HiredBarkSound`, `PromotionBarkSounds[]` (4 slots all null for sy) |
| Tactical bark tic sound | 1 slot | `SpeakerTemplate.SoundOnTacticalBarkShown` ({0,0} for sy) |

The 188 conversation paths span the trigger set defined by `ConversationTriggerType` (51 values: click responses, enemy sighted/killed, movement, combat reactions, squad responses, arrival, mission outcome, objectives, special weapon use, status events, feats). Examples from `JeanSy/`: `click_bark`, `enemy_killed_aimedshot`, `moving_jeansy_quietly`, `response_kill_lim`, `taking_fire_weakened`, `feat_self_constructs_killed_3`.

### ConversationTemplate Nodes structure

`ConversationTemplate.Nodes` is a `ConversationNodeContainer` whose only serialised field is `m_SerializedNodes: List<string>`. Each string is `<TYPE>|<json>`, where TYPE is one of:

- `SAY`. payload `{Guid, Sound: {bankId, itemId}, RoleGuid, Text}`. `Text` is the inline subtitle, resolved at runtime by `SayConversationNode.GetTranslatedText(_conv)`. `Sound` is the `Stem.ID` indirection into a SoundBank.
- `ACTION`. payload `{Guid, m_SerAction: "<ActionType>|<json>"}`, nested. Common action: `SetFlag|{"FlagName": "...", "FlagValue": true}`.
- `VARIATION`. payload `{Guid, Variations: [{Guid, m_SerializedNodes: [...]}], VariationCopyCount: [...]}`. Recursive: each variation carries its own inner `m_SerializedNodes` list.
- `CHOICE`. payload `{Guid, Choices: [{m_SerializedNodes: [...]}]}`. Same recursive shape as VARIATION.
- `EMPTY`. payload `{Guid}`.

`Roles[]` carries the speaker definitions. Each `Role`:

- `RoleName` (string, descriptive: `"Entity"`, `"Killer"`, `"Killed"`, `"JeanSy"`, ...)
- `Guid` (int, referenced by SAY.RoleGuid to identify the speaker)
- `Position` (enum: Left, Right, ...)
- `EntityIdx` (int)
- `m_SerializedRequirements: List<string>` where each entry is `<RequirementType>|<json>`. Common requirement types: `IsOnBattlefield|{}`, `Health|{"Health":1}`, `HasOneTag|{"Tags":"sy"}`, `HasNotTag|{"Tags":"segel"}`, `IsAlly|{}`, `IsEnemy|{}`, `IsActor|{}`, `IsType|{"Type":N}`, `ActionPoints|{"Operation":N,"Value":N}`, `HasLastSkillTags|{"Tags":"..."}`, `Statistic|{"Statistic":N,"Operation":N,"Value":N}`.

The `HasOneTagRoleRequirement.FulfillsRequirement(FindConversationSpeakersRequest, SpeakerTemplate _template, IConversationEntity _entity)` method takes both a SpeakerTemplate and an Entity, so the tag check considers both. Sy's `SpeakerTemplate.Tags` string is `"sy pirate_sl female infantry waybacker"`, and the conversation matcher picks up the `"sy"` token from that string.

### Speaker identification across 188 conversations

For each `JeanSy/*` conversation, the speaker role is the `Role` whose `Guid` matches any SAY node's `RoleGuid`. Proper parse of m_SerializedNodes (including recursive walk into VARIATION/CHOICE inner lists) and cross-reference against Roles[] gives:

| Speaker tag pattern | Count | Notes |
| --- | ---: | --- |
| HasOneTag="sy" (solo) | 173 | Sy alone speaks. |
| HasOneTag="sy" + other leader's tag | 12 | Multi-speaker conversations (sy + hayflick / rewa / lim / exconde / greifinger / darby / pirate_heavy). Sy's role and the other role both carry HasOneTag. |
| Not sy (3) | 3 | 2 are ivey-speaker conversations misfiled under sy's folder (`JeanSy/final_mission_a_arrival`, `JeanSy/final_mission_b_seeingboss`). 1 has `HasOneTag|{"Tags":""}` which never matches any character (vanilla dead asset: `JeanSy/arrival_wayback_intro`). |

In-scope for a sy → voymastina voice clone: **185 of 188 conversations**. The 3 not-sy-speaker entries do not need cloning.

### Cloning operation per conversation

Per-conversation transform is uniform across all 185 in-scope cases:

1. Rename `Path` from `JeanSy/X` to `Voymastina/X`.
2. Locate the role whose Requirements contain `HasOneTag|{"Tags":"sy"}`. There is exactly one such role per in-scope conversation, but its position in Roles[] varies (we observed positions 0, 1, and 2 across the 185).
3. Rewrite that single requirement from `HasOneTag|{"Tags":"sy"}` to `HasOneTag|{"Tags":"voymastina"}`. Leave all other requirements on that role intact.
4. Leave all other Roles[] entries unchanged. This covers the 12 multi-speaker cases: the other leader's role keeps its existing tag, so voymastina-with-hayflick dialogue replaces sy-with-hayflick dialogue without otherwise altering the conversation shape.
5. For each SAY node inside Nodes.m_SerializedNodes, rewrite `Sound.bankId` to the voymastina bank's bankId and `Sound.itemId` to a new sound id within that bank.
6. For each SAY node, rewrite `Text` to voymastina's subtitle (the rendered string in `BarkPanel.m_BarkTextLabel`).

The operation does not depend on the specific Requirements set, the role count, the node tree shape, or any per-conversation special-casing. A CSV of `(vanilla_path, new_path, sound_id, text)` per SAY node drives all 185 clones via a uniform mechanical transform.

### Strategy selection

Three strategies considered:

| Strategy | Approach | Why not |
| --- | --- | --- |
| A. drop-in clip replacement | `assets/replacements/audio/<sy-clip-name>.wav`. The existing `AudioReplacementPatch` Harmony-hooks `AudioSource.PlayOneShot`/`Play`/`PlayDelayed`/`PlayScheduled`/`PlayClipAtPoint` and substitutes by clip name. | Substitutes globally by clip name. Replaces sy's voice everywhere too, which fails the "voymastina is a clone, not a replacement" intent: both characters can coexist in the same campaign and need distinct voices. |
| B. append new Sound entries to sy's bank | Patch `tactical_barks_jeansy_va.sounds[]` with new entries, clone JeanSy/* conversations to reference them by new itemIds. | Couples voymastina's audio data to sy's bank file. Cross-mod append fragility in vanilla bank files. Diverges from MENACE's own pattern (one bank per character). |
| C. clone the bank | `clone "SoundBank" from="tactical_barks_jeansy_va" id="tactical_barks_voymastina_va"`, rebuild sounds[], plus 185 cloned conversations referencing the new bank. | Selected. Matches MENACE's per-character convention exactly. Sy's bank untouched. No cross-mod append risk. |

### Runtime smoke test of Strategy C

Test bundle: one cloned bank with one Sound (placeholder 440Hz tone), one cloned ConversationTemplate (`Voymastina/click_bark` from `JeanSy/click_bark`) with role tag rewritten to "voymastina" and a SAY node referencing the new {bankId, itemId}, plus `voymastina_speaker.Tags` rewrite dropping "sy" and adding "voymastina".

End-to-end pass: clicking voymastina in-mission renders the subtitle and plays the test tone. Clicking sy plays sy's normal bark unchanged. Speaker-tag filtering correctly isolates the two characters.

### Jiangyu changes actually required

The SDK delta is larger than the original scoping anticipated. The compile-time pieces were small. The runtime pieces had several layers, each blocking until the previous landed:

1. **Path-keyed lookup for ConversationTemplate.** The non-DataTemplate ScriptableObject clone path resolves source assets via `Resources.FindObjectsOfTypeAll + Object.name`. For ConversationTemplate that's ambiguous: every speaker has a `click_bark`-named conversation. The differentiator is the template's `Path` field. New file: `NonDataTemplateIdentityRegistry.cs` maps template type names to their identity-field overrides (`ConversationTemplate -> Path`). `TemplateCloneApplier.FindBySourceId` consults this when name lookup fails. After the clone is created, the identity field is also set on the new instance so subsequent lookups by CloneId resolve to it.

2. **Conversation manager registry and per-trigger bucket injection.** Each `BaseConversationManager` subclass builds two immutable indexes in its constructor: `m_ConversationTemplates` (snapshot) and `m_AvailableTemplatesByTriggerType` (per-trigger bucket dict). A clone created at runtime is invisible to that scan even with `s_AllConversationTemplates` cache invalidation, because the manager's bucket dict is its own snapshot. New files: `ConversationManagerRegistry.cs` (tracks live managers and pending clones, injects clones into per-trigger buckets, dedup by native pointer) and `ConversationManagerTrackingPatch.cs` (one Harmony postfix on `BaseConversationManager.TryFindSpeakerForRole` that catches every subclass on first matcher use). The injection only touches the bucket dict, not the master array — replacing `m_ConversationTemplates` post-construction has downstream side effects that break the matcher for unrelated speakers.

3. **OnAfterDeserialize refresh.** ConversationTemplate stores requirements and conversation nodes in two forms: a string list (`m_SerializedRequirements`, `m_SerializedNodes`) and a typed list (`Requirements`, `m_Nodes`). The matcher reads the typed forms. Unity rebuilds the typed forms from the strings via `OnAfterDeserialize` at asset load, but a runtime-patched clone skips that callback. Without rebuilding, the matcher walks stale typed state and either reads sy's data on a voymastina clone, or NRE-fails entirely on a freshly-constructed `ConversationNodeContainer`. The injection code calls `Role.OnAfterDeserialize(template)` on each role and `ConversationNodeContainer.OnAfterDeserialize(template)` on `Nodes` before adding the clone to the manager's bucket.

4. **Stem.SoundManager.RegisterBank in a post-patch pass.** Cloned `SoundBank` ScriptableObjects exist in Unity's object graph but Stem's runtime `SoundManager` bank-by-id index only includes banks added through serialisation. An explicit `SoundManager.RegisterBank(bank)` call is needed. Critically, this must happen **after** the bank's `bankId` patch has applied, otherwise Stem indexes the bank under the source's bankId (copied verbatim by `Object.Instantiate`). New method: `TemplateCloneApplier.RunPostPatchHooks` runs after `_templatePatchApplier.TryApply` in `ReplacementCoordinator.ApplyReplacements`. SoundBank clones are recorded during the clone pass and registered with Stem in the post-patch pass.

5. **(Not implemented, deferred)** Structured KDL grammar for ConversationTemplate.Nodes. The current `m_SerializedNodes: List<string>` carries JSON-encoded strings. Authoring by hand-writing escaped JSON is fragile but works for now (Python emits the escape sequences cleanly into a `.kdl` file). A proper `nodes { ... }` block is unavoidable for bulk authoring of all 185 conversations and is the next major SDK item.

### What ended up wrong about the original scoping

- Predicted "~30 LoC" for the HashableIdFieldRegistry extension. The actual ConversationTemplate path needed roughly 450 LoC across three new files plus modifications to `TemplateCloneApplier` and `ReplacementCoordinator`.
- Underestimated the runtime indexing problem. The investigation assumed cache invalidation would be enough; actually `TacticalBarksManager` (and siblings) build their own immutable indexes that need explicit injection.
- Did not anticipate the typed-vs-serialised state duality on Roles and Nodes. The matcher uses typed state and OnAfterDeserialize rebuilds it; runtime patches skip that callback.
- Did not anticipate the `Object.Instantiate` shallow-copy of `bankId` requiring post-patch registration ordering with Stem.
- Underestimated the number of times the loop "patch lands, looks like it works, runtime breaks subtly, iterate" would run. Five distinct runtime issues each masked the next.

### Things that ended up easier than expected

- The Path-keyed lookup needed only a small registry plus reflection. Less effort than expected.
- Once `OnAfterDeserialize` was identified as the missing call, the fix was small.
- `Stem.SoundManager.RegisterBank` exists as a public static method, reachable via reflection on `Il2CppStem.SoundManager`. No custom IL2CPP plumbing needed.

## Conclusion

Strategy C is verified end-to-end for cloning a leader's voice. The cloning operation is uniform across 185 of 188 source conversations (the other 3 are non-sy-speaker noise). Subtitle replacement is mechanical: rewrite `SAY.Text` inside the cloned conversation's node tree, ensure `OnAfterDeserialize` is called so the matcher sees the typed update. The four runtime layers (Path lookup, manager injection, OnAfterDeserialize refresh, Stem post-patch registration) are general SDK capabilities that future non-DataTemplate ScriptableObject cloning will reuse.

The promoted findings live in `verified/conversation-cloning.md` and `verified/soundbank-runtime-registration.md`. This investigation doc remains as the historical trace of the iterations.

## Next Step

1. Bulk-author voymastina's 185 cloned conversations from a `(vanilla_path, new_path, sound_name, text)` content table.
2. Build the structured `nodes { ... }` KDL parser (deferred SDK item) so bulk authoring doesn't require escaped JSON.
3. Refactor the per-type post-patch hook pattern into an explicit `IRuntimeRegistryInjector` interface when a third SO type with the same shape appears.

## See also

- [Audio bank routing](../verified/audio-bank-routing.md). bank/Sound/SoundVariation chain, weapons_soundbank example.
- [Audio replacement](../verified/audio-replacement.md). Harmony-prefix mechanism for replacing existing clips by name (rejected for this use case as it substitutes globally).
- [Template cloning](../verified/template-cloning.md). non-DataTemplate ScriptableObject clone path that Strategy C reuses.
