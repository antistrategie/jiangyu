# Templates registered by other loaders

Status: implemented and verified in-game (2026-09-09). The regression harness is `tests/harness/late-templates` (`mise run harness:late-templates`).

A Jiangyu mod can depend on a template that another MelonLoader mod registers after Jiangyu's pass for the type has run: as the template it patches, as the `from=` source of a clone, or as a `ref` value, inside a constructed handler included. This note is the rule the loader follows, what the code commits to, and the case matrix it is held to.

## The rule
A mod's operations on one template form a block. A block applies only when every template it refers to exists: the template it targets, and every template a value in it refers to (`ref=` values, including those nested inside constructed handlers / composites). A block applies in order, all at once. Until then it is held: nothing in it is written. It applies the moment the last referenced template appears, wherever that happens, and it is never partially applied. This one rule covers the three ways a Jiangyu mod can depend on a template registered by another MelonLoader mod: patching it, cloning it (clone with a missing `from=` source), and referring to it in a value.

## Why the block, why no give-up
- The unit is the whole block, not the failing operation: a patch that removes a handler and appends a replacement must not leave the template with the handler removed and nothing in its place.
- The block is per mod, not per template: another mod's operations on the same template apply on their own (existing composition rules, cross-mod index dependencies are already unsupported).
- No give-up: there is no ground truth for when another loader is finished (observed: 14 s into the first scene, on the title scene, and 4 s after a save load). A held block waits as long as it takes, reported once per scene schedule (the reported flag resets on scene change). A block whose reference never appears never applies, loudly. Held ops count as waiting in the self-check, never as mismatches.

