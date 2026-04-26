# What is Jiangyu?

Jiangyu is a modkit for MENACE (Unity 6, IL2CPP). It lets you replace assets (textures, sprites, models, audio) and patch the data MENACE ships with (unit stats, weapon damage, perk trees, and so on).

The toolkit has three pieces:

- **Studio**, a desktop editor for browsing assets, editing replacements and KDL patches, and compiling projects.
- **CLI** (`jiangyu`), the same operations from the terminal, for scripted workflows.
- **Loader** (`Jiangyu.Loader.dll`), a MelonLoader plugin that applies mods inside MENACE at runtime.

## Compared to MenaceAssetPacker

[MenaceAssetPacker](https://github.com/p0ss/MenaceAssetPacker) is the other, older modkit for MENACE. Jiangyu takes a different approach.

### Workflow

- **Mods are folders of files.** Replacements are declared by location: drop a file at the path Studio shows you and the filename does the matching. Your project is a directory you can put under git.
- **Templates patched in KDL.** Patches live in `templates/*.kdl` with operations like `patch`, `clone`, `set`, `append`, `insert`, `remove`. You can edit visually or write KDL by hand and switch between them at any time.
- **IDE-style Studio.** A grid of splittable, drag-and-drop panes with a command palette (`Ctrl+Shift+P`) and Monaco editors with vim mode.
- **Live previews and drag-to-author.** Textures, sprites, audio, and 3D models preview inline. Drag a template id or field from the browser into the editor to start a patch with the field path filled in.

### Foundations

- **Asset bundles built with the Unity Editor.** Studio drives `Unity.exe` to compile your replacements into AssetBundles. Game files like `resources.assets` are not modified.
- **Static template analysis.** Jiangyu reads MENACE's IL2CPP types offline (Cpp2IL + AsmResolver). You don't need to launch the game to extract template definitions.
- **Metadata-only asset index.** Indexing records each asset's name, type, path, and small per-asset details (audio frequency, sprite atlas rects), no actual asset data. Pixels, audio samples, and mesh geometry are pulled lazily when you export or preview an asset. Full extraction of MENACE's assets would be tens of gigabytes; the index stays small.
