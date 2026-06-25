# Dev verbs

A **dev verb** is a developer-only command your mod exposes to the dev tooling: a button you press while building the mod, never something a player can reach. Seed an inventory, jump to a game state, dump a value, win a mission. They are the mod-side companion to the SDK's own [game verbs](./verbs): same call-by-name mechanism, but the verb lives in your mod instead of the SDK.

```csharp
using Jiangyu.Sdk;
using Il2CppMenace.States;

// code/Gifts.Dev.cs
[DevVerb]
public static class Gifts
{
    [MutatingVerb]
    public static object Give(int count = 5)
    {
        var owned = StrategyState.Get()?.OwnedItems;
        if (owned == null)
            return new { error = "no strategy state" };
        // ... grant `count` of each gift ...
        return new { ok = true, granted = count };
    }
}
```

You invoke that over the dev bridge as `{ verb: "Gifts.Give", mutate: true }` (or with args, `{ verb: "Gifts.Give", args: [10], mutate: true }`).

## Writing one

- Mark a **static class** with `[DevVerb]`. The dev loader's verb runner discovers it in your mod's assembly alongside the SDK's `Jiangyu.Game.*` verbs.
- Each **public static method** on it is a verb, named `ClassName.MethodName`. Args marshal exactly like an SDK verb (primitives, enums by name or number, tagged game references like `{tile:[x,z]}` and `{template:"id"}`), and the return value is serialised back to a result summary.
- Mark a verb that changes game state with `[MutatingVerb]`, exactly as the SDK verbs do. It then runs only when the request passes `mutate: true`, so an exploratory call can't accidentally mutate.

A dev verb is the same primitive as a game verb, so everything on [Call game verbs](./verbs) applies: it's a thin call with no added safety, it faults where the raw call would, and the tactical ones need a live mission.

## Keeping them out of releases

Dev verbs must never ship to players, and Jiangyu enforces that in three layers:

1. **Unreachable at runtime, always.** The dev bridge and the verb runner live only in the dev loader. The loader a player runs has neither, so a `[DevVerb]` in a shipped mod is dead code nothing can call. This holds no matter how the mod was built.
2. **Compiled out of a release.** Put dev verbs in a file named `*.Dev.cs` (e.g. `Gifts.Dev.cs`). A plain `jiangyu compile` keeps them in for local testing, but `jiangyu compile --release` and `jiangyu package` exclude every `*.Dev.cs` from the build, so the shipped DLL never contains them. (`JIANGYU_DEV` is also defined in dev builds, for `#if JIANGYU_DEV` guards inside a shared file.)
3. **Refused at package time.** `jiangyu package` is a thin packager (it zips the existing `compiled/` output, it does not compile), so as a backstop it scans the compiled DLL and refuses to package if a `[DevVerb]` is still present, i.e. you packaged a dev build. The fix it points you to: build a release first, `jiangyu compile --release`, then `jiangyu package`.

So the release flow is: `jiangyu compile --release` then `jiangyu package`. A plain `jiangyu compile` keeps your dev verbs live while you iterate. In Studio, tick **Release build** in the Compile modal before compiling, then **Package**.

The rule of thumb is one line: **dev verbs go in a `*.Dev.cs` file.** Everything else follows.
