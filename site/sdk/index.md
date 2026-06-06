# SDK

Most mods need no code. Stat tweaks, new variants, asset swaps, and audio are all data, and live in [templates](/templates) and [asset files](/assets/replacements/). Reach for C# only when you want something the data layer cannot express.

When you do, the common case by far is a **custom [template type](./template-types)**: your own effect, condition, or value provider that the game constructs from a KDL `type=` slot. It needs no entry point, just a `[JiangyuType]` class, and it is where most code mods begin. The rest of this page, the entry point and the mod API, is for the rarer **behaviour mod** that reacts to the game at runtime.

::: tip
If you only want to add an effect, condition, or value the game reads from data, you want [Template types](./template-types) and can skip the entry point and `Context` below.
:::

A code mod compiles against `Jiangyu.Sdk` plus the game's IL2CPP proxy assemblies and ships as a DLL the loader injects at runtime.

## The surfaces

| Surface | Use it to | Entry point |
| --- | --- | --- |
| [Template types](./template-types) | add your own effect, condition, or value the game constructs from data | not needed |
| [Hooks](#hooks) | react to game moments (a kill, a round boundary, a leader hired) | needed |
| [Game verbs](./verbs) | read and command the live game (spawn a unit, query a path) | not needed |
| [Game UI](./ui) | inject your own elements into the game's screens | not needed |

Template types are the data-shaped surface. The other three are runtime behaviour, and verbs and UI can be called from inside any of them.

## The `code/` project

`jiangyu init` already scaffolds `code/`: a C# project that references the SDK and the game assemblies. It stays dormant until you add a class, so an empty `code/` ships nothing, and `jiangyu compile` builds it for you when it is present. Open the **mod root** (the folder with the `.slnx`) in your IDE, not `code/`, so the language server loads the project.

To ship Unity-native content (prefabs, custom UI in UXML) the mod also gets a [Unity project](/unity-project) under `unity/`. That is a separate surface from `code/`, covered on its own page.

## The game's types (`Il2CppMenace`)

Your code references the game's own types. IL2CPP exposes them to C# as proxy assemblies the loader ships under `<game>/MelonLoader/Il2CppAssemblies/`, and `code/` already references them. The game's `Menace` namespace appears under an `Il2Cpp` prefix, so `Menace.Tactical.Actor` is `Il2CppMenace.Tactical.Actor`. Unity's own types keep their `UnityEngine.*` names. These proxies are what your type's base class, hook casts, and patch targets name.

The fastest way to find your way around is [UnityExplorer](https://github.com/yukieiji/UnityExplorer), a runtime inspector you drop into MelonLoader. It browses the live game: the scene hierarchy and every GameObject, the components on an object and the values they hold, and the loaded type space, with a C# console to poke at any of it. Use it to find the type behind a screen, the name of a component, or what a live object actually holds.

When you are writing the code, the proxy assemblies are the reference for the exact members and signatures. Your IDE reads them directly, since `code/` references them: let autocomplete walk the `Il2CppMenace.*` namespace, and go-to-definition on a type or method to read its members. They ship as DLLs with no source, so enable decompiled-source navigation to step into them:

- Rider, Visual Studio, and VS Code (C# Dev Kit) have it on by default.
- Zed (Roslyn) needs it in `settings.json`:
  ```json
  { "lsp": { "roslyn": { "settings": {
    "csharp|navigation": { "dotnet_navigate_to_decompiled_sources": true }
  } } } }
  ```

They are ordinary .NET assemblies, so you can also open the DLLs under `MelonLoader/Il2CppAssemblies/` directly in any decompiler (ILSpy, dotPeek, dnSpy) without the project loaded.

For the data side of the game, the [`jiangyu templates`](/reference/cli#templates) commands surface the same types from the angle you author against: `templates inspect` shows a template subtype's fields and their types, and `templates query` reads a field off a live instance. `jiangyu assets search` finds bundled assets by name and type. Studio's asset and template browsers are the same discovery, with search.

## Systems

A **behaviour mod** is made of one or more **systems**: a `JiangyuSystem` subclass per feature. The loader discovers every system in the mod, instantiates each, binds the mod's shared `Context`, and drives the lifecycle. Every method is a no-op by default, so override only what you need. (A [template type](./template-types) is not a system and needs no entry point.)

```csharp
using Jiangyu.Sdk;

public sealed class RelationshipSystem : JiangyuSystem
{
    public override void OnInit()
    {
        Log.Info("loaded");
        Context.Hooks.Subscribe<RoundStartedContext>(ctx =>
            Log.Info($"round {ctx.Round} started"));
    }
}
```

| Override | Called |
| --- | --- |
| `OnInit()` | Once, after the system loads and its `Context` is bound. |
| `OnTemplatesApplied()` | Once, after every mod's template clones and patches have landed on the live templates. Read or further adjust the final merged template set here. |
| `OnSceneLoaded(buildIndex, sceneName)` | Each time a Unity scene finishes loading. |
| `OnUpdate()` | Every frame. Override only when you genuinely need per-frame work. |
| `OnUnload()` | On shutdown or hot reload. The loader stops your coroutines and removes your patches for you. |

The systems of one mod share a single `Context` (the same `State`, hooks, patches, and assets), so split a mod into as many systems as it has features and let them cooperate through `Context` rather than growing one entry point. Systems run in a stable, name-ordered sequence. When one must initialise after another, declare it with `[DependsOn]`, and the loader runs that dependency first on init and tears systems down in reverse on unload.

```csharp
[DependsOn(typeof(EconomySystem), typeof(RosterSystem))]
public sealed class TradeSystem : JiangyuSystem { /* ... */ }
```

A dependency that is not a system of the same mod is ignored with a warning, as is a cycle (its members fall back to the name order). The analyzer flags both in the IDE before you run (`JIA010`, `JIA011`), along with a system the loader cannot construct because it has no parameterless constructor (`JIA012`).

## The mod API

`Context` is the mod's handle on the loader's services. A `[JiangyuType]` handler with no `Context` of its own reaches the same services through `ModContext.For(this)`.

| Member | What it gives you |
| --- | --- |
| `Log` | Per-mod logging (`Info`, `Warn`, `Error`, `Debug`), auto-tagged with your mod id. The static `Jiangyu.Sdk.Log` is the same logger and needs no `Context`, so most code just calls `Log.Info(...)`. `Debug` is dropped unless the dev opts in. |
| `Hooks` | Subscribe to global game moments. See [Hooks](#hooks). |
| `State` | Per-save-slot persistent state. See [State](#state). |
| `Assets` | Load the mod's own bundled assets by name. See [Assets](#assets). |
| `Coroutines` | Run multi-frame or timed logic. See [Coroutines](#coroutines). |
| `Patches` | Patch a game method no hook covers. The escape hatch. See [Patches](#patches). |
| `ModFolder` | Absolute path to the deployed `Mods/<ModId>` folder. |
| `ModId`, `Version` | The mod id and the version from `jiangyu.json`. |

### Hooks

`Context.Hooks` is the SDK's event bus: global game moments your code reacts to, a kill, a round boundary, a leader hired, a save or load. Subscribe by context type from a system's [`OnInit`](#systems), and the subscription lives for the mod's lifetime.

```csharp
Context.Hooks.Subscribe<EntityDiedContext>(ctx =>
{
    var victim = ctx.Victim as Entity;   // game-typed payloads are object, cast them
    Log.Info($"{victim?.GetName()} died");
});
```

Every moment is a context type, split into a tactical family (combat, turns, movement, skills) and a strategy family (factions, leaders, operations). The [hook reference](/reference/hooks) lists them all with their payloads. Game-typed payloads are held as `object` to keep the SDK game-agnostic, so you cast them in your handler. Primitive payloads, a round number or a count, are typed directly.

The bus is observer-only. It tells you a moment happened, but it can't stop it. To change what a skill or effect actually does, write a [template type](./template-types) instead. Verbs read and command the live game from a handler, and [Patches](#patches) cover the rare method no hook reaches.

### State

`Context.State` persists mod-owned data across save and load, in a sidecar beside the save file and keyed by the save slot so it never leaks between saves.

```csharp
public sealed class MyState { public int TimesSpawned; }

var state = Context.State.Get<MyState>();
state.TimesSpawned++;   // mutate in place; written when the game saves
```

Most mod state should be game state, not this: a marker skill, stacks, a status with a duration. Reach for `State` only for genuinely out-of-band data the game save does not already hold.

### Assets

`Context.Assets` loads the mod's own bundled assets on demand, by name. Only this mod's bundles are visible, never another mod's, and a loaded asset is kept alive by the loader for the session.

```csharp
var icon = Context.Assets.Load<UnityEngine.Sprite>("my_icon");
```

`T` is a UnityEngine type and the name is the asset's path under its category folder with the extension dropped, so `my_icon` or `dir/my_icon`.

### Coroutines

Run logic across frames or over time with a normal C# iterator.

```csharp
Context.Coroutines.Start(Settle());

System.Collections.IEnumerator Settle()
{
    yield return null;                              // resume next frame
    yield return new UnityEngine.WaitForSeconds(1); // or wait a beat
    Log.Info("a second later");
}
```

Use it to act after a synchronous hook once the game state has settled, to poll for a condition no hook covers, or to sequence an effect over time. Routines are stopped for you when the mod unloads.

### Patches

When no hook covers the moment you need, patch the game method directly. Name it by its declaring type and method; your handler runs before (prefix) or after (postfix).

```csharp
Context.Patches.Postfix("Il2CppMenace.Tactical.Actor", "TakeDamage", info =>
{
    var actor = info.Instance as Actor;
    Log.Info($"{actor?.GetName()} took damage");
});
```

A prefix handler may set `info.Skip = true` to stop the original method running. This is the escape hatch: prefer a hook where one exists, and a template type to change an effect. Patches are tracked per mod and removed on unload.
