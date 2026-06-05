# code/

C# for this mod. Compiles against `Jiangyu.Sdk` plus the game's IL2CPP proxy
assemblies, and ships as a DLL the loader injects at runtime.

## Layout

- `Directory.Build.props` / `Directory.Build.targets`: Jiangyu-managed build settings and references. `jiangyu code sync` refreshes them, so do not edit.
- `local.props`: machine-specific game/SDK paths, written by `jiangyu code sync`. Gitignored.
- `*.csproj`, `*.cs`: your mod. Yours to edit, and sync never overwrites them.
- `../<Mod>.slnx` (at the mod root): seeded once so an IDE opened on the mod root discovers this project.

## IDE

Open the **mod root** (the folder containing `<Mod>.slnx`), not `code/`, so the C# language server loads the project.

## Authoring

- `[JiangyuType("Name")]` on a `partial` class injects it into the IL2CPP type
  system so KDL `type="<mod>:Name"` can construct it into a template slot. Mark the class `partial` and Jiangyu generates the IL2CPP injection constructors; you write only your fields and overrides.
- An injected `[JiangyuType]` the game constructs has no `Context` of its own. Log via the static `Jiangyu.Sdk.Log` (auto-tagged), and call `ModContext.For(this)` for the mod's `State` or `ModFolder`.
- For behaviour that reacts to game moments, write a `JiangyuSystem` subclass per feature (`OnInit`, `OnTemplatesApplied`, `OnSceneLoaded`, `OnUpdate`, `OnUnload`). The systems of one mod share a `Context`; when one must initialise after another, annotate it with `[DependsOn(typeof(OtherSystem))]`.

## Building

- `jiangyu compile` builds this project and packages the DLL alongside the manifest — it injects the game/SDK paths from your global config, so a fresh clone compiles without running sync first.
- `dotnet build` works too once `jiangyu code sync` has written `local.props`.