## Pieces
1. Compiler: every compiled reference carries its type (the destination field's declared type, or the explicit `ref="Type"`), so the loader can check existence without reflecting over live objects. Composites are walked recursively. A reference to an id absent from the index remains a valid compile.
2. Readiness check (loader, TemplatePatchApplier): before a block applies, collect its references (composites included) and check each with the same lookup the apply uses. DataTemplate id = map lookup. Non-DataTemplate id = matched by name against one enumeration per type per pass (one probe per pass, shared by target lookups, reference checks, the replay and the would-hold question), with at most one Resources folder reload per type per scene on a miss (the game unloads unreferenced assets, a later scene may need one again). A reference whose type resolves to something that is neither a DataTemplate nor a ScriptableObject, or whose lookup throws, is unresolvable: not held, reported by the op when it applies. A reference to a Jiangyu clone that is itself held counts as missing. A reference whose type does not resolve (or whose lookup throws) is a mismatch reported at apply time, not a template still to come. A reference without a type is resolved at apply time (see 6). If anything is missing, the block goes into the held set with its missing ids, the type's pass continues with the next block.
3. Retry: held blocks retry wherever held clones retry: every poll of a scene's schedule, then a template-only pass every 300 frames (no renderer sweep, prefab rebind or texture pass) for as long as any template work is deferred (held blocks or clones, a type not live yet, a scoped locale re-run) (the schedule ends at frame 600 and another loader registers on its own clock), every scene load, and a Harmony prefix at priority Last on new game / save load / startup entry points (it runs after every prefix registered before it or at a higher priority, a prefix another mod installs afterwards at the same priority runs after it, and the next pass covers what that one registers). They run after the held clones in the same pass. When a block lands, its ops apply in source order, and blocks that land in one pass apply in load order. The catalogue keeps a `set` a later-loaded mod overrides, and the applier skips it only when the later value is already on the slot, so a held later block never takes a ready earlier mod's value with it and the later mod wins whichever block lands first. A set, clear, insert or remove resets what sits under its slot (a set or clear replaces it, an insert or remove moves the elements after it), and the sets recorded there are forgotten, so a later value the late block wiped or moved never makes the late block's own value skip. Nothing already applied is written again: a structural conflict between two mods' blocks on one shared template lands in the order the blocks land (cross-mod index dependencies are unsupported). A block whose op fails against the live game (member missing, conversion) is a mismatch: reported, counted, the block's other ops still apply. The rule holds for templates that do not exist yet, not for game-shape mismatches, which the compile contract covers. Post-template work runs scoped to that template (chained-clone re-inherit, conversation-clone refresh, locale), held blocks' templates are excluded from the load-time locale apply and translated by the scoped re-run when they land.
4. Chained-clone replay (ReapplyTemplateEntry) is made of blocks like any pass, split by mod: a block whose references are missing is held, when it becomes ready it is released into the rebase-and-replay of the following post-work rather than applied directly, so its ops run once. A non-DataTemplate clone chained on a source whose block is held waits for that block (such clones copy their source once and are not re-inherited), in every clone pass including the early-injection prefix. The one exception is a source block that itself waits on that clone: the clone then copies the source as it stands, the block lands once the clone exists, and the copy keeps the earlier state (documented limitation, no deadlock). A SoundBank clone whose block is held stays queued for Stem registration until the block lands. Locale inheritance does not decide a clone whose block is held.
5. Reporting: self-check counts a held block's ops as waiting, not mismatches, at the end of a scene's schedule, and after any later steady-state pass, each held block not yet reported this scene is warned once naming mod, template and the ids still missing (taken again on every retry), when a block lands, one line says so. Templates-applied signal unchanged (does not wait on held blocks).
6. The compiler stamps reference types during catalogue validation, which needs the game assembly beside the configured game data, a compile without it warns that references stay untyped, and such a block applies as today (partial on a missing ref).

## Details the code commits to
- A DataTemplate clone is registered in every ancestor map except the root `DataTemplate` map, which the clone applier leaves alone, so a patch may address it under an ancestor's name or another spelling of its type. Two names alias when one type derives from the other (`TemplateRuntimeAccess.SameTemplateSpace`). One mod's ops on one template under every alias in the catalogue (`TemplatePatchCatalog.AliasNames`) are one block, gathered in load order by the first pass that sees the template and skipped by the aliases' own passes. Chained-clone detection, the replay, touched-field tracking, held-block matching, locale scoping and filtering, and authored-text detection all match by the same rule.
- A missing template is recorded as a (canonical type name, id) pair (`TemplateRef`), so references written with qualified or ancestor names and clone directives name the same thing, and a clone's self-dependency exception matches by template rather than by parsing a key.
- A target looked up under an ancestor or base name is cast to the wrapper of its live concrete type before its group's ops apply, so ops declared under a descendant name find their members.
- Both catalogues file a type under one canonical name (`TemplateRuntimeAccess.CanonicalTypeName`: a code-defined `ns:Name` as written, else the short name when it resolves to the type on its own, else the full name), so a qualified and a short spelling are one entry, one chain, one held key, one changed key, whichever catalogue names it. Aliases (`TemplatePatchCatalog.AliasNames`) are then ancestor and descendant entries only, each once.
- A reference to a clone this loader still holds counts as missing until the clone registers. An id another loader registers first is an id collision between two mods: the directive is skipped as already registered whatever the state of its source, the foreign template stands, and it is never treated as a chained clone (never rebased).
- A chain may be declared across ancestor and descendant entries (`TemplateCloneCatalog.TryGetDirective`), a source declared under a descendant entry is mirrored into the clone's map, a clone declared under an ancestor's name is registered in its live type's slot too and rebased with every member of that type. The scoped rebuild and locale inheritance walk the entries to a fixed point, so a chain in an entry already visited follows its source.
- A clone rebased from a source this loader had already translated keeps the source's recorded English as its own original, so the source language restores English.
- An op that throws counts as a conversion failure and the block's other ops still apply, a type whose enumeration throws stays pending for the next pass without stopping the pass.
- A stamped reference type is the short name when the catalogue resolves it alone (the spelling the offline previews use), else the full reflection name, which the loader resolves exactly.
- Post-template work a pass could not complete (a chain rebuild whose map was not available, a clone not in its map, a rebuild that threw) is remembered with its scope and run again by the next pass.
- A held clone's translations are left out of the plan like a held block's, so the load-time apply completes without them.
- A chained clone's held block is released to the replay once the clone and every template it refers to exist, and stays held, marked released, until the replay has run and removed it: a rebuild that cannot run this pass is retried by the next. A block the replay holds for a missing reference replaces the released one, so it is one entry, reported once.
- A block is all of one mod's ops on one template (`SplitBlocks` groups by mod), in load order. The held set treats a template addressed under an ancestor's name and under its own as one template (`HeldPatchBlocks.Add` matches by `SameTemplateSpace`), so the initial pass and the replay never hold it twice.
- The existence probe keys its enumerations by resolved type, shares them with the type's own pass, and lets an empty type reload its folder once per scene.
- The pass's probe drops its ScriptableObject enumerations whenever a clone registers, so a clone created after its source's held patches landed is found by the same pass.
- Conversation-clone refresh after a late pass matches the changed set by template (alias-aware), not by key spelling.
- A ScriptableObject clone of a source whose patches are held (or would be held, judged before the source's own pass has run) waits, in every clone pass, except when the only thing the source's block waits on is that very clone.
- A held block's translations are left out per block (owner, template): another mod's translations of the same template, whose block applied, stay in. Conversations, which carry no owner, are excluded per template.
- A scene's poll schedule and steady-state loop stop at the next scene change, they never poll or report on a later scene's behalf.
- The self-check reports registered clones as configured minus held, and held ops as waiting.

## A concrete case
Its Bribe perk patches (remove a handler, append a Buyout handler whose Whitelist refs `enemy.pirate_scavengers_smg`, a clone another loader creates about 14 s later) are held whole at the first pass, the perks stay vanilla, the next poll after the entity registers lands all three blocks in order.

## Out of scope
C# code that looks a foreign template up in OnInit. Another loader's own ordering failures.

## Known limitation outside this design
Chained-clone re-inheritance keeps a clone's authored non-collection members across a rebase (so a source sub-object is never shared), which means an append into a collection nested inside such a member runs again on each replay. That is the existing replay contract, independent of late templates. It is documented, not changed, here.

# Case matrix

Every case is stated with the behaviour the code commits to after this pass. "Landing order" means the order blocks actually apply, which is load order for blocks that land together and otherwise the order they land.

## A. Type names
| Case | Behaviour |
|---|---|
| A1 short name `PerkTemplate` | canonical name, one catalogue entry |
| A2 qualified `Il2CppMenace.Strategy.PerkTemplate` | canonicalised to the short name at catalogue load (both catalogues), same entry, same held key, same chain |
| A3 short name shared by two game types | a name in the game's main assembly wins outright (the existing resolution order), a short name absent there and present in several other assemblies is ambiguous, stays its own entry and is reported by its pass. The compiler stamps references with the full name, so a stamped reference never depends on this |
| A4 code-defined `ns:Name` | kept as written (already canonical) |
| A5 ancestor name (`BaseItemTemplate:w` for a `WeaponTemplate`) | alias of the descendant entry (`SameTemplateSpace`): one block with the descendant-named ops, target cast to its live concrete wrapper before the ops apply. A clone declared under an ancestor's name has its collections and owned elements copied for its live type, so nothing stays shared with the source |
| A6 descendant name processed first, ancestor second | the first pass consumes both entries for the id, the second skips it |
| A7 `DataTemplate` root | never an alias of anything (no map), its own entry, resolved or not by its pass |
| A8 held key spelling across catalogues | both catalogues canonical, so the initial pass and the replay hold one entry per (type, id, owner) |

## B. Targets and references
| Case | Behaviour |
|---|---|
| B1 native DataTemplate, live | map lookup, block applies |
| B2 native DataTemplate, type not live in this scene | type pass does not latch, retried every poll and every 300 frames (pre-existing) |
| B3 id registered by another loader later (poll, after the schedule, next scene, inside a prefix on new game / load) | block held with (type, id), lands on the first pass after registration: poll, steady-state pass, scene load, or the priority-Last prefix, never applied partially |
| B4 id never registered | held for the session, warned once per scene, counted as waiting |
| B5 Jiangyu clone, native source | clone pass precedes patch pass: target exists |
| B6 Jiangyu clone, source registered late | clone held (`LateSources`), block on the clone held on the target, both land in the same pass the source appears (clones retried before patches) |
| B7 Jiangyu clone chained on a sibling clone (DataTemplate) | ops deferred to the rebase-and-replay, block held while the clone is absent, released to the replay once the clone and every reference exist, staying held until the replay has run, a rebuild that cannot run in a pass (map not available, clone not in the map, a rebuild that threw) is retried by the next pass over the same scope, the replay re-holds a block with a missing reference under the same entry. A chain declared across ancestor and descendant entries is one chain, a clone declared under an ancestor's name is registered in its live type's slot too and rebased with every member of that type |
| B8 ScriptableObject clone chained on a sibling whose block is held | the clone waits for the block (`SourcePatchesHeld`), except a block whose only missing template is that very clone |
| B9 reference to a held Jiangyu clone | counts as missing (`CloneHeld`) until the clone registers. If another loader registers that very id first, the directive is released as already registered (an id collision between two mods, warned about) and the foreign template stands: references to it resolve, in both the DataTemplate and the ScriptableObject path, whatever the state of the directive's source or the source's patches, and whether the source has since appeared or not. The foreign template is not a chained clone: it is never rebased or mirrored, and patches on its id apply to it directly |
| B10 reference typed with an ancestor or base name of the target | resolved by that name (the map or the by-name enumeration under a base type finds it) |
| B11 reference whose stamped type does not resolve, or whose lookup throws | unresolvable: not held, the op reports the mismatch when it applies |
| B12 reference without a type (compiled without the game assembly) | resolved when the op applies, the compiler warns at compile time |
| B13 ScriptableObject reference, asset unloaded by the game | one Resources reload per type per scene, decided by the probe alone, then missing, the op that applies takes the object the probe found rather than enumerating again |
| B14 ScriptableObject type with no live object at all | one reload per scene in the patch applier's probe and one in the clone applier (each decided by that applier alone), then empty |
| B15 clone of a ScriptableObject type created later in the same pass | the probe forgets its enumerations on every registration, so the next lookup sees it |

## C. Op mixes inside one block
| Case | Behaviour |
|---|---|
| C1 scalar set | applies |
| C2 set into a collection index | applies, the recorded slot is the index |
| C3 append / insert / remove / clear | apply in source order within the block |
| C4 composite or type construction carrying a reference | the reference is walked (recursively) for readiness, the whole block waits on it |
| C5 op fails or throws against the live game (member missing, conversion, exception) | a mismatch: reported, counted, the block's other ops still apply (compile contract, not a late template) |

## D. Two mods on one template (A loads before B)
| Case | Behaviour |
|---|---|
| D1 both ready | A's block, then B's block: load order |
| D2 A held, B ready, then A lands | B applied, A applies after it, A's set on a slot B set is skipped, anything else lands in landing order |
| D3 B held, A ready, then B lands | A applied, B applies after it and wins |
| D4 A held with `clear X` then `set X.f`, B applied `set X.f` | A's clear resets X, the record under X is forgotten, A's `set X.f` applies: landing order (documented) |
| D5 B applied `set C[1]`, A lands late with `remove C[0]` | the remove forgets the records under C, nothing is replayed, landing order |
| D6 A and B both append to C, one held | appends land in landing order (documented: cross-mod index dependencies unsupported) |
| D7 later mod's set overrides an earlier one at catalogue load | both ops kept, the later applies after the earlier or the earlier is skipped when it lands late |
| D8 chained clone with blocks from two mods, one held | the rebase-and-replay re-applies every ready block in load order on each rebuild, the held block joins on release |

## E. Timing
| Case | Behaviour |
|---|---|
| E1 initial scene pass | blocks split, held or applied |
| E2 poll frames 5..600 | held blocks and clones retried each poll |
| E3 after frame 600 with anything deferred | template-only pass every 300 frames until nothing is deferred or the scene changes |
| E4 scene change while held | the scene's loop stops, the new scene's load pass retries, reported once per scene |
| E5 another mod's prefix on new game / load registers a template | Jiangyu's priority-Last prefix on the same method runs the late pass after every prefix registered before it or at a higher priority, a prefix installed later at the same priority runs after it, and the next pass covers what it registers (documented) |
| E6 Jiangyu's own early-injection prefix (normal priority) registers clones | drained into the next pass's changed set, the probe outside a pass has nothing to forget |
| E7 a clone registers between a probe's enumeration and a later lookup in the same pass | `Forget` on registration |

## F. Locale
| Case | Behaviour |
|---|---|
| F1 owner's translation of a held block's field | left out of the load-time apply (by the coordinate's owning mod, not the PO's shipper), written by the scoped re-run when the block lands |
| F2 another mod's translation of a template with a held block | stays in |
| F3 translation of a held clone (no block) | left out, like a held block's |
| F4 conversation subtitle of a held conversation clone | left out per template |
| F5 inherited text on a held clone or a clone of a held source | not decided until it lands, a clone rebased from an already-translated source keeps the source's English (its recorded original, or the text the mod authored on the source) as its own, so the source language restores English |
| F6 language switch while blocks are held | full re-apply without held work, scope cleared, held work translated on landing |
| F7 changed set keyed under an ancestor or other spelling | scope and inheritance match by template (`KeyedUnderAnyName`), inheritance orders a clone after its source whichever entry declares the source, walking the entries to a fixed point |

## G. Reporting and counts
| Case | Behaviour |
|---|---|
| G1 held at schedule end | warned once, naming the ids still missing |
| G2 first held by a steady-state pass | warned once by that pass |
| G3 new scene | warned once again |
| G4 missing list | recomputed on every retry |
| G5 self-check | held ops count as waiting, never as mismatches, registered clones = configured minus held |

## H. Post-work
| Case | Behaviour |
|---|---|
| H1 late landing on a chained clone's source | scoped rebuild of the chains on the changed set, walked to a fixed point so a chain in an entry already visited follows, a rebuild that could not run is retried by the next pass |
| H2 conversation clone changed under another spelling | refresh matches by template |
| H3 SoundBank clone whose block is held | Stem registration stays queued until the block lands |
| H4 a mod's `OnTemplatesApplied` edits between late passes | a late-only pass rebuilds only the chains the late ids touched |
