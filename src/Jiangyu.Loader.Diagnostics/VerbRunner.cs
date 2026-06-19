using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Text.Json;
using Il2CppInterop.Runtime.InteropTypes;
using Il2CppMenace.Tactical;
using Jiangyu.Loader.Templates;
using Jiangyu.Sdk;
using MelonLoader;
using DataTemplate = Il2CppMenace.Tools.DataTemplate;
using ScriptableObject = UnityEngine.ScriptableObject;

namespace Jiangyu.Loader.Diagnostics;

/// <summary>
/// Runs a game-API verb by name against the live game. The caller names a verb (a static
/// method on a <c>Jiangyu.Game.*</c> class) and passes JSON args. The runner resolves the
/// method, marshals the args (primitives, enums by name or number, and tagged game references
/// such as <c>{tile:[x,z]}</c>, <c>{actor:"active"}</c>, <c>{template:"id"}</c>, and
/// <c>{ref:"handle"}</c> for a live object handed back by an earlier verb), invokes it
/// on the Unity main thread, and serialises the result to a summary. A live object with
/// no richer form is returned with a handle (see <see cref="ObjectHandles"/>) so it can
/// be threaded into a later verb.
///
/// <para>A verb marked <c>[MutatingVerb]</c> runs only when the request passes
/// <c>mutate:true</c>, so an exploratory read can never accidentally change game state.
/// Driven by the <c>verb</c> bridge command.</para>
/// </summary>
internal static class VerbRunner
{
    // Request: { verb: "Mission.Actors", args: [...], mutate?: bool }.
    internal static object Run(JsonElement request, MelonLogger.Instance log)
    {
        if (request.ValueKind != JsonValueKind.Object
            || !request.TryGetProperty("verb", out var verbProp) || verbProp.GetString() is not { } verb)
            return new { error = "missing 'verb' (e.g. \"Mission.Actors\")" };

        var args = request.TryGetProperty("args", out var a) && a.ValueKind == JsonValueKind.Array
            ? a.EnumerateArray().ToArray()
            : Array.Empty<JsonElement>();
        var mutate = request.TryGetProperty("mutate", out var m) && m.ValueKind == JsonValueKind.True;

        var method = ResolveMethod(verb, args.Length);
        if (method == null)
            return new { error = $"no verb '{verb}' taking {args.Length} arg(s). Use \"Class.Method\" (e.g. \"Units.Spawn\")." };

        if (!mutate && method.IsDefined(typeof(MutatingVerbAttribute), inherit: false))
            return new { error = $"'{verb}' mutates game state; pass mutate:true to run it." };

        object[] marshalled;
        try
        {
            marshalled = MarshalArgs(method.GetParameters(), args);
        }
        catch (Exception ex)
        {
            return new { error = $"could not marshal args for '{verb}': {ex.Message}" };
        }

        try
        {
            var result = method.Invoke(null, marshalled);
            log.Msg($"[verb] {verb} -> {result?.GetType().Name ?? "null"}");
            return new { verb, result = Serialise(result) };
        }
        catch (TargetInvocationException ex)
        {
            return new { verb, error = $"verb threw: {ex.InnerException?.Message ?? ex.Message}" };
        }
        catch (Exception ex)
        {
            return new { verb, error = $"invoke failed: {ex.Message}" };
        }
    }

    // --- method resolution -------------------------------------------------

    // The verb classes are public static classes in the Jiangyu.Game.* namespaces of
    // Jiangyu.Sdk.Menace (merged into the dev loader). The set is fixed for the process,
    // so scan the assembly once and cache the short-name -> class map.
    private static Dictionary<string, Type> _verbClasses;

    private static Dictionary<string, Type> VerbClasses() => _verbClasses ??= BuildVerbClasses();

    private static Dictionary<string, Type> BuildVerbClasses()
    {
        var map = new Dictionary<string, Type>(StringComparer.OrdinalIgnoreCase);
        foreach (var t in typeof(Jiangyu.Game.Tactical.Mission).Assembly.GetTypes())
            if (t.IsClass && t.IsAbstract && t.IsSealed                 // static class
                && t.Namespace != null && t.Namespace.StartsWith("Jiangyu.Game", StringComparison.Ordinal))
                map[t.Name] = t;
        return map;
    }

    // Resolved verbs are cached by name + arity: the verb set and its overloads are
    // fixed for the process, so a repeated call skips the reflection scan.
    private static readonly Dictionary<string, MethodInfo> _methodCache = new(StringComparer.OrdinalIgnoreCase);

    private static MethodInfo ResolveMethod(string verb, int argCount)
    {
        var key = verb + "/" + argCount;
        if (_methodCache.TryGetValue(key, out var cached))
            return cached;
        var resolved = ResolveMethodUncached(verb, argCount);
        _methodCache[key] = resolved;
        return resolved;
    }

