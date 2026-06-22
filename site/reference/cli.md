# CLI

The `jiangyu` CLI mirrors most of Studio's authoring operations. Its job is to make Jiangyu scriptable: build pipelines, CI checks, batch exports, ad-hoc inspection. For interactive authoring, [Studio](/studio) is the better surface.

All commands run from your project directory. Run `jiangyu <command> --help` for the authoritative flag listing. This page covers what each command is *for*.

## Project commands

### `jiangyu init`

Scaffolds a new mod project in the current directory: `jiangyu.json` (with `name` derived from the directory), a `.gitignore` excluding `.jiangyu/` and `compiled/`, and the dormant [`code/`](/sdk/) C# project and [`unity/`](/unity-project) project so a code, prefab, or UI mod needs no extra setup. An empty `code/` or `unity/` ships nothing.

Equivalent to Studio's "New project" dialog. See [Manifest](/reference/manifest) for the scaffolded shape.

### `jiangyu code sync`

Refreshes the per-mod C# project at `code/`, which `jiangyu init` already scaffolds and which compiles against the Jiangyu SDK and the game's IL2CPP assemblies. Idempotent: rewrites the Jiangyu-managed build files and `local.props`, recreates `code/` if it is missing, and preserves your `.csproj` and source.

Run it after a Jiangyu update, or on a project scaffolded by an older version. `jiangyu compile` builds `code/` automatically when present, so day to day you rarely need this.

### `jiangyu compile`

Compiles the project's replacements and templates into a shippable mod under `compiled/`. Reads `jiangyu.json`, walks `assets/replacements/` and `templates/`, emits AssetBundles and the compiled manifest. If `unity/Assets/Prefabs/` has any `.prefab` files, invokes Unity batchmode against the `unity/` project to build prefab addition bundles.

Compile bootstraps two preconditions on first run so the modder doesn't need to chain commands by hand:

