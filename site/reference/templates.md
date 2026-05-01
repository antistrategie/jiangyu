# Templates (KDL)

Template patches let you tweak the data MENACE ships with: unit stats, skill parameters, weapon damage, perk trees, anything stored in a `DataTemplate`. Edits live in `templates/*.kdl` files in your project. The compiler reads them at build time and emits patches the loader applies at runtime.

Templates are MENACE's authored game data. There are dozens of subtypes (`EntityTemplate`, `UnitLeaderTemplate`, `WeaponTemplate`, `SkillTemplate`, `PerkTreeTemplate`, and so on), each with its own field schema. Patches apply at runtime. They don't modify your `resources.assets` file. Removing a mod restores vanilla behaviour next launch.

## Studio workflow

1. Open the [Template Browser](./studio.md#template-browser) pane.
2. Type the template id or type in the search box. Pick the template you want to edit. The browser shows its fields and current values in the detail panel.
3. Open the **Scaffold** dropdown in the detail panel and pick **Create Patch** or **Create Clone**. Studio generates the KDL snippet and either appends it to the template file you're currently editing, opens a picker so you can choose an existing `templates/*.kdl` file, or creates a new one. **Add patch to file…** and **Add clone to file…** in the same dropdown always go through the picker.
4. Studio opens the resulting KDL file in the code editor. Edit it directly to author the patch or clone.
5. [Compile](./studio.md#compile).

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
    set "AIRole" composite="RoleData" {
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

Inside a `patch` or `clone` block, operations modify fields on the targeted template.

| Operation                                            | Purpose                                              |
| ---------------------------------------------------- | ---------------------------------------------------- |
| `set "<field>" <value>`                              | Set a scalar, ref, enum, or composite field.         |
| `set "<field>" index=N <value>`                      | Set element N of a collection field.                 |
| `set "<field>" index=N { ... }`                      | Descend into element N to edit its sub-fields.       |
| `set "<field>" index=N handler="<X>" { ... }`        | Replace element N with a new constructed handler X.  |
| `append "<field>" <value>`                           | Append to a collection field.                        |
| `append "<field>" handler="<X>" { ... }`             | Construct a new handler X and append it.             |
| `insert "<field>" index=N <value>`                   | Insert into a collection field at position N.        |
| `insert "<field>" index=N handler="<X>" { ... }`     | Construct a new handler X and insert it at N.        |
| `remove "<field>" index=N`                           | Remove element N from a collection field.            |
| `clear "<field>"`                                    | Empty a collection field. Composes with `append` for "replace the whole list". |

Indexes are zero-based. `append` doesn't take an `index=` property; use `insert` for positional writes. `clear` takes neither index nor value.

## Field paths

Field paths navigate into nested members:

| Form                       | Meaning                                              |
| -------------------------- | ---------------------------------------------------- |
| `Properties.Accuracy`      | Dotted member access on a single object.             |
| `InitialAttributes`        | Whole-collection field (with `index=N` for elements).|

## Descent: editing inside a collection element

When you need to edit fields *inside* an element of a collection, wrap the inner edits in a `set "<field>" index=N { ... }` block. The outer `set` carries no value of its own; it just navigates. Inner directives operate on the element's own fields.

```kdl
patch "EntityTemplate" "player_squad.darby" {
    set "Properties" index=0 {
        set "Accuracy" 80.0
        set "AccuracyMult" 1.5
    }
}
```

Each inner `set` flattens to a single compiled patch op behind the scenes; the descent block is purely an authoring shape that keeps deeply-indexed paths readable.

### Polymorphic descent: type=

When the collection's declared element type is an abstract base with multiple concrete subclasses (for example, `SkillTemplate.EventHandlers` is `List<SkillEventHandlerTemplate>` but the actual elements are `Attack`, `AddSkill`, `ChangeProperty`, etc.), the compiler can't see the concrete subclass's fields from the abstract base alone. Add `type="<ConcreteType>"` to declare which subclass lives at this slot:

```kdl
patch "PerkTemplate" "perk.unique_darby_high_value_targets" {
    set "EventHandlers" index=0 type="AddSkill" {
        set "ShowHUDText" #true
    }
}
```

The `type=` hint is required at any polymorphic-abstract descent boundary; the compiler errors loudly when it's missing, naming the available subclasses. For non-polymorphic collections (where the element type is concrete and unambiguous), `type=` is unnecessary.

Descent blocks may nest: each inner `set "<field>" index=N type="..." { ... }` works the same way, descending one level further.

## Construction: adding new collection elements

To add a brand-new element to a polymorphic-reference collection (most commonly an event handler on a skill or perk), use `handler="<SubtypeName>" { ... }` on `append`, `insert`, or `set` (with `index=`). The constructed element gets a fresh `ScriptableObject` instance, populated with the inner field values, and pushed/inserted/replaced into the named collection at apply time.

```kdl
patch "PerkTemplate" "perk.unique_darby_high_value_targets" {
    append "EventHandlers" handler="AddSkill" {
        set "Event" enum="AddEvent" "OnAttack"
        set "SkillToAdd" ref="SkillTemplate" "effect.bleeding"
        set "ShowHUDText" #true
    }
}
```

Differences vs. descent:

- **`type=` on `set` with index** descends into an *existing* element to edit its fields in place. The element keeps its identity.
- **`handler=` on `append`, `insert`, or `set` with index** constructs a *new* element. On `set` with `index=`, the old element at N is replaced; on `append` and `insert`, no existing element is disturbed.

Inner `set` directives inside a `handler=` block configure top-level fields of the constructed element — scalars, refs, enums, composites. Indexed writes (`set "TargetTags" index=0 ...`) and further descent are not supported inside the construction block; use a follow-up patch with descent against the now-existing element if you need to populate a list-typed field on it.

The `handler=` subtype name is required when the array's element type is abstract polymorphic (the common case for event handlers). It can be omitted when the element type is monomorphic (the array's declared element type is the only construction option). The compiler errors when the subtype is missing for a polymorphic destination, when the named subtype isn't a subclass of the destination's element type, or when an inner field doesn't exist on the constructed type.

## Value kinds

A value at the end of a `set`, `append`, or `insert` line is one of:

| Kind                | Syntax                                                   | Example                                                  |
| ------------------- | -------------------------------------------------------- | -------------------------------------------------------- |
| Scalar number       | bare number                                              | `set "HudYOffsetScale" 2.0`                              |
| Boolean             | `#true` or `#false`                                      | `set "AvoidOpponents" #true`                             |
| String              | quoted string                                            | `set "InitialPerk" "perk.assassin"`                      |
| Template reference  | `ref="<TemplateType>" "<templateId>"`                    | `append "PerkTrees" ref="PerkTreeTemplate" "perk_tree.tech"` |
| Enum                | `enum="<EnumType>" "<value>"`                            | `set "Tier" enum="PerkTier" "Advanced"`                  |
| Composite           | `composite="<TypeName>" { ...nested set ops... }`        | inline value-type composite (e.g. `RoleData`)            |
| Handler construction | `handler="<SubtypeName>" { ...nested set ops... }`      | construct a new ScriptableObject element (see above)     |

`composite=` builds an inline value embedded in the parent's serialised data; `handler=` constructs a separate ScriptableObject asset that the parent references via PPtr. The two are dispatched by the runtime based on the value kind, not just the syntax.

The compiler infers numeric width (Byte, Int32, Single) from the destination field's type. For polymorphic destinations (an abstract base type with multiple concrete subclasses), specify the type explicitly via `ref=`, `composite=`, `handler=`, or `type=` as appropriate.

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

`templates inspect --output text` is the scan-friendly view; the default JSON output is for scripting. Pass `--with-mod <project-path>` to inspect the effective template state after your project's clones and patches apply, before you launch MENACE.

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

Some fields are flagged as Odin-only in the inspect output; the compiler rejects writes to them.

## CLI alternative

```sh
jiangyu templates index
jiangyu templates query EntityTemplate.Properties.Accuracy
jiangyu compile
```

`templates query` is a jq-like navigator that emits copy-pasteable KDL snippets for leaf fields.
