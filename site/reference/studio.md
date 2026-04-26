# Studio

Studio is Jiangyu's interactive workspace. Find assets, edit replacements and templates, compile your mod, all in one window. The CLI mirrors most of what Studio does; see [CLI](./cli.md) for the scripting surface.

## Opening a project

When Studio starts without a project loaded, it shows a welcome screen with three options:

- **Open project**: pick a directory containing a `jiangyu.json`.
- **Recent projects**: up to five recently opened projects, click to reopen.
- **New project**: scaffold a fresh project. Studio asks for a directory and a project name, then writes the same starter files as `jiangyu init`. See [Manifest](./manifest.md) for the scaffolded shape.

A loaded project shows in the topbar with its directory name and current git branch.

## The pane workspace

The main editing area is a grid of panes. Each pane has tabs, and each tab is one of three kinds:

- **Code**: a text editor for any project file. Used for `jiangyu.json`, `templates/*.kdl`, and any other file you want to edit by hand.
- **Asset Browser**: search and preview game assets. See [below](#asset-browser).
- **Template Browser**: search and edit MENACE templates. See [below](#template-browser).

The workspace is built around drag-and-drop. Most layout work is done by grabbing things and moving them, not by clicking menu items.

### Resizing panes

Every divider between panes is a resize handle. Drag the divider left/right or up/down to repartition space. Resizes persist with the project. They're restored next time you open it.

### Moving tabs

Drag a tab by its title:

- Drop it elsewhere in the same tab strip to **reorder** within the pane.
- Drop it onto another pane's tab strip to **move** the tab there.
- Drop it onto another pane's body to enter the drop overlay (see below).

To break a pane out into its own window, click the **Move pane to new window** button in the tab strip. The pane moves out of the main window and the secondary window takes over its tabs. Drag tabs or panes from a secondary window's tab strip back into the main workspace to dock them in. The same drop zones apply. Studio remembers your open secondary windows per project and re-spawns them next time you load the project.

### Splitting and rearranging panes

While dragging, an overlay covers the hovered pane and divides it into five drop zones: **Left**, **Right**, **Top**, **Bottom**, and **Centre**. The overlay highlights which zone the cursor is in.

What happens on drop depends on what you grabbed:

- **A single tab** (dragged from its title):
  - Edge → splits the target pane in that direction; the tab opens in the new sub-pane.
  - Centre → the tab joins the target pane's tab strip. Code panes only.
- **A whole pane** (dragged from empty space in the tab strip):
  - Edge → moves the dragged pane to that side of the target.
  - Centre → swaps positions with the target pane.

`F11` (or **Toggle fullscreen** from the palette) maximises the focused pane to the full window.

### Sidebar and status bar

The **sidebar** on the left is a project file tree. Click a file to open it in a code pane, or drag it into a specific pane to control where it lands. Hide the sidebar with **Ctrl+B**.

The **status bar** along the bottom shows the current compile state and a button to open the [compile dossier](#compile).

## Command palette

Press **Ctrl+Shift+P** (or **Ctrl+K**) to open the palette: a fuzzy-searchable list of every action Studio currently registers. Most navigation happens through here.

Key uses:

- **Open a browser** ("Open Asset Browser", "Open Template Browser") to start searching the asset or template index. Browsers open in a new pane.
- **Compile** the project.
- **Switch project** or open a recent project.
- **Layout actions**: split right or down, focus the next or previous pane, toggle fullscreen, toggle the sidebar.
- **Go to file**: fuzzy navigation across the project tree.

Actions are grouped by scope (**App**, **Project**, **View**, **File**, **Editor**, **Go to file**). Some show a keyboard shortcut on the right. Running the palette entry is equivalent to pressing the shortcut.

## Asset Browser

The Asset Browser searches the asset index built by `jiangyu assets index` (or by Studio's own index button when the index is missing or stale).

- **Search box**: live name search across the index. Typing filters results as you type.
- **Kind filter pills**: **All**, **Model**, **Mesh**, **Texture**, **Sprite**, **Audio**. Click one to narrow results.
- **Results list**: each row shows the asset name, class, collection, and pathId.
- **Detail panel**: when you select a result, a side panel shows class, collection, pathId, and asset-specific metadata (frequency and channels for audio, atlas and rect for sprites). The `Replace` row gives the path under `assets/replacements/` to drop your replacement at. The `Affects` row shows the count when the name is shared.
- **Preview pane**: textures and sprites render inline; audio shows an inline player; models render in a 3D viewer with orbit controls.
- **Export**: pulls the vanilla asset out as a starting point. The dropdown picks the destination: **Project** (your project's configured export path), **Default** (`<project>/.jiangyu/exports/`), or **Browse** to choose elsewhere. Multi-select to export several at once. Successful exports push a toast with a Reveal action that opens the file location.

## Template Browser

The Template Browser searches the template index built by `jiangyu templates index`.

- **Search box**: substring match across template type names, ids, and collections.
- **Type filter**: narrow to one template subtype (`UnitLeaderTemplate`, `WeaponTemplate`, etc.).
- **Results list**: each row is one template id within its subtype.
- **Open**: clicking a row opens the template in the [Template Visual Editor](#template-visual-editor) in a new tab.

Studio caches the template index alongside the asset index. Re-run **Index templates** when MENACE updates.

## Template Visual Editor

The visual editor edits KDL patches and clones with structured controls instead of free-form text.

- **Visual tab**: every field on the template appears as a row. Edits add `set` operations (or `append`/`insert`/`remove` for collection fields) to the underlying patch. The current vanilla value is shown next to your edit so you always know what you're overriding.
- **Source tab**: switch to the raw KDL text for the same patch. Edits in either tab round-trip through the parser, so you can pop into Source for a fiddly edit and back to Visual without losing structure.
- **Save target**: by default Studio saves into `templates/<type>-<id>.kdl` in your project. The first time you edit a template, Studio asks where to save.

The fastest way to author is by **dragging from the Template Browser** into the editor:

- **Drag an instance row** (a specific template id) into the editor body to add a new patch or clone targeting that template. A grip glyph reveals on hover to signal draggability.
- **Drag a member row** (a field on the template's type) into a patch's body to add a `set` operation for that field. The editor pre-fills the field path, the right value control for the field's type, and the current vanilla value as a placeholder.

See [Templates](./templates.md) for the KDL grammar.

## Compile

Compile runs the same pipeline as `jiangyu compile`, with a UI that shows you what's going to happen before it does.

- **Ctrl+Shift+B** kicks off a compile immediately. Use this when you trust the project state and just want to ship.
- **Palette → Compile** (or the compile button in the status bar) opens the **compile dossier**: a two-column modal with a stat panel on the right showing the asset and template counts that will be emitted, and a terminal-style log column on the left that streams output once the build starts. Click the **Compile** button inside the dossier to begin.

A successful compile pushes a toast with the elapsed time, warning count, and a **Reveal** action that opens your project's `compiled/` folder in your file manager.

## Settings

The Settings dialog (palette → **Settings**) configures both global and per-project state.

- **Game path**: directory containing `MENACE.exe`. Required for indexing and compilation. Studio detects the game's Unity version and shows it next to the field.
- **Unity Editor path**: a Unity Editor binary. The compile step uses it to build AssetBundles. Studio shows the expected version next to the field and warns if your install doesn't match.
- **Asset export path** (per-project): default destination for `Export → Project` in the Asset Browser. Stored in the project's local config, not global.
- **Restore open tabs**: when on, Studio reopens the panes and tabs from your last session.

The same global config is what `jiangyu` CLI reads. Edits in Studio and the CLI share one file.
