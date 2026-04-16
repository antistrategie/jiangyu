# Legacy Structural Spot-Check: OperationTemplate Conversation Arrays

Date: 2026-04-15

## Goal

Determine whether any OperationTemplate instance has populated conversation arrays (`IntroConversations`, `VictoryConversations`, `FailureConversations`, `AbortConversations`). If populated instances exist, assess polymorphism. If not, classify them as currently unassessable from Jiangyu's serialized inventory.

## Why This Target

The polymorphic reference array cross-template survey (same date) checked 2 of 8 OperationTemplate instances and found all 4 conversation arrays empty. The next-support-type-candidates list flagged a full inventory sweep as the top remaining priority: either find populated instances to advance, or conclusively close the question.

## Samples

All 8 OperationTemplate instances in `resources.assets` — complete inventory sweep, no sampling gaps.

| Operation | pathId |
|---|---|
| operation.alien_wildlife_alien_infestation | 113649 |
| operation.constructs_story1 | 113650 |
| operation.constructs_suspected_activity | 113651 |
| operation.pirates_counterinsurgency | 113652 |
| operation.pirates_generic_operation | 113653 |
| operation.pirates_thwart_invasion | 113654 |
| operation.rogue_army_generic_operation | 113655 |
| operation.rogue_army_turn_the_tide | 113656 |

## Method

1. Listed all OperationTemplate instances via `jiangyu templates list --type OperationTemplate`.
2. Inspected every instance via `jiangyu templates inspect --type OperationTemplate --name <name>`.
3. Extracted all 7 conversation/event fields from `m_Structure` on each instance:
   - 4 arrays: `IntroConversations`, `VictoryConversations`, `FailureConversations`, `AbortConversations`
   - 3 single references: `VictoryEvent`, `FailureEvent`, `AbortEvent`
4. Checked ConversationTemplate discoverability via `jiangyu templates list --type ConversationTemplate`.
5. Compared field names and types against legacy schema (`../MenaceAssetPacker/generated/schema.json`).

## Results

### Main result: conversation arrays

**All 4 conversation arrays are empty (0 elements) across 100% (8/8) of OperationTemplate instances.**

| Operation | IntroConv | VictoryConv | FailureConv | AbortConv |
|---|---|---|---|---|
| alien_wildlife_alien_infestation | 0 | 0 | 0 | 0 |
| constructs_story1 | 0 | 0 | 0 | 0 |
| constructs_suspected_activity | 0 | 0 | 0 | 0 |
| pirates_counterinsurgency | 0 | 0 | 0 | 0 |
| pirates_generic_operation | 0 | 0 | 0 | 0 |
| pirates_thwart_invasion | 0 | 0 | 0 | 0 |
| rogue_army_generic_operation | 0 | 0 | 0 | 0 |
| rogue_army_turn_the_tide | 0 | 0 | 0 | 0 |

All 4 arrays are typed `Menace.Conversations.ConversationTemplate[]`. The field type is stable across all 8 instances.

### Single-reference event fields

| Operation | VictoryEvent | FailureEvent | AbortEvent |
|---|---|---|---|
| constructs_story1 | pathId=108079 | pathId=108079 | pathId=108079 |
| (all other 7) | null | null | null |

`constructs_story1` is the sole operation with non-null event references. All 3 point to the same ConversationTemplate: `event_story_constructs_01_aftermath_cutscene` (pathId 108079). This is the one "story" operation in the inventory.

### Legacy schema comparison

Legacy declares:
- `IntroConversations: ConversationTemplate[]` (collection, element_type ConversationTemplate)
- `VictoryConversations: ConversationTemplate[]` (collection, element_type ConversationTemplate)
- `FailureConversations: ConversationTemplate[]` (collection, element_type ConversationTemplate)
- `AbortConversations: ConversationTemplate[]` (collection, element_type ConversationTemplate)
- `VictoryEvent: ConversationTemplate` (reference)
- `FailureEvent: ConversationTemplate` (reference)
- `AbortEvent: ConversationTemplate` (reference)

Jiangyu observes the same 7 fields with matching names and types. Zero mismatches.

Legacy inheritance for ConversationTemplate: `ScriptableObject → ConversationTemplate` (no DataTemplate, no SerializedScriptableObject). No known concrete subtypes in the legacy schema.

### Side findings (not the main conclusion)

**ConversationTemplate inventory size:** 3,626 instances in `resources.assets`. The conversation system is rich — conversations exist in large numbers but are not wired through OperationTemplate arrays.

**ConversationCondition shape:** `Menace.Conversations.ConversationCondition` is a single-field inline object (`ExpressionStr: String`). Populated on 2 operations:
- `rogue_army_generic_operation`: `rogue_army_ops_unlocked > 0`
- `rogue_army_turn_the_tide`: `rogue_army_ops_unlocked > 0`

Empty on the other 6.

**OperationIntrosTemplate:** 3 instances exist (`operation_intros.backbone`, `operation_intros.dice`, `operation_intros.zaynbeecher`). This may be the actual mechanism for operation-related conversation delivery rather than the direct conversation arrays. Not investigated further in this pass.

## Interpretation

**OperationTemplate conversation arrays are structurally validated but content-empty in the current serialized inventory.**

The 4 conversation array fields exist on every OperationTemplate instance with the correct type (`Menace.Conversations.ConversationTemplate[]`), matching the legacy schema exactly. However, no array contains any elements across the entire 8-instance inventory. This means:

- **Structural existence: confirmed.** The fields are present, correctly typed, and stable.
- **Polymorphism: not assessable.** With zero array elements, there is no data to evaluate whether the arrays would contain mixed concrete types.
- **Content: entirely empty.** Conversations may be populated via a different mechanism (OperationIntrosTemplate, runtime wiring, separate asset bundles, or expression-based lookup), or the game may not yet use these fields in the current build.

The single-reference event fields (`VictoryEvent`, `FailureEvent`, `AbortEvent`) provide the only evidence that OperationTemplate references ConversationTemplate at all. Only `constructs_story1` has these populated, and all 3 point to the same conversation.

## Conclusion

This pass resolves the first priority from the next-support-type-candidates list. The answer is definitive: **no populated conversation arrays exist anywhere in the current OperationTemplate serialized inventory**. The structural shape is confirmed and matches legacy exactly, but content-level and polymorphism questions cannot be answered from this data.

This is a valid structural result. The empty state is itself useful knowledge — it tells us that operation-conversation wiring in MENACE likely does not go through these array fields in the current game build, despite the fields being declared.

### What this pass validates

- Structural existence of 4 conversation array fields and 3 event reference fields on OperationTemplate
- Field type stability across the complete inventory
- Exact match with legacy schema for field names, types, and array/reference classification
- Complete inventory sweep (8/8, no sampling gaps)

### What this pass does not validate

- Whether conversations are populated via a different mechanism (OperationIntrosTemplate, runtime wiring, other assets)
- Whether `ConversationTemplate[]` arrays would be polymorphic if populated (unlikely given no known subtypes)
- ConversationTemplate's own field set
- OperationIntrosTemplate's role as an alternative conversation delivery mechanism
- Runtime behaviour of any conversation/event field

## Next Step

The OperationTemplate conversation arrays question is now closed. The remaining priorities from the candidates list are:

1. **Deeper EntityTemplate.Items validation** — inspect ArmorTemplate/AccessoryTemplate field sets against legacy (richer structural content, guaranteed populated data)
2. **TemplateClassifier extension decision** — determine discovery strategy for non-Template-named concrete subtypes before template-patching design
3. **OperationIntrosTemplate** could be a dedicated pass if operation-conversation wiring becomes relevant to feature work
