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
// polymorphic families (conditions, value providers, filters) that fill a handler's
// interface-typed fields. The pure rendering lives in HandlerDocEmit (unit-tested); this
// file is the reflection + IO around it. A base type that no longer resolves forces a
// non-zero exit -- the game-update contract check.

const string HandlerBaseName = "Il2CppMenace.Tactical.Skills.SkillEventHandler";
const string TemplateBaseName = "Il2CppMenace.Tactical.Skills.SkillEventHandlerTemplate";
const string ConditionBaseName = "Il2CppMenace.Tactical.Skills.TacticalCondition";

// Each family: the section title, the interface a handler field is typed with (the slot),
// the type whose concrete subtypes fill the slot (a class base when the game has one, so
// its own fields render once as the shared table; otherwise the interface itself), the
// intro prose and the example.
var familySpecs = new[]
{
    new FamilySpec(
        "Conditions",
        "Il2CppMenace.Tactical.Skills.ITacticalCondition",
        ConditionBaseName,
        "A condition gates a handler: the handler only acts when the condition holds. A handler field typed `ITacticalCondition` takes one of these subtypes, and a field typed `ITacticalCondition[]` takes a list of them. Name the subtype with `type=\"<Name>\"` and set its fields, the same way as a handler:",
        """
        set "EventHandlers" index=0 {
            set "Condition" type="EntityWithTagsCondition" {
                set "CheckEntity" "Target"
                append "RequiredTags" "LARGE"
            }
        }
        """),
    new FamilySpec(
        "Value providers",
        "Il2CppMenace.Tactical.Skills.Effects.IValueProvider",
        "Il2CppMenace.Tactical.Skills.Effects.IValueProvider",
        "A value provider computes a number for a handler field typed `IValueProvider` (for example `ChangeProperty.ValueProvider`). Name the subtype with `type=\"<Name>\"` and set its fields:",
        """
        set "EventHandlers" index=0 {
            set "ValueProvider" type="CoverValueProvider" {
                set "HeavyCoverValue" 3
            }
        }
        """),
    new FamilySpec(
        "Skill filters",
        "Il2CppMenace.Tactical.Skills.SkillFilters.ISkillFilter",
        "Il2CppMenace.Tactical.Skills.SkillFilters.ISkillFilter",
        "A skill filter narrows which skills a handler applies to. A handler field typed `ISkillFilter` takes one of these subtypes. Name it with `type=\"<Name>\"` and set its fields.",
        null),
    new FamilySpec(
        "Item filters",
        "Il2CppMenace.Tactical.Skills.ItemFilters.IItemFilter",
        "Il2CppMenace.Tactical.Skills.ItemFilters.IItemFilter",
        "An item filter narrows which items a handler applies to. A handler field typed `IItemFilter` takes one of these subtypes. Name it with `type=\"<Name>\"` and set its fields.",
        null),
};

if (args.Length < 1)
{
    Console.Error.WriteLine("usage: jiangyu-codegen-handlers <outputDir>");
    return 2;
}
var outputDir = args[0];

var config = GlobalConfig.Load();
var (gameDir, _) = GlobalConfig.ResolveGamePath(config);
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

// The supplement lists the managed classes that implement each interface family (value
// providers, filters). Without it those families come out empty and every slot would render
// as C# only, and a supplement older than the game binaries would list the previous build's
// subtypes, so only a fresh one is accepted. The template index writes it.
var (dataDir, dataError) = GlobalConfig.ResolveGameDataPath(config);
if (dataDir is null)
{
    Console.Error.WriteLine($"handlergen: {dataError}");
    return 2;
}
if (!Il2CppMetadataCache.TryResolveGamePaths(dataDir, out var gameAssemblyPath, out var metadataPath))
{
    Console.Error.WriteLine($"handlergen: GameAssembly or global-metadata.dat not found under {gameDir}");
    return 2;
}
var supplement = Il2CppMetadataCache.LoadIfFresh(config.GetCachePath(), gameAssemblyPath, metadataPath);
if (supplement is null)
{
    Console.Error.WriteLine("handlergen: IL2CPP metadata supplement is missing or older than the game binaries. Run 'jiangyu templates index' first.");
    return 2;
}

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

// Resolve the families first: a handler field row links to the family whose slot type it
// is assignable to, and only when that family has subtypes to pick from. A slot outside
// every family that nonetheless has shipped implementations is a family this file does
// not know about yet; generation stops rather than documenting the slot as C# only.
var families = new List<ResolvedFamily>();
var unmodelledSlots = new List<string>();
foreach (var spec in familySpecs)
{
    var slotType = catalog.ResolveType(spec.SlotTypeName, out _, out _);
    var subtypeBase = catalog.ResolveType(spec.SubtypeBaseName, out _, out _);
    if (slotType is null || subtypeBase is null)
    {
        Console.Error.WriteLine($"handlergen: family '{spec.Title}' types not found (game update?): "
            + $"{spec.SlotTypeName}={(slotType is null ? "missing" : "ok")} "
            + $"{spec.SubtypeBaseName}={(subtypeBase is null ? "missing" : "ok")}");
        return 1;
    }
    // The subtypes are whatever fills the slot, which is what type= resolves against. A
    // class base only says which of them share its fields.
    families.Add(new ResolvedFamily(spec, slotType, subtypeBase, catalog.EnumerateConstructibleElementSubtypes(slotType)));
}

// A family's subtypes come entirely from the supplement's interface-implementation
// table. An empty family means that table is missing them, and rendering would mark
// every slot of that family C# only, so it is a failure rather than a page.
var emptyFamilies = families.Where(f => f.Subtypes.Count == 0).ToList();
if (emptyFamilies.Count > 0)
{
    Console.Error.WriteLine("handlergen: the supplement lists no implementations for these families. Rebuild the template index and check the IL2CPP metadata extract:");
    foreach (var family in emptyFamilies)
        Console.Error.WriteLine($"  {family.Spec.Title}: {family.Spec.SlotTypeName}");
    return 1;
}