    // Match "Class.Method" by the class's short name and the method by name + arity.
    private static MethodInfo ResolveMethodUncached(string verb, int argCount)
    {
        var dot = verb.LastIndexOf('.');
        if (dot <= 0)
            return null;
        var className = verb.Substring(0, dot);
        var methodName = verb.Substring(dot + 1);

        if (!VerbClasses().TryGetValue(className, out var type))
            return null;

        // Prefer an overload whose required-parameter count fits the supplied args.
        return type.GetMethods(BindingFlags.Public | BindingFlags.Static)
            .Where(mi => string.Equals(mi.Name, methodName, StringComparison.OrdinalIgnoreCase))
            .Select(mi => (mi, ps: mi.GetParameters()))
            .Where(x => x.ps.Count(pi => !pi.IsOptional) <= argCount && argCount <= x.ps.Length)
            .OrderByDescending(x => x.ps.Length)
            .Select(x => x.mi)
            .FirstOrDefault();
    }

    // --- arg marshalling ---------------------------------------------------

    private static object[] MarshalArgs(ParameterInfo[] parameters, JsonElement[] args)
    {
        var marshalled = new object[parameters.Length];
        for (var i = 0; i < parameters.Length; i++)
        {
            if (i >= args.Length)
            {
                marshalled[i] = parameters[i].IsOptional ? Type.Missing : null;
                continue;
            }
            marshalled[i] = MarshalArg(args[i], parameters[i].ParameterType);
        }
        return marshalled;
    }

    private static object MarshalArg(JsonElement arg, Type target)
    {
        var nullable = Nullable.GetUnderlyingType(target);
        if (nullable != null)
            return arg.ValueKind == JsonValueKind.Null ? null : MarshalArg(arg, nullable);

        // {ref:"handle"} selects a live object handed back by an earlier verb, for any
        // reference-typed parameter.
        if (arg.ValueKind == JsonValueKind.Object && arg.TryGetProperty("ref", out var handleProp))
            return ResolveHandle(handleProp, target);

        if (target == typeof(string)) return arg.GetString();
        if (target == typeof(bool)) return arg.GetBoolean();
        if (target == typeof(int)) return arg.GetInt32();
        if (target == typeof(long)) return arg.GetInt64();
        if (target == typeof(float)) return arg.GetSingle();
        if (target == typeof(double)) return arg.GetDouble();
        if (target.IsEnum) return ParseEnum(arg, target);

        if (typeof(Tile).IsAssignableFrom(target)) return ResolveTile(arg);
        if (typeof(Actor).IsAssignableFrom(target)) return ResolveActor(arg);
        if (typeof(DataTemplate).IsAssignableFrom(target) || typeof(ScriptableObject).IsAssignableFrom(target))
            return ResolveTemplate(arg, target);

        throw new NotSupportedException($"arg type {target.Name} is not marshallable yet");
    }

    // An enum arg is either its member name (case-insensitive) or its numeric value.
    private static object ParseEnum(JsonElement arg, Type target)
    {
        if (arg.ValueKind == JsonValueKind.Number)
            return Enum.ToObject(target, arg.GetInt64());

        var name = arg.GetString();
        if (!string.IsNullOrEmpty(name) && Enum.TryParse(target, name, ignoreCase: true, out var value))
            return value;

        var names = Enum.GetNames(target);
        var listed = names.Length <= 16 ? string.Join(", ", names) : string.Join(", ", names.Take(16)) + ", ...";
        throw new ArgumentException($"'{name}' is not a {target.Name}. Expected a name ({listed}) or its number.");
    }

    // {ref:"handle"} -> the live object ObjectHandles captured for that handle.
    private static object ResolveHandle(JsonElement handleProp, Type target)
    {
        var handle = handleProp.GetString();
        if (!ObjectHandles.TryGet(handle, out var value))
            throw new ArgumentException($"no live object for handle '{handle}' (handles are cleared on scene change).");
        if (!target.IsInstanceOfType(value))
            throw new ArgumentException($"handle '{handle}' is a {value.GetType().Name}, not the expected {target.Name}.");
        return value;
    }

    // Unwrap a tagged reference {key: value} to its inner value, passing a bare value
    // through unchanged. Shared by the tile/actor/template/handle forms below.
    private static JsonElement Untag(JsonElement arg, string key)
        => arg.ValueKind == JsonValueKind.Object && arg.TryGetProperty(key, out var inner) ? inner : arg;

    // [x,z] or {tile:[x,z]}
    private static Tile ResolveTile(JsonElement arg)
    {
        var coords = Untag(arg, "tile");
        if (coords.ValueKind != JsonValueKind.Array)
            throw new ArgumentException("tile arg expects [x,z] or {tile:[x,z]}");
        var xz = coords.EnumerateArray().ToArray();
        if (xz.Length < 2)
            throw new ArgumentException("tile arg expects two coordinates [x,z]");
        return Jiangyu.Game.Tactical.Mission.TileAt(xz[0].GetInt32(), xz[1].GetInt32());
    }

