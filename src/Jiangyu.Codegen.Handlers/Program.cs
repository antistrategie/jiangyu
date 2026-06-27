using System.Reflection;
using Jiangyu.Codegen.Handlers;
using Jiangyu.Core.Config;
using Jiangyu.Core.Il2Cpp;
using Jiangyu.Core.Templates;

// jiangyu-codegen-handlers <outputDir>
//
// Reflects the game's Il2CppInterop assembly (read-only, no execution) through the shared
// TemplateTypeCatalog and renders the event-handler reference into <outputDir> (the docs
// site's reference/): the handler base types and their overridable methods (the C# path),
// every built-in handler subtype and its KDL-settable fields (the data path), and the
// condition and value-provider families. The pure rendering lives in HandlerDocEmit
// (unit-tested); this file is the reflection + IO around it. A base type that no longer
// resolves forces a non-zero exit -- the game-update contract check.

const string HandlerBaseName = "Il2CppMenace.Tactical.Skills.SkillEventHandler";
const string TemplateBaseName = "Il2CppMenace.Tactical.Skills.SkillEventHandlerTemplate";
const string ConditionBaseName = "Il2CppMenace.Tactical.Skills.TacticalCondition";
const string ValueProviderName = "IValueProvider";

if (args.Length < 1)
{
    Console.Error.WriteLine("usage: jiangyu-codegen-handlers <outputDir>");
    return 2;
}
var outputDir = args[0];

var (gameDir, _) = GlobalConfig.ResolveGamePath(GlobalConfig.Load());
if (gameDir is null)
{
    Console.Error.WriteLine("handlergen: no game path in global config.");
    return 2;
}
var asmPath = Path.Combine(gameDir, "MelonLoader", "Il2CppAssemblies", "Assembly-CSharp.dll");
if (!File.Exists(asmPath))
{
    Console.Error.WriteLine($"handlergen: Assembly-CSharp.dll not found at {asmPath}");
    return 2;
}
var searchDirs = new List<string>();
var net6 = Path.Combine(gameDir, "MelonLoader", "net6");
if (Directory.Exists(net6)) searchDirs.Add(net6);

// The supplement is optional: GetMembers' Odin flagging and class-subtype enumeration do
// not depend on it. Load it when present so [NamedArray]/interface-impl enrichment is there.
Il2CppMetadataSupplement? supplement = null;
try { supplement = Il2CppMetadataCache.LoadIfPresent(GlobalConfig.Load().GetCachePath()); } catch { /* optional */ }

using var catalog = TemplateTypeCatalog.Load(asmPath, searchDirs, supplement);

var handlerBase = catalog.ResolveType(HandlerBaseName, out _, out _);
var templateBase = catalog.ResolveType(TemplateBaseName, out _, out _);
if (handlerBase is null || templateBase is null)
{
    Console.Error.WriteLine($"handlergen: handler base types not found (game update?): "
        + $"{HandlerBaseName}={(handlerBase is null ? "missing" : "ok")} "
        + $"{TemplateBaseName}={(templateBase is null ? "missing" : "ok")}");
    return 1;
}

var bases = new List<HandlerBaseDoc>
{
    new(handlerBase.Name,
        "The runtime behaviour. The game ticks this through a mission. Override the events you care about.",
        OverridableMethods(handlerBase)),
    new(templateBase.Name,
        "The factory the template data holds. `Create()` returns a fresh handler with the authored fields copied in.",
        OverridableMethods(templateBase)),
};

var handlers = new List<HandlerDoc>();
foreach (var type in catalog.EnumerateConcreteSubtypes(templateBase))
{
    var ownNames = OwnDeclaringTypeNames(type, templateBase);
    var fields = new List<HandlerFieldDoc>();
    foreach (var m in TemplateTypeCatalog.GetMembers(type))
    {
        if (!ownNames.Contains(m.DeclaringTypeFullName)) continue;
        var enumValues = m.MemberType.IsEnum ? TemplateTypeCatalog.GetEnumMemberNames(m.MemberType) : [];
        fields.Add(new HandlerFieldDoc(m.Name, PrettyPrimitives(catalog.FriendlyName(m.MemberType)), m.IsLikelyOdinOnly, enumValues));
    }
    handlers.Add(new HandlerDoc(type.Name, fields));
}

var conditions = NamesOfSubtypes(catalog, ConditionBaseName);
var valueProviders = NamesOfSubtypes(catalog, ValueProviderName);

var model = new HandlerModel(bases, handlers, conditions, valueProviders);

Directory.CreateDirectory(outputDir);
var file = Path.Combine(outputDir, "event-handlers.md");
File.WriteAllText(file, HandlerDocEmit.Emit(model));
Console.WriteLine($"handlergen: wrote {file} ({handlers.Count} handler(s), "
    + $"{bases[0].Methods.Count} + {bases[1].Methods.Count} method(s), "
    + $"{conditions.Count} condition(s), {valueProviders.Count} value provider(s))");
return 0;

// The public virtuals a modder can override: declared on the type itself (not the engine
// base), virtual, not sealed, not a property/event accessor.
static IReadOnlyList<HandlerMethodDoc> OverridableMethods(Type type) =>
    type.GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly)
        .Where(m => m.IsVirtual && !m.IsFinal && !m.IsSpecialName)
        .Select(m => new HandlerMethodDoc(m.Name, RenderSignature(m)))
        .ToList();

// The set of type names from the concrete handler up to and including the template base, so
// members declared above it (UnityEngine.Object's name/hideFlags, Odin's serializationData)
// are filtered out of the authoring surface.
static HashSet<string> OwnDeclaringTypeNames(Type type, Type templateBase)
{
    var names = new HashSet<string>(StringComparer.Ordinal);
    for (var cur = type; cur is not null; cur = cur.BaseType)
    {
        if (cur.FullName is not null) names.Add(cur.FullName);
        if (cur.FullName == templateBase.FullName) break;
    }
    return names;
}

static IReadOnlyList<string> NamesOfSubtypes(TemplateTypeCatalog catalog, string baseName)
{
    var baseType = catalog.ResolveType(baseName, out _, out _);
    return baseType is null ? [] : catalog.EnumerateConcreteSubtypes(baseType).Select(t => t.Name).ToList();
}

// Match the field-type column to the C# keywords the method signatures use. FriendlyName
// keeps the BCL names (Boolean, Single, Int32); a modder writes bool/float/int.
static string PrettyPrimitives(string friendly) => friendly switch
{
    "Boolean" => "bool",
    "Int32" => "int",
    "Int64" => "long",
    "Single" => "float",
    "Double" => "double",
    "String" => "string",
    _ => friendly,
};

static string RenderSignature(MethodInfo m)
{
    var ps = m.GetParameters()
        .Select(p => $"{CleanType(p.ParameterType)} {p.Name?.TrimStart('_')}");
    return $"{CleanType(m.ReturnType)} {m.Name}({string.Join(", ", ps)})";
}

static string CleanType(Type t)
{
    if (t.IsByRef) return "ref " + CleanType(t.GetElementType()!);
    if (t.IsArray) return CleanType(t.GetElementType()!) + "[]";
    if (t.IsGenericType)
    {
        var name = t.Name;
        var tick = name.IndexOf('`');
        if (tick >= 0) name = name[..tick];
        return name + "<" + string.Join(", ", t.GetGenericArguments().Select(CleanType)) + ">";
    }
    return t.Name switch
    {
        "Void" => "void",
        "Boolean" => "bool",
        "Int32" => "int",
        "Int64" => "long",
        "Single" => "float",
        "Double" => "double",
        "String" => "string",
        _ => t.Name,
    };
}
