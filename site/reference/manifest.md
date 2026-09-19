# Manifest (`jiangyu.json`)

Every mod has a `jiangyu.json` at its root. It carries the mod's identity (name, version, author) and its dependency lists. Replacements aren't listed in the manifest. They're discovered by convention from `assets/replacements/`. Template patches aren't authored in the manifest either, but live in `templates/*.kdl`.

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
| `optionalDepends` | `string[]` | no       | (none)    | See [Optional dependencies](#optional-dependencies).   |
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

Both presence and version are enforced. A mod is blocked when a required mod is absent, blocked itself, or present but failing the constraint (e.g. it requires `Base >= 1.0.0` but `Base` is `0.9.0`). A bare name with no constraint checks presence only. A constraint is compared as a [semantic version](https://semver.org). An installed version that is not one degrades the entry to a presence-only check, while a constraint that is not one is a manifest error that blocks the mod.

A required mod also loads first. See [Load order](#load-order).

Names match against other mods' `name` fields (case-sensitive). An entry that names the mod itself is ignored with a load-order warning. The literal name `Jiangyu` is the loader itself, resolved against the installed loader version, so `"Jiangyu >= 1.3.0"` is a hard floor on the loader. No mod may take that name in any letter case, so an entry naming `Jiangyu` always means the loader, and an entry naming `jiangyu` can never be met and is reported as not the loader's name. You rarely need to write it: the compiler already stamps `compiledForJiangyu` and the loader warns on a newer-than-installed build. Add an explicit floor only when your mod will not function below a known loader version.

::: warning Dependency identity is provisional
`depends` resolves against display `name` until Jiangyu defines a stable machine-readable mod ID. Renaming a mod renames its dependency identity. Treat names as long-lived.
:::

## Optional dependencies

`optionalDepends` uses the same `<name>` or `<name> <op> <constraint>` grammar as `depends`. A listed mod loads before this one when it is installed, loadable, and inside the constraint. When it is absent, this mod loads with no ordering and nothing is logged. When it is installed but blocked, or outside the constraint, this mod still loads, is not ordered after it, and the loader logs a load-order warning naming the entry. Every entry is judged on its own, so a second entry for the same mod with a failing constraint still logs. The literal name `Jiangyu` never orders, but a constraint on it is still checked and logged when it fails.

```json
{
  "optionalDepends": [
    "WeaponPack >= 2.0.0"
  ]
}
```

Use it for a mod that patches another mod's templates when that mod is present, so the patches land after the templates they target, without making that mod a requirement.

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

A bare name conflicts with any installed version. A constrained entry conflicts only with versions in the range, so `"OtherMod < 2.0.0"` lets `OtherMod` 2.0.0 and newer load alongside you. When a conflicting mod's version can't be parsed, a constrained conflict does not trigger (an unconfirmable range never blocks). A conflict triggers on an installed mod whether or not that mod loads. An entry that names the mod itself, or the loader in another letter case, is ignored with a load-order warning.

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
| `textureReplacements` | names of texture replacements and sprite replacements with their own backing texture. An empty list lets the loader skip texture replacement scans |
| `additionPrefabs`   | logical names of prefab addition bundles staged into the compiled output (see [Prefabs](/assets/additions/prefabs)) |
| `compiledForUnity`  | the game's Unity version stamped at compile time, so the loader can compare it to the running game and warn on a build mismatch |
| `compiledForJiangyu`| the Jiangyu toolchain version that built the mod; the loader warns when it's newer than the installed loader |

Texture replacement scans use the assets authored under `assets/replacements/textures/` and `assets/replacements/sprites/`, including composited sprite atlases. Textures embedded in model bundles stay with their models and do not replace game textures globally. Manifests without `textureReplacements` retain legacy matching by asset name.

The compiled template program (the patch and clone directives emitted from `templates/*.kdl`) is **not** in the manifest. It ships beside it as `compiled/templates.json`, so `jiangyu.json` stays a small identity record the loader scans cheaply. A mod with no patches or clones ships no `templates.json`.

Both `compiled/jiangyu.json` and `compiled/templates.json` ship inside the compiled mod folder. Modders read them for debugging compiled output, never edit them.

## Loader validation

When MENACE starts, the loader scans `Mods/**/jiangyu.json`. A mod is blocked (its bundles aren't loaded, its templates don't apply) when:

- The manifest is missing or unreadable.
- `name` is empty or missing.
- `name` is `Jiangyu` in any letter case, which is reserved for the loader.
- A `depends`, `optionalDepends` or `conflicts` entry is empty, doesn't parse against the `<name> <op> <constraint>` grammar, or carries a constraint that is not a semantic version.
- A required mod (by `name`) isn't present in `Mods/`, is present but fails the version constraint, or is itself blocked.
- A `conflicts` entry matches an installed mod.
- Two mod folders share the same `name`. Both copies are blocked. A copy blocked for another reason keeps that reason, and any other copy names every duplicate location.
- The mod's required dependencies form a cycle. Every mod in the cycle is blocked, and so is everything that requires one of them.

Mods with a valid manifest but no `.bundle` files are treated as **present for dependency checks**. This is useful for "metadata only" mods that consist entirely of template patches.

## Load order

Mods load in lexical order of their folder paths under `Mods/`, with one adjustment: a mod's dependencies, required and optional, load before it. Walking folder order, when the loader reaches a mod whose dependencies have not loaded yet, it loads them first, each preceded by its own dependencies, and then the mod. A dependency pulled ahead this way loads before every mod between its folder and its dependent's, except the mods pulled ahead with it, which are placed by the same rule. Mods that are not pulled ahead keep their order relative to each other. Name your folders to control the order between unrelated mods (`010-Base`, `020-Addon`) and declare dependencies to guarantee the order between related ones.

When two mods replace the same asset or patch the same template field, **the later-loaded mod wins**, with a warning logged. Code mods initialise in the same order, so a mod's systems run `OnInit` after the systems of every mod it depends on.

A cycle that closes only through optional dependencies is broken by ignoring optional entries on it, preferring entries whose dependency sits later in folder order so the folder order stands. For a small tangle, up to sixteen optional entries on cycles with four or fewer to ignore, the loader finds the fewest entries that leave no cycle. For a larger one it ignores every entry on a cycle and then honours each one it can, so no ignored entry could have been honoured on its own. Every other entry that resolves is honoured. Each ignored entry is logged as a load-order warning naming the mod and the entry.
