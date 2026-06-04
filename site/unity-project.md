# The Unity project

Some content ships as Unity AssetBundles rather than as KDL or replacement files: [prefab additions](/assets/additions/prefabs) (new models) and [Game UI](/sdk/ui) (screens in UXML). Jiangyu scaffolds a per-mod Unity project under `<mod>/unity/` where these live, separate from the `code/` C# project, and `jiangyu compile` invokes Unity in batchmode to build them into AssetBundles automatically.

Prefabs are authored in the Unity Editor. UXML and USS are plain text you can write by hand or lay out in Unity's UI Builder, and either way they live under `unity/Assets/UI/` and build the same way. `jiangyu init` scaffolds the project for every mod, but it stays dormant: an empty `unity/` builds no bundles, so re-skins, template edits, and code-only mods can leave it alone.

## Layout

`jiangyu init` lays the project out as part of scaffolding the mod:

```text
<mod>/unity/
├── Assets/
│   ├── Jiangyu/              jiangyu-managed (re-sync refreshes)
│   │   └── Editor/           build, import, and bake editor scripts
│   ├── Prefabs/              prefab additions go here
│   └── UI/                   UXML and USS go here
├── Packages/manifest.json    seeded with no dependencies, extend as needed
└── .gitignore
```

Open it in Unity Editor to author a prefab or design UXML in the UI Builder. On first open Unity creates `ProjectSettings/`, `Library/`, and `ProjectVersion.txt`. `jiangyu unity sync` re-runs the scaffold idempotently: it refreshes `Assets/Jiangyu/` and `.gitignore` (Jiangyu owns those) and leaves your own assets, packages, and `ProjectSettings/` untouched.

Use the Unity version the game was built with. Studio shows the expected version next to the [Unity Editor path](/studio#settings) and the build refuses a mismatch.

## What goes where

| Folder | Holds | Referenced from |
| --- | --- | --- |
| `Assets/Prefabs/` | prefab additions (`.prefab`) | a KDL `asset="..."` on a `GameObject` field. See [Prefab additions](/assets/additions/prefabs). |
| `Assets/UI/` | UI markup (`.uxml`) and stylesheets (`.uss`) | `UI.Inject(target, "name")`. See [Game UI](/sdk/ui). |

A USS linked from a UXML by a `<Style>` tag, or a texture a prefab references, rides along inside that asset's bundle as a dependency, so you only ever name the top-level prefab or UXML.

## Building

`jiangyu compile`, or Studio's Compile, invokes Unity batchmode against the project, builds one AssetBundle per prefab and per UXML, and stages them into `compiled/` with the rest of the build. There is nothing to run by hand: authoring the asset and compiling is the whole loop.

## Naming and subfolders

An asset's runtime name is its path under the category folder (`Assets/Prefabs/` or `Assets/UI/`) with the extension dropped:

| Authored at | Name |
| --- | --- |
| `Assets/Prefabs/test_cube.prefab` | `test_cube` |
| `Assets/Prefabs/dir/test_cube.prefab` | `dir/test_cube` |
| `Assets/UI/relationship_bar.uxml` | `relationship_bar` |
| `Assets/UI/strategy/relationship_bar.uxml` | `strategy/relationship_bar` |

Subfolders are supported and recursive, so organise freely and the folder path becomes part of the name. A KDL `asset="dir/test_cube"` or a `UI.Inject(target, "strategy/relationship_bar")` resolves to the matching file. A bare leaf name resolves too, so name by subfolder when two files would otherwise share a leaf.
