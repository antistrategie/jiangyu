# Manifest (`jiangyu.json`)

Every mod has a `jiangyu.json` at its root. It carries the mod's identity (name, version, author) and its dependency list. Replacements aren't listed in the manifest. They're discovered by convention from `assets/replacements/`. Template patches aren't authored in the manifest either, but live in `templates/*.kdl`.

## Default scaffold

`jiangyu init` and Studio's "New project" dialog call the same scaffold path. Both write:

```json
{
  "name": "MyMod",
  "version": "0.1.0"
}
```

`name` defaults to the project directory name (or to the name typed into the New project dialog). The scaffold also writes a `.gitignore` that excludes `.jiangyu/` and `compiled/`.

The Jiangyu requirement is not seeded into `depends`. The compiler stamps the toolchain version that built the mod into `compiledForJiangyu` (see [Compiler-owned fields](#compiler-owned-fields)), and the loader warns if it's newer than the installed loader. Add `"Jiangyu >= x.y.z"` to `depends` yourself only when you need a hard minimum.

## Modder-authored fields

| Field             | Type       | Required | Default   | Notes                                                  |
| ----------------- | ---------- | -------- | --------- | ------------------------------------------------------ |
| `name`            | `string`   | yes      | (none)    | Used as the dependency-resolution identity.            |
| `version`         | `string`   | no       | `"0.1.0"` | Semantic version. Other mods' constraints resolve against it. |
| `author`          | `string`   | no       | (none)    | Display only.                                          |
| `description`     | `string`   | no       | (none)    | Display only.                                          |
| `depends`         | `string[]` | no       | (none)    | See [Dependencies](#dependencies).                     |
| `conflicts`       | `string[]` | no       | (none)    | See [Conflicts](#conflicts).                           |
| `imports`         | `string[]` | no       | (none)    | See [Imported prefabs](#imported-prefabs).             |

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

Both presence and version are enforced. A mod is blocked when a required mod is absent, or present but failing the constraint (e.g. it requires `Base >= 1.0.0` but `Base` is `0.9.0`). A bare name with no constraint checks presence only. A constraint is compared as a [semantic version](https://semver.org); if either side can't be parsed as one, it falls back to a presence-only check.

Names match against other mods' `name` fields (case-sensitive). The literal name `Jiangyu` is the loader itself, resolved against the installed loader version, so `"Jiangyu >= 1.3.0"` is a hard floor on the loader. You rarely need to write it: the compiler already stamps `compiledForJiangyu` and the loader warns on a newer-than-installed build. Add an explicit floor only when your mod will not function below a known loader version.

::: warning Dependency identity is provisional
`depends` resolves against display `name` until Jiangyu defines a stable machine-readable mod ID. Renaming a mod renames its dependency identity. Treat names as long-lived.
:::

## Conflicts

`conflicts` uses the same `<name>` or `<name> <op> <constraint>` grammar as `depends`, but inverts the meaning: a mod is blocked when a named mod **is** present.

```json
{
  "conflicts": [
    "IncompatibleMod",
    "OtherMod < 2.0.0"
  ]
}
```

A bare name conflicts with any installed version. A constrained entry conflicts only with versions in the range, so `"OtherMod < 2.0.0"` lets `OtherMod` 2.0.0 and newer load alongside you. When a conflicting mod's version can't be parsed, a constrained conflict does not trigger (an unconfirmable range never blocks).

## Imported prefabs

`imports` lists vanilla game prefabs that the mod's authored content references at compile time (typically shaders, materials, or avatars donated by [`BakeHumanoid`](/assets/additions/prefabs) or `BakeWeapon`). Each entry is the asset name surfaced by `jiangyu assets search`, the same value you would pass to [`jiangyu unity import-prefab`](/reference/cli#jiangyu-unity-import-prefab-name).

```json
{
  "imports": [
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
| `meshes`            | mesh compilation, one entry per replaced skinned-renderer path               |
| `additionPrefabs`   | logical names of prefab addition bundles staged into the compiled output (see [Prefabs](/assets/additions/prefabs)) |
| `compiledForUnity`  | the game's Unity version stamped at compile time, so the loader can compare it to the running game and warn on a build mismatch |
| `compiledForJiangyu`| the Jiangyu toolchain version that built the mod; the loader warns when it's newer than the installed loader |

The compiled template program (the patch and clone directives emitted from `templates/*.kdl`) is **not** in the manifest. It ships beside it as `compiled/templates.json`, so `jiangyu.json` stays a small identity record the loader scans cheaply. A mod with no patches or clones ships no `templates.json`.

Both `compiled/jiangyu.json` and `compiled/templates.json` ship inside the compiled mod folder. Modders read them for debugging compiled output, never edit them.

## Loader validation

When MENACE starts, the loader scans `Mods/**/jiangyu.json`. A mod is blocked (its bundles aren't loaded, its templates don't apply) when:

- The manifest is missing or unreadable.
- `name` is empty or missing.
- A `depends` or `conflicts` entry is empty or doesn't parse against the `<name> <op> <constraint>` grammar.
- A required mod (by `name`) isn't present in `Mods/`, or is present but fails the version constraint.
- A `conflicts` entry matches an installed mod.
- Two mod folders share the same `name`. Both copies are blocked, with an error naming every duplicate location.

Mods with a valid manifest but no `.bundle` files are treated as **present for dependency checks**. This is useful for "metadata only" mods that consist entirely of template patches.

## Load order

Mods load in lexical order of their folder paths under `Mods/`. When two mods replace the same asset or patch the same template field, **the later-loaded mod wins**, with a warning logged.