    // "active" | <index> | {actor:"active"} | {actor:<index into Mission.Actors()>}
    private static Actor ResolveActor(JsonElement arg)
    {
        var sel = Untag(arg, "actor");
        if (sel.ValueKind == JsonValueKind.Number)
        {
            var actors = Jiangyu.Game.Tactical.Mission.Actors();
            var index = sel.GetInt32();
            if (index < 0 || index >= actors.Count)
                throw new ArgumentException($"actor index {index} out of range (0..{actors.Count - 1}).");
            return actors[index];
        }
        if (sel.ValueKind == JsonValueKind.String && string.Equals(sel.GetString(), "active", StringComparison.OrdinalIgnoreCase))
            return Jiangyu.Game.Tactical.Mission.ActiveActor;
        throw new ArgumentException("actor arg expects {actor:\"active\"} or {actor:<index>}");
    }

    // "id" or {template:"id"} -> a live template of the parameter's type, resolved
    // through the same registry that template patching and cloning use, so clones
    // and game-native templates both resolve.
    private static object ResolveTemplate(JsonElement arg, Type target)
    {
        var idElement = Untag(arg, "template");
        if (idElement.ValueKind != JsonValueKind.String)
            throw new ArgumentException("template arg expects \"id\" or {template:\"id\"}");

        var id = idElement.GetString();
        if (!TemplateRuntimeAccess.TryGetTemplateById(target, id, out var template, out var error))
            throw new ArgumentException(error ?? $"no {target.Name} template with id '{id}'");
        return template;
    }

    // --- result serialisation ----------------------------------------------

    private static object Serialise(object value)
    {
        switch (value)
        {
            case null: return null;
            case string or bool or int or long or float or double: return value;
            case Enum e: return e.ToString();
            case Tile tile: return new { type = "Tile", x = tile.GetX(), z = tile.GetZ() };
            case Actor actor: return SerialiseActor(actor);
            case DataTemplate or ScriptableObject: return SerialiseTemplate(value);
            case IEnumerable seq when value is not Il2CppSystem.Object: return SerialiseSeq(seq);
            case Il2CppObjectBase obj: return SerialiseHandle(obj);
        }
        return new { type = value.GetType().Name, value = value.ToString() };
    }

    private static object SerialiseTemplate(object template)
        => new { type = TypeName(template), id = TemplateRuntimeAccess.ReadTemplateId(template) };

    // A live game object with no richer form: hand back a handle so it can be passed to
    // another verb as {ref:"..."}, plus its id when it exposes one.
    private static object SerialiseHandle(Il2CppObjectBase obj)
        => new { type = TypeName(obj), handle = ObjectHandles.Register(obj), id = HandleId(obj) };

    // A live instance carries no id of its own; its identity is the template behind it.
    // Read the template id for items and leaders so a handle is recognisable at a glance
    // (e.g. which armour an owned instance is), falling back to the generic reflection scan.
    private static string HandleId(Il2CppObjectBase obj)
    {
        switch (obj)
        {
            case Il2CppMenace.Items.BaseItem item:
                return item.GetBaseItemTemplate()?.GetID() ?? TemplateRuntimeAccess.ReadTemplateId(obj);
            case Il2CppMenace.Strategy.BaseUnitLeader leader:
                return leader.GetTemplate()?.GetID() ?? TemplateRuntimeAccess.ReadTemplateId(obj);
            default:
                return TemplateRuntimeAccess.ReadTemplateId(obj);
        }
    }

    // A returned actor carries a handle too, so it can be threaded into a later verb the
    // same way as any other live object, alongside its readable faction and tile.
    private static object SerialiseActor(Actor actor)
    {
        var tile = actor.GetTile();
        return new
        {
            type = "Actor",
            handle = ObjectHandles.Register(actor),
            faction = actor.GetFaction().ToString(),
            tile = tile == null ? null : new { x = tile.GetX(), z = tile.GetZ() },
        };
    }

    // The concrete IL2CPP class name, falling back to the wrapper type. A wrapper from a
    // List<AbstractBase> otherwise reports the abstract base, not the real subclass.
    private static string TypeName(object value)
        => (value is Il2CppObjectBase obj ? Il2CppTypeName.Resolve(obj) : null) ?? value.GetType().Name;

    private static object SerialiseSeq(IEnumerable seq)
    {
        const int cap = 200;
        var items = new List<object>();
        var truncated = false;
        foreach (var item in seq)
        {
            if (items.Count >= cap) { truncated = true; break; }
            items.Add(Serialise(item));
        }
        return truncated ? new { count = items.Count, truncated = true, items } : (object)items;
    }
}