var handlerTypes = catalog.EnumerateConcreteSubtypes(templateBase);
var handlers = handlerTypes
    .Select(type => new HandlerDoc(HandlerTypeWalk.DisplayName(type, handlerTypes), FieldsDeclaredBetween(type, templateBase, inclusiveBase: true)))
    .ToList();

var familyDocs = new List<FamilyDoc>();
foreach (var family in families)
{
    // A class base (TacticalCondition) contributes the fields every subtype shares. A family
    // whose subtype base is the slot interface itself has none. (Il2CppInterop proxies do not
    // carry IsInterface, so the spec's shape is the test, not the CLR flag.)
    var shared = family.SubtypeBase == family.SlotType
        ? []
        : FieldsDeclaredBetween(family.SubtypeBase, family.SubtypeBase, inclusiveBase: true);
    // A subtype that implements the slot without deriving from the class base (AndCondition
    // composes other conditions straight off the interface) lists every field it declares.
    var subtypes = family.Subtypes
        .Select(t =>
        {
            var sharesBase = shared.Count > 0 && family.SubtypeBase.IsAssignableFrom(t);
            var stopBase = sharesBase ? family.SubtypeBase : family.SlotType;
            return new HandlerDoc(
                HandlerTypeWalk.DisplayName(t, family.Subtypes),
                FieldsDeclaredBetween(t, stopBase, inclusiveBase: false),
                SharesFamilyFields: sharesBase);
        })
        .ToList();
    familyDocs.Add(new FamilyDoc(family.Spec.Title, family.Spec.Intro, family.Spec.Example, shared, subtypes));
}

if (unmodelledSlots.Count > 0)
{
    Console.Error.WriteLine("handlergen: interface-typed slots with shipped implementations belong to no modelled family. Add a FamilySpec for each:");
    foreach (var slot in unmodelledSlots.Distinct(StringComparer.Ordinal).OrderBy(x => x, StringComparer.Ordinal))
        Console.Error.WriteLine($"  {slot}");
    return 1;
}

var model = new HandlerModel(bases, handlers, familyDocs);

Directory.CreateDirectory(outputDir);
var file = Path.Combine(outputDir, "event-handlers.md");
File.WriteAllText(file, HandlerDocEmit.Emit(model));
Console.WriteLine($"handlergen: wrote {file} ({handlers.Count} handler(s), "
    + $"{bases[0].Methods.Count} + {bases[1].Methods.Count} method(s), "
    + string.Join(", ", familyDocs.Select(f => $"{f.Subtypes.Count} {f.Title.ToLowerInvariant()}")) + ")");
return 0;

// The KDL-settable fields declared on `type` and its bases up to `stopBase` (with or without
// the base's own fields), each row linking to the family that fills it when it is a
// polymorphic slot. See HandlerTypeWalk.DeclaringTypeNames for where the walk stops.
IReadOnlyList<HandlerFieldDoc> FieldsDeclaredBetween(Type type, Type stopBase, bool inclusiveBase)
{
    var ownNames = HandlerTypeWalk.DeclaringTypeNames(type, stopBase, inclusiveBase);
    var fields = new List<HandlerFieldDoc>();
    foreach (var m in TemplateTypeCatalog.GetMembers(type))
    {
        if (!ownNames.Contains(m.DeclaringTypeFullName)) continue;
        var elementType = TemplateTypeCatalog.GetElementType(m.MemberType);
        var slot = elementType ?? m.MemberType;
        // An enum list (TagType[]) lists its members like an enum scalar does.
        var enumValues = slot.IsEnum ? TemplateTypeCatalog.GetEnumMemberNames(slot) : [];
        var family = families.FirstOrDefault(f => f.SlotType.IsAssignableFrom(slot));
        var linked = family is { Subtypes.Count: > 0 } ? family.Spec.Title : null;
        var codeOnly = family is null ? m.IsLikelyOdinOnly : linked is null;
        // Il2CppInterop proxies carry neither IsInterface nor IsAbstract, so the catalogue's
        // shell-shape test identifies an interface-typed slot. It runs on the element type so
        // an interface array counts too, and the constructible-subtype view leaves out the
        // shapes that are authored another way (ref= targets, asset references, catch-all
        // object types) without scanning the whole type graph for them.
        if (family is null
            && TemplateTypeCatalog.LooksLikeUnfillableShell(slot)
            && catalog.EnumerateConstructibleElementSubtypes(slot).Count > 0)
        {
            unmodelledSlots.Add($"{type.Name}.{m.Name}: {catalog.FriendlyName(slot)}");
        }
        fields.Add(new HandlerFieldDoc(
            m.Name,
            PrettyPrimitives(catalog.FriendlyName(m.MemberType)),
            codeOnly,
            enumValues,
            linked,
            IsCollection: elementType is not null));
    }
    return fields;
}

// The public virtuals a modder can override: declared on the type itself (not the engine
// base), virtual, not sealed, not a property/event accessor.
static IReadOnlyList<HandlerMethodDoc> OverridableMethods(Type type) =>
    type.GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly)
        .Where(m => m.IsVirtual && !m.IsFinal && !m.IsSpecialName)
        .Select(m => new HandlerMethodDoc(m.Name, RenderSignature(m)))
        .ToList();


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

sealed record FamilySpec(string Title, string SlotTypeName, string SubtypeBaseName, string Intro, string? Example);

sealed record ResolvedFamily(FamilySpec Spec, Type SlotType, Type SubtypeBase, IReadOnlyList<Type> Subtypes);