- Host prefabs declared in [`imports`](/reference/manifest#imported-prefabs) are ripped into `unity/Assets/Imported/<name>/` if missing. Cached on subsequent runs.
- The asset index is built if missing or stale. Cached on subsequent runs.

Game data is loaded once and reused for both steps. Compile also verifies that every `Imported/<X>/` GUID referenced from authored content has `X` listed in `imports`, and fails with the unlisted names if not.

Equivalent to Studio's Compile dossier. Returns non-zero on any compile error.

### `jiangyu package`

Packages the already-compiled `compiled/` output into `<name>-<version>.zip` for distribution. Like `deploy`, it works on the existing build and does **not** compile, so run `jiangyu compile` first (it fails if `compiled/` is absent). The archive holds a single top-level `<name>/` folder, so a player extracts it straight into `Mods/`. The name and version come from `compiled/jiangyu.json`. Writes into the project directory by default; `--output <dir>` redirects it.

```sh
jiangyu compile
jiangyu package --output ../dist
```

To cut a new version, bump `version` in `jiangyu.json`, recompile, then package.

### `jiangyu deploy`

Copies the compiled mod from `compiled/` into the game's `Mods/<name>/` folder, where `<name>` is the manifest `name`. Clean: the existing deployed folder is removed first so stale artifacts from a previous build never linger. Run `jiangyu compile` first.

Equivalent to Studio's "Deploy Mod" palette command.

### `jiangyu unity sync`

Refreshes the per-mod [Unity project](/unity-project) at `unity/`, which `jiangyu init` already scaffolds. Idempotent: re-running rewrites the Jiangyu-managed files under `unity/Assets/Jiangyu/` and `.gitignore`, recreates `unity/` if it is missing, and preserves your own assets and packages.

Run it after a Jiangyu update. `jiangyu compile` builds `unity/` automatically when it has prefabs or UXML, so day to day you rarely need this.

### `jiangyu unity open`

Launches Unity Editor on the mod's `unity/` project, detaching so the CLI returns immediately. Uses the editor path resolved from your global config's `unityEditor` field.

### `jiangyu unity import-prefab <name>`

Extracts a vanilla game prefab plus its transitive dependency closure (meshes, materials, textures, shaders) into `unity/Assets/Imported/<name>/` as Unity-native assets so you can author against a real game baseline. Auto-bootstraps `unity/` if missing. Targeted extraction: only the dependency closure of the named asset is written, not the whole game.

```sh
jiangyu unity import-prefab rmc_default_female_soldier_2
```

Pass `--path-id` and/or `--collection` when the name is ambiguous (use `jiangyu assets search` to disambiguate).

Most modders won't run this directly. List the names in [`imports`](/reference/manifest#imported-prefabs) instead, and `jiangyu compile` rips on first run. Use this command as an escape hatch when you want to bring in a prefab one-off for inspection without committing to a manifest entry.

## Assets

The asset pipeline is index-first: build the catalogue once, then search and export against it.

### `jiangyu assets index`

Builds the searchable asset index and an attribute-hint supplement (covering `[NamedArray]`, `[Range]`, `[Min]`, `[Tooltip]`, `[HideInInspector]`, `[SoundID]`) used by the compiler and template inspector. Run this once after a game update or first-time setup, and subsequent commands read the cached output.

The index lives in your global cache (XDG / LocalAppData). Configure the cache root with the `cache` field in the global config (see [Configuration](#configuration)).

### `jiangyu assets search <query>`

Searches the asset index by name. Filters by class with `--type`:

```sh
jiangyu assets search window_background --type Texture2D
jiangyu assets search local_forces --type PrefabHierarchyObject
jiangyu assets search mortar --type AudioClip
```

The output includes each match's pathId, collection, and the suggested replacement path under `assets/replacements/`. `--type` takes raw Unity class names (`Texture2D`, `Sprite`, `AudioClip`, `PrefabHierarchyObject`, `GameObject`, `Mesh`). These are the same values as the asset index's `className` field.

### `jiangyu assets export <kind> <name>`

Exports a vanilla asset as a starting point for your replacement. Five kinds:

| Kind                                     | Output                                                    |
| ---------------------------------------- | --------------------------------------------------------- |
| `jiangyu assets export model <name>`     | Self-contained model package directory (cleaned glTF + auxiliary textures). |
| `jiangyu assets export texture <name>`   | PNG file.                                                 |
| `jiangyu assets export sprite <name>`    | PNG file.                                                 |
| `jiangyu assets export atlas <name>`     | PNG of the atlas with sprite outlines drawn on.           |
| `jiangyu assets export audio <name>`     | Audio file in whatever format Unity embedded (usually `.ogg`, sometimes `.wav`). |

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

Each takes its own flag set, so run with `--help` for specifics.

## Templates

### `jiangyu templates index`

Builds the template index by walking `Assembly-CSharp.dll` and the live `DataTemplate` instances in `resources.assets`. Like the asset index, this is a one-time-per-game-update operation, and later template commands read the cached output.

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

`--output text` is the scan-friendly view. The default is `pretty`, and `--output json` is for scripting. Pass `--with-mod <project-path>` to preview the effective state after your project's clones and patches apply, before launching MENACE.

### `jiangyu templates query`

A jq-like navigator over the template type tree, parsed offline from `Assembly-CSharp.dll`. Useful for finding the right `fieldPath` for a patch:

```sh
jiangyu templates query EntityTemplate.Properties.Accuracy
jiangyu templates query 'UnitLeaderTemplate.InitialAttributes[0]'
jiangyu templates query EntityTemplate.Skills        # auto-unwraps to SkillTemplate
```

A qualified `modId:Name` in the type position resolves one of your own `[JiangyuType]` code types instead of a game type. It reads the built DLLs under `compiled/code` (so run `jiangyu compile` first) from the current project directory, and lists the type's own fields alongside the inherited game-base members:

```sh
jiangyu templates query WOMENACE:WomenaceFocus              # the code type's fields
jiangyu templates query WOMENACE:WomenaceFocus.DamageBonus  # drill into one
```

For leaf fields it emits a copy-pasteable KDL snippet you can drop into a `templates/*.kdl` file (omitted for code-type fields, which are set through a `type=` construction rather than patched by template id).

### `jiangyu templates format`

Canonicalises every `*.kdl` under `templates/` (or a path you pass). Runs the same parse → validate → normalise → serialise pipeline Studio uses on save, so the on-disk text matches what the visual editor would write. Comments, leading and inline, round-trip through the format.

```sh
jiangyu templates format                  # rewrite every templates/*.kdl
jiangyu templates format path/to/file.kdl # rewrite one file
jiangyu templates format --check          # exit 1 if anything would change
```

What the pass does on top of plain reformatting:

- Strips redundant `type="X"` and `ref="X"` attributes when the destination field is monomorphic (the type is recovered on re-parse).
- Resolves symbolic Conversation `RoleGuid "Entity"` into the numeric guid the game stores (`RoleGuid 1248015120`).
- Coerces shorthand forms the validator accepts (e.g. `set "MoraleState" "Fleeing"` → enum, `set "ParentRef" "id"` on a concrete-typed field → `ref="X" "id"`).

`--check` is the CI-friendly mode: it prints each file that would change and exits non-zero if any do. No writes.

## Configuration

The CLI reads global configuration from a JSON file in your platform's standard config location (XDG / AppData adaptive). Three fields:

| Field         | Purpose                                                    |
| ------------- | ---------------------------------------------------------- |
| `game`        | Path to your MENACE install root (the directory containing `MENACE.exe`). |
| `unityEditor` | Path to a Unity Editor binary, used by `compile` to build AssetBundles. |
| `cache`       | Cache root for the asset index, exports, and the attribute-hint supplement. |

Studio's Settings dialog and the CLI write to the same file. The CLI doesn't have a config-edit subcommand, so edit the file directly or use Studio.

## Exit codes

- `0`: success.
- `1`: any handled error (missing config, parse failure, compile failure, ambiguous asset name, etc.). Error messages go to stderr.

The CLI doesn't distinguish error categories beyond `0` vs `1` today, so check stderr for the specific cause.
