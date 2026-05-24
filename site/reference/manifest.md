# Manifest (`jiangyu.json`)

Every mod has a `jiangyu.json` at its root. It carries the mod's identity (name, version, author) and its dependency list. Replacements aren't listed in the manifest. They're discovered by convention from `assets/replacements/`. Template patches aren't authored in the manifest either, but live in `templates/*.kdl`.

## Default scaffold

`jiangyu init` and Studio's "New project" dialog call the same scaffold path. Both write:

```json
{
  "name": "MyMod",
  "version": "0.1.0",
  "depends": ["Jiangyu >= 1.0.0"]
}
```

`name` defaults to the project directory name (or to the name typed into the New project dialog). The scaffold also writes a `.gitignore` that excludes `.jiangyu/` and `compiled/`.

## Modder-authored fields

| Field             | Type       | Required | Default   | Notes                                                  |
| ----------------- | ---------- | -------- | --------- | ------------------------------------------------------ |
| `name`            | `string`   | yes      | (none)    | Used as the dependency-resolution identity.            |
| `version`         | `string`   | no       | `"0.1.0"` | Free-form text, not parsed or enforced.                |
| `author`          | `string`   | no       | (none)    | Display only.                                          |
| `description`     | `string`   | no       | (none)    | Display only.                                          |
| `depends`         | `string[]` | no       | (none)    | See [Dependencies](#dependencies).                     |
| `importedPrefabs` | `string[]` | no       | (none)    | See [Imported prefabs](#imported-prefabs).             |

Unknown fields are ignored on read.

## Dependencies

Each entry in `depends` is `<name>` or `<name> <op> <constraint>`, where `<op>` is one of `>=`, `<=`, `==`, `!=`, `>`, `<`, `=`.

```json
{
  "depends": [
    "Jiangyu >= 1.0.0",
    "AnotherMod"
  ]
}
```

**Only the name is enforced.** The constraint is parsed and held in memory, but the loader logs a warning rather than blocking on version mismatch:

> `Mod 'X' dependency 'Y >= 1.0.0' includes a version constraint that is not enforced yet; required presence only.`

Names match against other mods' `name` fields (case-sensitive). The literal name `Jiangyu` always satisfies because it represents the loader itself.

::: warning Dependency identity is provisional
`depends` resolves against display `name` until Jiangyu defines a stable machine-readable mod ID. Renaming a mod renames its dependency identity. Treat names as long-lived.
:::

## Imported prefabs

`importedPrefabs` lists vanilla game prefabs that the mod's authored content references at compile time (typically shaders, materials, or avatars donated by [`BakeHumanoid`](/assets/additions/prefabs) or `BakeWeapon`). Each entry is the asset name surfaced by `jiangyu assets search`, the same value you would pass to [`jiangyu unity import-prefab`](/reference/cli#jiangyu-unity-import-prefab-name).

```json
{
  "importedPrefabs": [
    "rmc_default_female_soldier_2",
    "arc_assault_rifle_t1"
  ]
}
```

On `jiangyu compile`, each listed name is checked against `unity/Assets/Imported/<name>/`. Missing directories are ripped from the modder's game install in one shared pass. Present directories are skipped. The combined effect: a fresh clone can run `jiangyu compile` directly without a separate import step, and the committed repo never needs to ship derivative host assets.

Compile also walks `unity/Assets/` for GUID references into `Imported/<X>/` and fails if `X` is not declared. This catches the silent fallback to Unity's pink "missing shader" material when a contributor bakes against a host rip but forgets to update the manifest.

`unity/Assets/Imported/` should be gitignored. Authored content (PMX-derived models, weapon meshes, sprites the modder created) belongs elsewhere under `unity/Assets/` and IS committed.

## Compiler-owned fields

The compiler writes additional fields into `compiled/jiangyu.json`. **Don't author these by hand.** The next compile will overwrite them.

| Field               | Source                                                                       |
| ------------------- | ---------------------------------------------------------------------------- |
| `meshes`            | mesh compilation; one entry per replaced skinned-renderer path               |
| `templatePatches`   | emitted from `templates/*.kdl`                                               |
| `templateClones`    | emitted from `templates/*.kdl`                                               |
| `additionPrefabs`   | logical names of prefab addition bundles staged into the compiled output (see [Prefabs](/assets/additions/prefabs)) |

`compiled/jiangyu.json` ships inside the compiled mod folder. Modders read it for debugging compiled output, never edit it.

## Loader validation

When MENACE starts, the loader scans `Mods/**/jiangyu.json`. A mod is blocked (its bundles aren't loaded, its templates don't apply) when:

- The manifest is missing or unreadable.
- `name` is empty or missing.
- A `depends` entry is empty or doesn't parse against the `<name> <op> <constraint>` grammar.
- A required mod (by `name`) isn't present in `Mods/`.
- Two mod folders share the same `name`. Both copies are blocked, with an error naming every duplicate location.

Mods with a valid manifest but no `.bundle` files are treated as **present for dependency checks**. This is useful for "metadata only" mods that consist entirely of template patches.

## Load order

Mods load in lexical order of their folder paths under `Mods/`. When two mods replace the same asset or patch the same template field, **the later-loaded mod wins**, with a warning logged.
