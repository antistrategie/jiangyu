# Template types

A **custom template type** is your own effect, condition, or value provider that the game constructs from a KDL `type=` slot, exactly like it constructs its built-in ones. It is the common reason to write C#, and the one SDK surface that needs no [system](/sdk/#systems), just a `[JiangyuType]` class.

Set up the [`code/` project](/sdk/#the-code-project) and read the game's types as the [SDK overview](/sdk/) describes. From inside a type, your code can read and command the live game with [game verbs](./verbs).

## Defining a type

A `[JiangyuType]` class is your own subtype of a game type that the game constructs and dispatches through, slotted from a template exactly like a built-in one. **You do not need a `JiangyuSystem` for this**, just the class.

The common shape is a pair. A `SkillEventHandlerTemplate` is the factory the template data holds, and its `Create()` returns the `SkillEventHandler` the game ticks at runtime.

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
