# Templates (KDL)

Template patches let you tweak the data MENACE ships with: unit stats, skill parameters, weapon damage, perk trees, anything stored in a `DataTemplate`. Edits live in `templates/*.kdl` files in your project. The compiler reads them at build time and emits patches the loader applies at runtime.

Templates are MENACE's authored game data. There are dozens of subtypes (`EntityTemplate`, `UnitLeaderTemplate`, `WeaponTemplate`, `SkillTemplate`, `PerkTreeTemplate`, and so on), each with its own field schema. Patches apply at runtime; they don't modify your `resources.assets` file. Removing a mod restores vanilla behaviour next launch.

## Studio workflow

1. Open the [Template Browser](./studio.md#template-browser) pane.
2. Type the template id or type in the search box. Pick the template you want to edit.
3. Studio opens the template in the [Template Visual Editor](./studio.md#template-visual-editor). Switch to the Source tab to author the same patch as raw KDL text. Both modes round-trip, so edits in one show up in the other.
4. Make your changes. Studio saves them into `templates/<some-name>.kdl` in your project.
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

| Operation                                  | Purpose                                              |
| ------------------------------------------ | ---------------------------------------------------- |
| `set "<field>" <value>`                    | Set a scalar, ref, enum, or composite field.         |
| `set "<field>" index=N <value>`            | Set element N of a collection field.                 |
| `append "<field>" <value>`                 | Append to a collection field.                        |
| `insert "<field>" index=N <value>`         | Insert into a collection field at position N.        |
| `remove "<field>" index=N`                 | Remove element N from a collection field.            |

Indexes are zero-based. `append` doesn't take an `index=` property; use `insert` for positional writes.

## Field paths

Field paths navigate into nested members and collection elements:

| Form                       | Meaning                                              |
| -------------------------- | ---------------------------------------------------- |
| `Properties.Accuracy`      | Dotted member access.                                |
| `Skills[0]`                | Indexer on a collection (zero-based).                |
| `Skills[0].Uses`           | Combined: indexer then dotted member.                |
| `InitialAttributes[3]`     | Element 3 of a collection.                           |

The `[N]` indexer form is interchangeable with the `index=N` property on a `set` operation. Studio's visual editor outputs the property form; both parse identically.

## Value kinds

A value at the end of a `set`, `append`, or `insert` line is one of:

| Kind                | Syntax                                                   | Example                                                  |
| ------------------- | -------------------------------------------------------- | -------------------------------------------------------- |
| Scalar number       | bare number                                              | `set "HudYOffsetScale" 2.0`                              |
| Boolean             | `#true` or `#false`                                      | `set "AvoidOpponents" #true`                             |
| String              | quoted string                                            | `set "InitialPerk" "perk.assassin"`                      |
| Template reference  | `ref="<TemplateType>" "<templateId>"`                    | `append "PerkTrees" ref="PerkTreeTemplate" "perk_tree.tech"` |
| Enum                | `enum="<EnumType>" "<value>"`                            | `set "Tier" enum="PerkTier" "Advanced"`                  |
| Composite           | `composite="<TypeName>" { ...nested set ops... }`        | see the `AIRole` example above                           |

The compiler infers numeric width (Byte, Int32, Single) from the destination field's type. For polymorphic destinations (an abstract base type with multiple concrete subclasses), specify the type explicitly via `ref=` or `composite=`. Otherwise the type is implicit.

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

`templates query` is a jq-like navigator that emits copy-pasteable KDL snippets for leaf fields. Studio is the recommended surface for authoring; the CLI is intended for build pipelines and scripting.
