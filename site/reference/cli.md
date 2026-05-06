# CLI

The `jiangyu` CLI mirrors most of Studio's authoring operations. Its job is to make Jiangyu scriptable: build pipelines, CI checks, batch exports, ad-hoc inspection. For interactive authoring, [Studio](/studio) is the better surface.

All commands run from your project directory. Run `jiangyu <command> --help` for the authoritative flag listing. This page covers what each command is *for*.

## Project commands

### `jiangyu init`

Scaffolds a new mod project in the current directory. Writes `jiangyu.json` (with `name` derived from the directory) and a `.gitignore` excluding `.jiangyu/` and `compiled/`.

Equivalent to Studio's "New project" dialog. See [Manifest](/reference/manifest) for the scaffolded shape.

### `jiangyu compile`

Compiles the project's replacements and templates into a shippable mod under `compiled/`. Reads `jiangyu.json`, walks `assets/replacements/` and `templates/`, emits AssetBundles and the compiled manifest.

Equivalent to Studio's Compile dossier. Returns non-zero on any compile error.

## Assets

The asset pipeline is index-first: build the catalogue once, then search and export against it.

### `jiangyu assets index`

Builds the searchable asset index and an attribute-hint supplement (covering `[NamedArray]`, `[Range]`, `[Min]`, `[Tooltip]`, `[HideInInspector]`, `[SoundID]`) used by the compiler and template inspector. Run this once after a game update or first-time setup; subsequent commands read the cached output.

The index lives in your global cache (XDG / LocalAppData). Configure the cache root with the `cache` field in the global config (see [Configuration](#configuration)).

### `jiangyu assets search <query>`

Searches the asset index by name. Filters by class with `--type`:

```sh
jiangyu assets search window_background --type Texture2D
jiangyu assets search local_forces --type PrefabHierarchyObject
jiangyu assets search mortar --type AudioClip
```

The output includes each match's pathId, collection, and the suggested replacement path under `assets/replacements/`. `--type` takes raw Unity class names (`Texture2D`, `Sprite`, `AudioClip`, `PrefabHierarchyObject`, `GameObject`, `Mesh`); these are the same values as the asset index's `className` field.

### `jiangyu assets export <kind> <name>`

Exports a vanilla asset as a starting point for your replacement. Four kinds:

| Kind                                     | Output                                                    |
| ---------------------------------------- | --------------------------------------------------------- |
| `jiangyu assets export model <name>`     | Self-contained model package directory (cleaned glTF + auxiliary textures). |
| `jiangyu assets export texture <name>`   | PNG file.                                                 |
| `jiangyu assets export sprite <name>`    | PNG file.                                                 |
| `jiangyu assets export audio <name>`     | Audio file in whatever format Unity embedded (typically `.ogg`). |

Common options:

- `--path-id <id>` picks a specific asset when the name matches more than one. Required for ambiguous names. The error tells you when.
- `--collection <name>` filters by source collection alongside `--path-id`.
- `--output <path>` overrides the default output location (defaults under `<cache>/exports/`).
- `assets export model` also accepts `--raw` to keep the native AssetRipper representation. Don't author against `--raw`. It's for inspection only.

### `jiangyu assets inspect <subcommand>`

Power-user inspection tools. Useful when you're debugging a replacement that compiles but doesn't behave as expected.

| Subcommand                        | Purpose                                                   |
| --------------------------------- | --------------------------------------------------------- |
| `assets inspect glb`              | Dump a glTF/GLB's node hierarchy and skin info.           |
| `assets inspect mesh`             | Inspect a serialised mesh's contract fields in a bundle.  |
| `assets inspect prefab`           | Compare a game prefab to a bundle prefab side by side.    |
| `assets inspect package <dir>`    | Validate an exported model package.                       |
| `assets inspect object`           | Dump a game object's observed field tree.                 |

Each takes its own flag set; run with `--help` for specifics.

## Templates

### `jiangyu templates index`

Builds the template index by walking `Assembly-CSharp.dll` and the live `DataTemplate` instances in `resources.assets`. Like the asset index, this is a one-time-per-game-update operation; later template commands read the cached output.

### `jiangyu templates list`

Lists template types and instances:

```sh
jiangyu templates list                                    # all template subtypes
jiangyu templates list --type UnitLeaderTemplate          # all instances of one subtype
```

### `jiangyu templates search <query>`

Substring search across template type names, ids, and collections. The fastest way to find an id when you only remember part of the name.

### `jiangyu templates inspect`

Reads the current value shape of one template:

```sh
jiangyu templates inspect --type UnitLeaderTemplate \
    --name squad_leader.darby --output text
```

`--output text` is the scan-friendly view; the default JSON output is for scripting. Pass `--with-mod <project-path>` to preview the effective state after your project's clones and patches apply, before launching MENACE.

### `jiangyu templates query`

A jq-like navigator over the template type tree, parsed offline from `Assembly-CSharp.dll`. Useful for finding the right `fieldPath` for a patch:

```sh
jiangyu templates query EntityTemplate.Properties.Accuracy
jiangyu templates query 'UnitLeaderTemplate.InitialAttributes[0]'
jiangyu templates query EntityTemplate.Skills        # auto-unwraps to SkillTemplate
```

For leaf fields it emits a copy-pasteable KDL snippet you can drop into a `templates/*.kdl` file.

## Configuration

The CLI reads global configuration from a JSON file in your platform's standard config location (XDG / AppData adaptive). Three fields:

| Field         | Purpose                                                    |
| ------------- | ---------------------------------------------------------- |
| `game`        | Path to your MENACE install root (the directory containing `MENACE.exe`). |
| `unityEditor` | Path to a Unity Editor binary, used by `compile` to build AssetBundles. |
| `cache`       | Cache root for the asset index, exports, and the attribute-hint supplement. |

Studio's Settings dialog and the CLI write to the same file. The CLI doesn't have a config-edit subcommand; edit the file directly or use Studio.

## Exit codes

- `0`: success.
- `1`: any handled error (missing config, parse failure, compile failure, ambiguous asset name, etc.). Error messages go to stderr.

The CLI doesn't distinguish error categories beyond `0` vs `1` today; check stderr for the specific cause.
