# Template types

Most mods need no code. Stat tweaks, new variants, asset swaps, and audio are all data and live in [templates](/templates) and [assets](/assets/). Reach for C# only when you want something the data layer cannot express.

When you do, the common case by far is a **custom template type**: your own effect, condition, or value provider that the game constructs from a KDL `type=` slot, exactly like it constructs its built-in ones. This is the first thing to learn, and it needs no entry point, just the class. The other, rarer case is a behaviour mod that reacts to game moments at runtime, covered in [Hooks](./hooks). From inside either, your code can read and command the live game with [game verbs](./verbs).

A code mod compiles against `Jiangyu.Sdk` plus the game's IL2CPP proxy assemblies and ships as a DLL the loader injects at runtime.

## The `code/` project

`jiangyu init` already scaffolds `code/`: a C# project that references the SDK and the game assemblies. It stays dormant until you add a class, so an empty `code/` ships nothing, and `jiangyu compile` builds it for you when it is present. Open the **mod root** (the folder with the `.slnx`) in your IDE, not `code/`, so the language server loads the project.

## The game's types (`Il2CppMenace`)

Your code references the game's own types. IL2CPP exposes them to C# as proxy assemblies the loader ships under `<game>/MelonLoader/Il2CppAssemblies/`, and `code/` already references them. The game's `Menace` namespace appears under an `Il2Cpp` prefix, so `Menace.Tactical.Actor` is `Il2CppMenace.Tactical.Actor`; Unity's own types keep their `UnityEngine.*` names. These proxies are what your type's base class, hook casts, and patch targets name.

To discover what is there, you read the proxy assemblies. The convenient way is your IDE, since `code/` already references them: let autocomplete walk the `Il2CppMenace.*` namespace, and go-to-definition on a type or method to read its members. They ship as DLLs with no source, so enable decompiled-source navigation to step into them:

- Rider, Visual Studio, and VS Code (C# Dev Kit) have it on by default.
- Zed (Roslyn) needs it in `settings.json`:
  ```json
  { "lsp": { "roslyn": { "settings": {
    "csharp|navigation": { "dotnet_navigate_to_decompiled_sources": true }
  } } } }
  ```

They are ordinary .NET assemblies, so you can also open the DLLs under `MelonLoader/Il2CppAssemblies/` directly in any decompiler (ILSpy, dotPeek, dnSpy) without the project loaded.

For the data side of the game, the [`jiangyu templates`](/reference/cli#templates) commands surface the same types from the angle you author against: `templates inspect` shows a template subtype's fields and their types, and `templates query` reads a field off a live instance. `jiangyu assets search` finds bundled assets by name and type. Studio's asset and template browsers are the same discovery, with search.

## Defining a type

A `[JiangyuType]` class is your own subtype of a game type that the game constructs and dispatches through, slotted from a template exactly like a built-in one. **You do not need a `JiangyuMod` entry point for this**, just the class.

The common shape is a pair: a `SkillEventHandlerTemplate` is the factory the template data holds, and its `Create()` returns the `SkillEventHandler` the game ticks at runtime.

```csharp
using Il2CppMenace.Tactical;
using Il2CppMenace.Tactical.Skills;
using Jiangyu.Sdk;

[JiangyuType("Focus")]
public sealed partial class Focus : SkillEventHandlerTemplate
{
    public float DamageBonus = 0.5f;

    public override SkillEventHandler Create() => new FocusHandler { DamageBonus = DamageBonus };
}

[JiangyuType("FocusHandler")]
public sealed partial class FocusHandler : SkillEventHandler
{
    public float DamageBonus = 0.5f;

    public override void OnUpdate(EntityProperties properties)
        => properties.DamageMult *= 1f + DamageBonus;
}
```

The template carries the value the KDL sets, and its `Create()` copies it into a fresh handler, so `DamageBonus` lives on both: it is the authored configuration on the template, and the live value the handler reads when the game ticks it. They are two separate IL2CPP objects, the data and the behaviour.

Mark both classes `partial`. IL2CPP injection needs two constructors on every injected type, an `IntPtr` one to wrap an existing native object and a parameterless one to allocate a fresh one, and they are identical on every type bar the name, so Jiangyu generates them for you from the `[JiangyuType]` attribute. You write only your fields and overrides.

The loader injects each type and namespaces it as `<modId>:Name`, so you slot the template from KDL the same way you would a built-in effect:

```kdl
patch "PerkTemplate" "perk.fury" {
    append "EventHandlers" type="my_mod:Focus" {
        set "DamageBonus" 0.75
    }
}
```

`jiangyu compile` cross-checks every `type="<mod>:Name"` in your KDL against the names it finds in the built DLL and reports a typo or a missing build before you ship.

The game constructs an injected handler itself, so it has no `Context` of its own. For logging that does not matter: the static `Jiangyu.Sdk.Log` is auto-tagged with your mod id and works anywhere, no `Context` needed. To reach the mod's `State` or `ModFolder`, call `ModContext.For(this)`.

A class that satisfies a game interface rather than deriving a game class still derives `Il2CppSystem.Object` (so it is an IL2CPP type the constructors can be generated for) and lists the interface on the attribute:

```csharp
[JiangyuType("MyProvider", Interfaces = new[] { typeof(IValueProvider) })]
public sealed partial class MyProvider : Il2CppSystem.Object { /* the interface's methods */ }
```
