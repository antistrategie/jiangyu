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

## Create

::: warning Reach for `clone` first
You almost never want `create`. A `create` starts from a blank template with every field at its type default, so you have to set everything the game expects and it is easy to miss one, leaving a broken or inert template. `clone` deep-copies a real template instead, so the new id inherits all of the source's Inspector-baked defaults and you set only what differs. Use `create` only when no existing template of that type is a sensible base.
:::

A `create` block makes a fresh template of a given type and registers it under a new id, with no source to copy from. It takes no `from=`, and everything is set inside the block.

```kdl
create "PerkTemplate" id="my_mod.brand_new_perk" {
    // nothing is inherited: set every field the game needs
}
```

Like clones, creates run before patches, so the new id is targetable by `ref="..."` in subsequent patches and by other game systems that look up templates by id.

## Operations

Inside a `patch` or `clone` block, operations modify fields on the targeted template. A patch is a list of operations rather than a desired final state: `set "Damage" 50` only changes `Damage`, and `append "Perks" ref="..."` only adds an entry. This keeps each modder's intent intact when patches from different mods stack (see [Composition](#composition-across-installed-mods)).

For the apply-order model (clones before patches, source order within a template) and how operations compose conceptually, see [Concepts](/concepts/#template-operations).

| Operation                                            | Purpose                                              |
| ---------------------------------------------------- | ---------------------------------------------------- |
| `set "<field>" <value>`                              | Set a scalar, ref, enum, or asset field.             |
| `set "<field>" { ... }`                              | Descend into the object/struct at the field to edit its sub-fields in place. |
| `set "<field>" index=N <value>`                      | Set element N of a collection field.                 |
| `set "<field>" index=N { ... }`                      | Descend into element N to edit its sub-fields. The element's type is inferred. |
| `set "<field>" cell="r,c" <value>`                   | Set one cell of a multi-dimensional array field. |
| `set "<field>" type="<X>" { ... }`                   | Construct a fresh X for a **polymorphic** field (names the subtype). |
| `set "<field>" index=N type="<X>" { ... }`           | Construct a fresh X and replace **polymorphic** element N.            |
| `append "<field>" <value>`                           | Append to a collection field.                        |
| `append "<field>" type="<X>" { ... }`                | Construct a new X and append it.                     |
| `insert "<field>" index=N <value>`                   | Insert into a collection field at position N.        |
| `insert "<field>" index=N type="<X>" { ... }`        | Construct a new X and insert it at N.                |
| `remove "<field>" index=N`                           | Remove element N from a `List<T>` field.             |
| `remove "<field>" <value>`                           | Remove a matching entry from a set-style (`HashSet`) field. |
| `clear "<field>"`                                    | Empty a collection, or reset an inline object to its defaults. Composes with `append` ("replace the whole list") or with a descent block ("reset, then set a few fields"). |

Indexes are zero-based. `append` doesn't take an `index=` property, so use `insert` for positional writes. `clear` takes neither index nor value.

`type=` names a concrete subtype and only applies where there's a choice to make, a **polymorphic** field or list element. On a monomorphic field `type=` is an error: edit it in place with `set "<field>" { ... }`, reset it with `clear`, or replace a monomorphic element with `remove` + `insert`. References (`GameObject`, `ScriptableObject`, another template) are nulled with `#null`, not `clear`.

## Composition across installed mods

The loader merges patches from all installed mods before applying. Mods load in lexical folder-name order.

- **`set` dedups by field path.** Two installed mods setting the same field on the same template: later-loaded mod wins, earlier is dropped, and the loader logs an `Override template patch ...` warning naming both mods.
- **`append`, `insert`, `remove`, `clear` accumulate.** No deduplication, and all ops apply in load order. Three mods each appending a perk to the same tree leave three perks added.

Collection-style edits compose without per-mod compatibility patches. Genuine scalar conflicts become explicit warnings rather than silent overrides.

Within a single project, `(templateType, templateId)` collisions remain a hard error at compile time (see [File layout](#file-layout)).

## Field paths

A field name targets a member of the template:

| Form                       | Meaning                                              |
| -------------------------- | ---------------------------------------------------- |
| `HudYOffsetScale`          | A top-level scalar field.                            |
| `InitialAttributes`        | A whole-collection field (with `index=N` for elements).|

To reach fields *inside* a nested object or a collection element, use a [descent block](#descent-editing-inside-an-object-or-element) (`set "AIRole" { ... }`) rather than a dotted path.

## Descent: editing inside an object or element

When you need to edit fields *inside* a nested object or a collection element, wrap the inner edits in a descent block. The outer `set` carries no value of its own and just navigates, while inner directives operate in place on the target's own fields, and every other field is left untouched.

- `set "<field>" { ... }` descends into the **object/struct** at `<field>`.
- `set "<field>" index=N { ... }` descends into **element N** of a collection.

```kdl
patch "EntityTemplate" "player_squad.darby" {
    // Edit two fields of the inline AIRole object; the rest stay as they are.
    set "AIRole" {
        set "AvoidOpponents" #true
        set "SafetyScale" 2.0
    }
}
```

The descent block is purely an authoring shape: it edits the existing value in place. To build a fresh one instead, see [Construction](#construction) (`clear`, `remove`/`insert`, or `type=`).

### Editing a polymorphic element

Editing into an element needs no type annotation, even when the collection's declared element type is an abstract base with multiple concrete subclasses (for example, `SkillTemplate.EventHandlers` is `List<SkillEventHandlerTemplate>` but the actual elements are `Attack`, `AddSkill`, `ChangeProperty`, etc.). The descent edits the element already at that slot, so the concrete subtype is inferred:

```kdl
patch "PerkTemplate" "perk.unique_darby_high_value_targets" {
    set "EventHandlers" index=0 {
        set "ShowHUDText" #true
    }
}
```

The runtime reads the live element's concrete type and edits it, leaving every other field untouched. The compiler validates each inner field against the element base's concrete subtypes, so a typo on a field that exists on no subtype is still caught. To replace a polymorphic element with a freshly-constructed one instead of editing it, add `type="<X>"` (see [Construction](#construction)).

Descent blocks may nest: each inner `set "<field>" index=N { ... }` works the same way, descending one level further.

Value-type (struct) elements are the one exception: a `List<SomeStruct>` element can't be edited in place, so `set "<field>" index=N { ... }` into a struct element is rejected. Replace the whole element with `remove` + `insert` instead.

## Construction

Editing keeps what's there. **Construction** builds a fresh value. Which tool you reach for depends on whether there's a *type choice* to make.

### `type=`: pick a polymorphic subtype

`type="X"` names the concrete subtype to build, and only applies where there's a choice, a **polymorphic** field or list element (an abstract base with multiple concrete subclasses), or a tagged-string field. The most common case is adding an event handler:

```kdl
patch "PerkTemplate" "perk.unique_darby_high_value_targets" {
    append "EventHandlers" type="AddSkill" {
        set "Event" enum="AddEvent" "OnAttack"
        set "SkillToAdd" ref="SkillTemplate" "effect.bleeding"
        set "ShowHUDText" #true
    }
}
```

- `append "F" type="X" { ... }` / `insert "F" index=N type="X" { ... }`: build a fresh X and add it.
- `set "F" index=N type="X" { ... }`: build a fresh X and replace polymorphic element N.
- `set "F" type="X" { ... }`: build a fresh X for a polymorphic scalar field (an Odin-routed interface such as `Attack.DamageFilterCondition`).

The inner `set` directives provide the new instance's fields, and everything else takes its type default (`0`, `null`, empty list, …). Nothing carries over, so set every field the new instance needs to function, including its trigger. A passive perk's `AddSkill`, for example, needs `set "Event" enum="AddEvent" "OnMissionStart"` or it never grants the skill. Inner directives accept the full `set` / `append` / `insert` / `remove` / `clear` vocabulary against the new instance.

`type=` is an **error on a monomorphic destination**: there's no subtype to pick. The compiler also errors when the named subtype isn't a subclass of the destination's element type, or when an inner field doesn't exist on it.

### Monomorphic: `clear` and `remove`/`insert`

A monomorphic field has only one possible type, so there's nothing for `type=` to name. To build a fresh one:

- **Object/struct field**: `clear "F"` resets it to defaults, then a descent block sets the fields you want:
  ```kdl
  clear "AIRole"
  set "AIRole" { set "AvoidOpponents" #true }   // fresh RoleData, only AvoidOpponents changed
  ```
- **Collection element**: `remove "F" index=N` then `insert "F" index=N { ... }` (the element analogue of `clear` + `set`).
- **Append a monomorphic element**: `append "F" { ... }` infers the only element type, no `type=` needed.

`clear` is the mirror image of `type=`: it resets a **monomorphic** inline object, and is an **error on a polymorphic one**: there's no single default to pick, so reconstruct it with `type="<Subtype>"` instead. A **reference** field (a `GameObject`, `ScriptableObject`, or another template) is pointed with `ref=`/`asset=` and nulled with `#null`, so `clear` doesn't apply to it.

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

This copies every field of the `aimed_shot` sound (volume, pitch, retrigger mode, distance falloff, etc.) and then overrides `id`, `name`, and `variations` on the copy. Without `from=`, every scalar field defaults to zero and the modder has to remember to set sensible playback parameters. The `from=` name matches the source element's `name` property, and the destination type doesn't need to be named explicitly when only one type is possible.

## Value kinds

A value at the end of a `set`, `append`, or `insert` line is one of:

| Kind                | Syntax                                                   | Example                                                  |
| ------------------- | -------------------------------------------------------- | -------------------------------------------------------- |
| Scalar number       | bare number                                              | `set "HudYOffsetScale" 2.0`                              |
| Boolean             | `#true` or `#false`                                      | `set "AvoidOpponents" #true`                             |
| String              | quoted string, or triple-quoted multi-line               | `set "InitialPerk" "perk.assassin"`                      |
| Template reference  | `ref="<TemplateType>" "<templateId>"`                    | `append "PerkTrees" ref="PerkTreeTemplate" "perk_tree.tech"` |
| Enum                | `enum="<EnumType>" "<value>"`                            | `set "Tier" enum="PerkTier" "Advanced"`                  |
| Construction        | `type="<TypeName>" { ...nested set ops... }`             | build a polymorphic element/scalar or tagged string (see [Construction](#construction)) |
| Null                | `#null`                                                  | `set "CustomHead" #null` (null a reference field)        |

`type=` names a polymorphic subtype (or a tagged-string discriminator). On a monomorphic destination it's an error.

A bare child block with no `type=` is a **descent**: it edits the existing object/struct in place (see [Descent](#descent-editing-inside-an-object-or-element)). It's also what `append`/`insert` use to build a monomorphic element, where the element type is inferred from the destination:

```kdl
patch "UnitLeaderTemplate" "squad_leader.darby_clone" {
    set "UnitTitle" {
        set "m_DefaultTranslation" "Tactical Doll"
    }
}
```

Here `UnitTitle` is a monomorphic `LocalizedLine`, so the block edits it in place: `m_DefaultTranslation` changes and its other fields stay. (To wipe it first, `clear "UnitTitle"` then set the fields you want.)

`#null` clears a reference-typed scalar field (GameObject, ScriptableObject, MonoBehaviour, etc.) so the runtime falls back to its default-when-null behaviour. Useful on cloned templates where the source's field holds an overlay you don't want, for example dropping a soldier clone's `CustomHead` so the body mesh's own head shows through. Value-typed fields (numbers, enums, structs) reject `#null` at compile time.

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
        clear "m_SerializedNodes"
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

`set "Nodes" { ... }` edits the cloned container in place, so `clear "m_SerializedNodes"` first defines a fresh node list rather than appending after the source's nodes. Drop the `clear` to extend the cloned conversation instead.

Recursion is automatic. The inner `type="SetFlag"` packs first to `"SetFlag|{...}"`, assigns to the typed `ActionConversationNode.m_SerAction`, then the outer node packs to `"ACTION|{"Guid":...,"m_SerAction":"SetFlag|{...}"}"`. The raw string form (`set "F" "TYPE|{\"...\":\"...\"}"`) still works as an escape hatch when a discriminator isn't recognised yet or a modder pastes from a decompiled asset.

### Ergonomic auto-fills

Four common omissions are filled automatically:

- **Node Guids.** Every `BaseConversationNode` subtype and `ConversationNodeContainer` carries an `int Guid`. When omitted, the compiler emits `FNV-1a("{patchId}#node_{counter}")`, stable across rebuilds and distinct from the source's Guids. Leave it to the auto-fill: the guid is also the game's runtime node id (conversation jumps, save-state), so a hand-picked value risks colliding with another node's.
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

`templates inspect --output text` is the scan-friendly view. The default is `pretty`, and `--output json` is for scripting. Pass `--with-mod <project-path>` to inspect the effective template state after your project's clones and patches apply, before you launch MENACE.

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
