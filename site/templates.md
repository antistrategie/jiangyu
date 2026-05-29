# Templates (KDL)

Template patches let you tweak the data MENACE ships with: unit stats, skill parameters, weapon damage, perk trees, anything stored in a `DataTemplate`. Edits live in `templates/*.kdl` files in your project. The compiler reads them at build time and emits patches the loader applies at runtime.

Templates are MENACE's authored game data. There are dozens of subtypes (`EntityTemplate`, `UnitLeaderTemplate`, `WeaponTemplate`, `SkillTemplate`, `PerkTreeTemplate`, and so on), each with its own field schema. Patches apply at runtime. They don't modify your `resources.assets` file. Removing a mod restores vanilla behaviour next launch.

## Studio workflow

1. Open the [Template Browser](/studio#template-browser) pane.
2. Type the template id or type in the search box. Pick the template you want to edit. The browser shows its fields and current values in the detail panel.
3. Open the **Scaffold** dropdown in the detail panel and pick **Create Patch** or **Create Clone**. Studio generates the KDL snippet and either appends it to the template file you're currently editing, opens a picker so you can choose an existing `templates/*.kdl` file, or creates a new one. **Add patch to file…** and **Add clone to file…** in the same dropdown always go through the picker.
4. Studio opens the resulting KDL file in the code editor. Edit it directly to author the patch or clone.
5. [Compile](/studio#compile).

## File layout

```text
templates/<any-name>.kdl
templates/subdir/<any-name>.kdl
```

Files can be organised any way you like. The compiler walks the entire `templates/` tree recursively and parses every `.kdl` file. Convention-wise, naming files by the template id they patch (`unit_leader-darby.kdl`, `weapon-rifle.kdl`) keeps things scannable.

Each template id can be patched in **at most one file** across the project. Cloning the same id twice, or patching it in two files, is a compile-time error.

## Patches

A `patch` block targets a specific template by type and id. Inside the block you list operations.

```kdl
patch "EntityTemplate" "player_squad.darby" {
    set "HudYOffsetScale" 2.0
    set "AIRole" {
        set "AvoidOpponents" #true
    }
}
```

The two arguments after `patch` are the template's subtype name and its serialised `m_ID`.

## Clones

A `clone` block creates a new template by deep-copying an existing one, then registers the copy under a fresh id. Operations inside the block apply to the clone, not to the source.

```kdl
clone "UnitLeaderTemplate" from="squad_leader.darby" id="squad_leader.darby_alt" {
    set "InitialAttributes" index=4 0
    set "InitialPerk" "perk.assassin"
}
```

Clones run before patches at load time, so the clone's id is targetable by `ref="..."` in subsequent patches and by other game systems that look up templates by id.

## Operations

Inside a `patch` or `clone` block, operations modify fields on the targeted template. A patch is a list of operations rather than a desired final state: `set "Damage" 50` only changes `Damage`; `append "Perks" ref="..."` only adds an entry. This keeps each modder's intent intact when patches from different mods stack (see [Composition](#composition-across-installed-mods)).

For the apply-order model (clones before patches, source order within a template) and how operations compose conceptually, see [Concepts](/concepts/#template-operations).

| Operation                                            | Purpose                                              |
| ---------------------------------------------------- | ---------------------------------------------------- |
| `set "<field>" <value>`                              | Set a scalar, ref, enum, or asset field.             |
| `set "<field>" index=N <value>`                      | Set element N of a collection field.                 |
| `set "<field>" index=N { ... }`                      | Descend into element N to edit its sub-fields. The element's type is inferred. |
| `set "<field>" index=N type="<X>" { ... }`           | Construct a fresh X and replace element N.           |
| `set "<field>" type="<X>" { ... }`                   | Construct a fresh X for a value or polymorphic-scalar field. |
| `append "<field>" <value>`                           | Append to a collection field.                        |
| `append "<field>" type="<X>" { ... }`                | Construct a new X and append it.                     |
| `insert "<field>" index=N <value>`                   | Insert into a collection field at position N.        |
| `insert "<field>" index=N type="<X>" { ... }`        | Construct a new X and insert it at N.                |
| `remove "<field>" index=N`                           | Remove element N from a collection field.            |
| `clear "<field>"`                                    | Empty a collection field. Composes with `append` for "replace the whole list". |

Indexes are zero-based. `append` doesn't take an `index=` property, so use `insert` for positional writes. `clear` takes neither index nor value.



## Composition across installed mods

The loader merges patches from all installed mods before applying. Mods load in lexical folder-name order.

- **`set` dedups by field path.** Two installed mods setting the same field on the same template: later-loaded mod wins, earlier is dropped, and the loader logs an `Override template patch ...` warning naming both mods.
- **`append`, `insert`, `remove`, `clear` accumulate.** No deduplication, and all ops apply in load order. Three mods each appending a perk to the same tree leave three perks added.

Collection-style edits compose without per-mod compatibility patches. Genuine scalar conflicts become explicit warnings rather than silent overrides.

Within a single project, `(templateType, templateId)` collisions remain a hard error at compile time (see [File layout](#file-layout)).

## Field paths

Field paths navigate into nested members:

| Form                       | Meaning                                              |
| -------------------------- | ---------------------------------------------------- |
| `Properties.Accuracy`      | Dotted member access on a single object.             |
| `InitialAttributes`        | Whole-collection field (with `index=N` for elements).|

## Descent: editing inside a collection element

When you need to edit fields *inside* an element of a collection, wrap the inner edits in a `set "<field>" index=N { ... }` block. The outer `set` carries no value of its own and just navigates, while inner directives operate on the element's own fields.

```kdl
patch "EntityTemplate" "player_squad.darby" {
    set "Properties" index=0 {
        set "Accuracy" 80.0
        set "AccuracyMult" 1.5
    }
}
```

The descent block is purely an authoring shape that keeps deeply-indexed paths readable.

### Editing a polymorphic element

Editing into an element needs no type annotation, even when the collection's declared element type is an abstract base with multiple concrete subclasses (for example, `SkillTemplate.EventHandlers` is `List<SkillEventHandlerTemplate>` but the actual elements are `Attack`, `AddSkill`, `ChangeProperty`, etc.). The descent edits the element already at that slot, so the concrete subtype is inferred:

```kdl
patch "PerkTemplate" "perk.unique_darby_high_value_targets" {
    set "EventHandlers" index=0 {
        set "ShowHUDText" #true
    }
}
```

The runtime reads the live element's concrete type and edits it, leaving every other field untouched. The compiler validates each inner field against the element base's concrete subtypes, so a typo on a field that exists on no subtype is still caught. To replace the element with a freshly-constructed one instead of editing it, add `type="<X>"` (see [Construction with `type=`](#construction-adding-new-collection-elements)).

Descent blocks may nest: each inner `set "<field>" index=N { ... }` works the same way, descending one level further.

## Construction: adding new collection elements

To add a brand-new element to a polymorphic-reference collection (most commonly an event handler on a skill or perk), use `type="<SubtypeName>" { ... }` on `append` or `insert`. The inner block sets the new element's fields.

```kdl
patch "PerkTemplate" "perk.unique_darby_high_value_targets" {
    append "EventHandlers" type="AddSkill" {
        set "Event" enum="AddEvent" "OnAttack"
        set "SkillToAdd" ref="SkillTemplate" "effect.bleeding"
        set "ShowHUDText" #true
    }
}
```

### `type=`: name the type to construct

`type="X"` is the one construction keyword. It names the concrete type to build, and the compiler picks how to store it from the destination field: a constructed ScriptableObject for a polymorphic-reference element (an event handler), an inline value for a concrete struct field (`RoleData`, `LocalizedLine`), or a tagged string for a tagged-string field (see [Tagged-string fields](#tagged-string-polymorphic-fields)). `type=` always constructs a fresh instance:

- `append "F" type="X" { ... }` / `insert "F" index=N type="X" { ... }`: construct a fresh X and add it to the collection.
- `set "F" index=N type="X" { ... }`: construct a fresh X and replace element N (even when N already holds an X). To edit element N in place, drop `type=` and use a plain descent block.
- `set "ScalarField" type="X" { ... }`: construct a fresh X and assign it to a value or polymorphic-scalar field (an Odin-routed interface such as `Attack.DamageFilterCondition`).

The inner `set` directives provide the new instance's fields and everything else takes its type default (`0`, `null`, empty list, …). Nothing carries over from whatever was there, so set every field the element needs to function, including its trigger. A passive perk's `AddSkill`, for example, needs `set "Event" enum="AddEvent" "OnMissionStart"` or it never grants the skill. To keep the existing fields and change only a few, edit in place with a plain descent block instead.

Inner directives inside a `type=` block accept the full `set` / `append` / `insert` / `remove` / `clear` vocabulary against the new instance, so you can author "construct an `AddSkill` and append two `PropertyChange` entries to its `Properties` list" inline rather than splitting into separate patches.

The `type=` subtype name is required when the destination's element type is abstract polymorphic (the common case for event handlers). It can be omitted when the element type is monomorphic (a child block with no `type=` infers the only construction option). The compiler errors when the subtype is missing for a polymorphic destination, when the named subtype isn't a subclass of the destination's element type, or when an inner field doesn't exist on the constructed type.

### `from=`: inherit an existing element's fields

`append` and `insert` accept `from="<name>"` to seed the new element from a named entry already in the destination collection. Inner directives apply on top, so only the fields you want to change need to be listed:

```kdl
patch "SoundBank" "weapons_soundbank" {
    append "sounds" from="aimed_shot" {
        set "id" "custom_rifle_fire"
        set "name" "custom_rifle_fire"
        clear "variations"
        append "variations" {
            set "clip" asset="weapons/custom_rifle/fire_01"
        }
    }
}
```

This copies every field of the `aimed_shot` sound (volume, pitch, retrigger mode, distance falloff, etc.) and then overrides `id`, `name`, and `variations` on the copy. Without `from=`, every scalar field defaults to zero and the modder has to remember to set sensible playback parameters. The `from=` name matches the source element's `name` property; the destination type doesn't need to be named explicitly when only one type is possible.

## Value kinds

A value at the end of a `set`, `append`, or `insert` line is one of:

| Kind                | Syntax                                                   | Example                                                  |
| ------------------- | -------------------------------------------------------- | -------------------------------------------------------- |
| Scalar number       | bare number                                              | `set "HudYOffsetScale" 2.0`                              |
| Boolean             | `#true` or `#false`                                      | `set "AvoidOpponents" #true`                             |
| String              | quoted string, or triple-quoted multi-line               | `set "InitialPerk" "perk.assassin"`                      |
| Template reference  | `ref="<TemplateType>" "<templateId>"`                    | `append "PerkTrees" ref="PerkTreeTemplate" "perk_tree.tech"` |
| Enum                | `enum="<EnumType>" "<value>"`                            | `set "Tier" enum="PerkTier" "Advanced"`                  |
| Construction        | `type="<TypeName>" { ...nested set ops... }`             | build a value, element, or tagged string (see above)     |
| Null                | `#null`                                                  | `set "CustomHead" #null` (clear a reference field)       |

`type=` names the type to build and the compiler picks the storage mechanism from the destination field: an inline value for a concrete struct (`RoleData`, `LocalizedLine`), a constructed ScriptableObject for a polymorphic-reference element (event handlers on skills and perks), or a tagged string for a tagged-string field.

When the destination type is unambiguous (a value-type field like `LocalizedLine`, or a monomorphic list element), the `type="X"` declaration can be omitted. Studio's visual editor emits the omitted form for these cases:

```kdl
patch "UnitLeaderTemplate" "squad_leader.darby_clone" {
    set "UnitTitle" {
        set "m_DefaultTranslation" "Tactical Doll"
    }
}
```

The compiler infers the concrete type from the destination field. Polymorphic destinations (an abstract base with multiple concrete subclasses) still require explicit `type=` so the chosen subtype is unambiguous. The validator lists the candidates when the hint is missing.

`#null` clears a reference-typed scalar field (GameObject, ScriptableObject, MonoBehaviour, etc.) so the runtime falls back to its default-when-null behaviour. Useful on cloned templates where the source's field holds an overlay you don't want; e.g. dropping a soldier clone's `CustomHead` so the body mesh's own head shows through. Value-typed fields (numbers, enums, structs) reject `#null` at compile time.

Strings containing newlines emit as KDL v2 triple-quoted multi-line literals. The visual editor flips to a textarea when a value has any newline in it, and Studio's source view round-trips both forms without rewriting:

```kdl
set "Description" """
    A first line.
    A second line.

    A fourth, after a blank line.
    """
```

The leading whitespace on the closing `"""` defines the common indent stripped from every line on re-parse. Single-line strings stay on the standard `"text"` form, so most patches still diff compactly.

The compiler infers numeric width (Byte, Int32, Single) from the destination field's type. For polymorphic destinations (an abstract base type with multiple concrete subclasses), specify the type explicitly via `ref=` or `type=` as appropriate.

## Tagged-string polymorphic fields

Some MENACE fields store polymorphic typed values as strings of the form `"DISCRIMINATOR|{json}"`, paired with a sibling typed field that the game rebuilds on load. `ConversationTemplate`'s node tree and role-requirement list, and `ActionConversationNode`'s sub-action, all use this convention.

Author them with the same `type="X"` op. The `X` is the discriminator (`ACTION`, `SAY`, `HasOneTag`, `SetFlag`, ...), which Jiangyu maps to the concrete CLR subtype via the polymorphic family's naming convention. The compiler builds the typed instance, JSON-serialises it, and prefixes the discriminator before storing the result.

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
        append "m_SerializedNodes" type="SAY" {
            set "Sound" {
                set "bankId" "tactical_barks_voymastina_va"
                set "itemId" "voymastina_click_bark_test"
            }
            set "RoleGuid" "Entity"
            set "Text" "Roger."
        }
        append "m_SerializedNodes" type="EMPTY" {}
    }
}
```

Recursion is automatic. The inner `type="SetFlag"` packs first to `"SetFlag|{...}"`, assigns to the typed `ActionConversationNode.m_SerAction`, then the outer node packs to `"ACTION|{"Guid":...,"m_SerAction":"SetFlag|{...}"}"`. The raw string form (`set "F" "TYPE|{\"...\":\"...\"}"`) still works as an escape hatch when a discriminator isn't recognised yet or a modder pastes from a decompiled asset.

### Ergonomic auto-fills

Four common omissions are filled automatically:

- **Node Guids.** Every `BaseConversationNode` subtype and `ConversationNodeContainer` carries an `int Guid`. When omitted, the compiler emits `FNV-1a("{patchId}#node_{counter}")` — stable across rebuilds, distinct from the source's Guids. Modders can still write `set "Guid" N` to force a value.
- **Clone identity.** `clone "SoundBank" id="X"` implies `set "bankId" "X"`. `clone "ConversationTemplate" id="X"` implies `set "Path" "X"`. Skipped when the modder set the field explicitly.
- **`Stem.Sound.id` from `name`.** A `Stem.Sound` composite with `set "name" "X"` and no `set "id"` defaults `id` to FNV-1a(`X`). Within-bank uniqueness only requires distinct names.
- **`VariationCopyCount` sync.** `VariationConversationNode`'s parallel `VariationCopyCount` array is padded with `1`s to match the number of `append "Variations"` ops. Branches without a matching copy-count entry play silently in MENACE's engine.

### Symbolic role references

`SAY.RoleGuid` accepts a role name string instead of the int. The compiler resolves it against the source conversation's `Roles[].RoleName` via the asset index (rebuild with `jiangyu assets index` after a MENACE update so the latest Guids are recorded). For a clone, lookup follows the clone-to-source chain.

```kdl
set "RoleGuid" "Entity"   // resolves against source.Roles[].RoleName
set "RoleGuid" 1248015120 // literal int still works
```

## Discovering templates

Studio's Template Browser is the fastest way to find a template id and inspect its current values, but the same flow exists on the CLI:

```sh
jiangyu templates index
jiangyu templates list                                    # all template subtypes
jiangyu templates list --type UnitLeaderTemplate          # all ids of one subtype
jiangyu templates search darby                            # substring search
jiangyu templates inspect --type UnitLeaderTemplate \
    --name squad_leader.darby --output text
```

`templates inspect --output text` is the scan-friendly view. The default JSON output is for scripting. Pass `--with-mod <project-path>` to inspect the effective template state after your project's clones and patches apply, before you launch MENACE.

## Compile-time errors

Compile refuses the build, with a clear message, when:

- A KDL file fails to parse. The error names the file and line.
- The template id targeted by a `patch` or `clone` doesn't exist in the index.
- A `clone` id collides with an existing template id or another clone in the project.
- The same `(type, id)` is patched in two different files.
- A field path doesn't resolve on the target template's schema.
- A value kind doesn't match the destination field type (for example, writing a `String` into a `Single` field).
- An `enum=` value isn't a member of the named enum.
- A `ref=` target isn't a template of the declared type.

Some fields are flagged as Odin-only in the inspect output, and the compiler rejects writes to them.

## CLI alternative

```sh
jiangyu templates index
jiangyu templates query EntityTemplate.Properties.Accuracy
jiangyu compile
```

`templates query` is a jq-like navigator that emits copy-pasteable KDL snippets for leaf fields.

## Formatting

`jiangyu templates format` rewrites every `*.kdl` under `templates/` (or a single file path) to the canonical form Studio emits when you save in the visual editor. It runs the same parse → validate → normalise → serialise pipeline as the editor:

- Strips redundant `type=` / `ref=` attributes on monomorphic destinations.
- Resolves symbolic Conversation role names into the numeric guids the game stores.
- Coerces shorthand value forms the validator accepts.
- Round-trips leading and inline `//` comments alongside the code.

Use `--check` in CI: it prints which files would change and exits non-zero if any do, without writing.
