using System.Text;
using System.Xml.Linq;
using Jiangyu.Codegen.Docs;
using Jiangyu.Sdk;

// jiangyu-codegen-docs <Jiangyu.Sdk.Menace.xml> <outputDir>
//
// Renders the generated modder reference into <outputDir> (the docs site's reference/):
// verbs.md from the SDK's XML doc (the one source covering generated and bespoke verbs
// alike) and hooks.md from the shipped HookCatalog. Both are committed and regenerated
// from the surface, so the reference cannot drift from the SDK.

if (args.Length < 2)
{
    Console.Error.WriteLine("usage: jiangyu-codegen-docs <Jiangyu.Sdk.Menace.xml> <outputDir>");
    return 2;
}
var xmlPath = args[0];
var outputDir = args[1];

if (!File.Exists(xmlPath))
{
    Console.Error.WriteLine($"docsgen: SDK XML doc not found at {xmlPath} (build Jiangyu.Sdk.Menace first).");
    return 2;
}

var verbs = ParseVerbs(xmlPath);
if (verbs.Count == 0)
{
    Console.Error.WriteLine("docsgen: no Jiangyu.Game verbs found in the XML doc.");
    return 1;
}

var ui = ParseUi(xmlPath);

Directory.CreateDirectory(outputDir);
var verbsFile = Path.Combine(outputDir, "verbs.md");
var hooksFile = Path.Combine(outputDir, "hooks.md");
var uiFile = Path.Combine(outputDir, "ui.md");
File.WriteAllText(verbsFile, DocsEmit.EmitVerbs(verbs));
File.WriteAllText(hooksFile, DocsEmit.EmitHooks(HookCatalog.All));
File.WriteAllText(uiFile, DocsEmit.EmitUi(ui));
Console.WriteLine($"docsgen: wrote {verbsFile} ({verbs.Count} verb(s)), {hooksFile} ({HookCatalog.All.Count} hook(s)), {uiFile} ({ui.Count} UI type(s))");
return 0;

static List<VerbDoc> ParseVerbs(string xmlPath)
{
    // The Tactical/Strategy verb namespaces. Jiangyu.Game.Ui and the top-level
    // extensions are separate surfaces documented elsewhere, so they are not verbs.
    string[] layers = ["Tactical", "Strategy"];
    var verbs = new List<VerbDoc>();

    foreach (var member in XDocument.Load(xmlPath).Descendants("member"))
    {
        var name = member.Attribute("name")?.Value;
        if (name is null || name.Length < 2) continue;
        var kind = name[0];
        if (kind is not ('M' or 'P')) continue;

        // id is "Jiangyu.Game.<Layer>.<Class>.<Member>" with an optional "(params)" tail.
        var id = name[2..];
        var paren = id.IndexOf('(');
        var paramList = paren >= 0 ? id[(paren + 1)..^1] : "";
        var path = (paren >= 0 ? id[..paren] : id).Split('.');
        if (path.Length != 5 || path[0] != "Jiangyu" || path[1] != "Game" || !layers.Contains(path[2]))
            continue;

        var (layer, cls, memberName) = (path[2], path[3], path[4]);
        var signature = kind == 'P' || paren < 0
            ? memberName
            : $"{memberName}({string.Join(", ", SplitParams(paramList).Select(ShortType))})";
        var summary = RenderSummary(member.Element("summary"));
        verbs.Add(new VerbDoc(layer, cls, memberName, signature, summary));
    }

    return verbs;
}

static List<UiClassDoc> ParseUi(string xmlPath)
{
    // (group, namespace prefix). Components is matched before Ui because its namespace nests
    // under Ui. The XML doc carries internal documented types (UiSite, ...) too, so only the
    // public UI surface is kept.
    (string Group, string Ns)[] groups =
    [
        ("Components", "Jiangyu.Game.Ui.Components."),
        ("Audio", "Jiangyu.Game.Audio."),
        ("Injection and helpers", "Jiangyu.Game.Ui."),
    ];
    string[] publicClasses =
        ["UI", "UiTarget", "UiSelector", "UiInjection", "UiElementExtensions", "TextButton", "ItemTile", "Flyout", "Tooltip", "Sound"];

    var byClass = new Dictionary<string, (string Group, string Summary, List<UiMemberDoc> Members)>();

    foreach (var member in XDocument.Load(xmlPath).Descendants("member"))
    {
        var name = member.Attribute("name")?.Value;
        if (name is null || name.Length < 2) continue;
        var kind = name[0];
        if (kind is not ('T' or 'M' or 'P')) continue;

        var id = name[2..];
        var paren = id.IndexOf('(');
        var paramList = paren >= 0 ? id[(paren + 1)..^1] : "";
        var full = paren >= 0 ? id[..paren] : id;

        var group = groups.FirstOrDefault(g => full.StartsWith(g.Ns, StringComparison.Ordinal));
        if (group.Ns is null) continue;
        var remainder = full[group.Ns.Length..];
        var dot = remainder.IndexOf('.');
        var cls = dot < 0 ? remainder : remainder[..dot];
        if (!publicClasses.Contains(cls)) continue;

        var summary = RenderSummary(member.Element("summary"));
        if (!byClass.TryGetValue(cls, out var acc))
            acc = (group.Group, "", new List<UiMemberDoc>());
        acc.Group = group.Group;

        if (kind == 'T')
        {
            // Only the class's own T: member sets the section summary; a nested public type (e.g.
            // Tooltip.Style) shares cls with its outer class, so its T: member must not overwrite it.
            if (dot < 0)
                acc.Summary = summary;
        }
        else if (dot >= 0)
        {
            var memberName = remainder[(dot + 1)..];
            if (memberName == "#ctor")
                memberName = cls;
            // Generic methods carry an arity marker in the doc id (Screen``1); render it as <T>.
            var tick = memberName.IndexOf("``", StringComparison.Ordinal);
            if (tick >= 0)
                memberName = memberName[..tick] + "<T>";
            var signature = kind == 'P' || paren < 0
                ? memberName
                : $"{memberName}({string.Join(", ", SplitParams(paramList).Select(ShortType))})";
            acc.Members.Add(new UiMemberDoc(signature, summary));
        }
        byClass[cls] = acc;
    }

    return byClass.Select(kv => new UiClassDoc(kv.Value.Group, kv.Key, kv.Value.Summary, kv.Value.Members)).ToList();
}

// Split a doc-id parameter list on top-level commas (generics use {} and may nest commas).
static IEnumerable<string> SplitParams(string paramList)
{
    if (string.IsNullOrEmpty(paramList)) yield break;
    var depth = 0;
    var start = 0;
    for (var i = 0; i < paramList.Length; i++)
    {
        var c = paramList[i];
        if (c is '{' or '<') depth++;
        else if (c is '}' or '>') depth--;
        else if (c == ',' && depth == 0) { yield return paramList[start..i]; start = i + 1; }
    }
    yield return paramList[start..];
}

static string ShortType(string type)
{
    type = type.Trim();
    var brace = type.IndexOf('{');
    if (brace >= 0) type = type[..brace];
    if (type.EndsWith("[]", StringComparison.Ordinal)) type = type[..^2];
    var simple = type.Contains('.') ? type[(type.LastIndexOf('.') + 1)..] : type;
    return simple switch
    {
        "Void" => "void",
        "Int32" => "int",
        "Int64" => "long",
        "Boolean" => "bool",
        "Single" => "float",
        "Double" => "double",
        "String" => "string",
        _ => simple,
    };
}

// Flatten a <summary> to text: keep text nodes, render <paramref>/<typeparamref> as the
// name, <see cref/langword> as the short symbol, and the inner text of everything else
// (<c>, <para>, ...). Without this, XElement.Value drops a <paramref name="actor"/> and
// leaves a dangling "'s effective accuracy".
static string RenderSummary(XElement? summary)
{
    if (summary is null) return "";
    var sb = new StringBuilder();
    AppendNodes(summary, sb);
    return string.Join(" ", sb.ToString().Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
}

static void AppendNodes(XElement element, StringBuilder sb)
{
    foreach (var node in element.Nodes())
    {
        if (node is XText text)
        {
            sb.Append(text.Value);
        }
        else if (node is XElement child)
        {
            switch (child.Name.LocalName)
            {
                case "paramref":
                case "typeparamref":
                    sb.Append(child.Attribute("name")?.Value);
                    break;
                case "see":
                case "seealso":
                    sb.Append(ShortCref(child.Attribute("cref")?.Value ?? child.Attribute("langword")?.Value ?? ""));
                    break;
                default:
                    AppendNodes(child, sb);
                    break;
            }
        }
    }
}

static string ShortCref(string cref)
{
    if (cref.Length > 2 && cref[1] == ':') cref = cref[2..];
    var paren = cref.IndexOf('(');
    if (paren >= 0) cref = cref[..paren];
    var name = cref.Contains('.') ? cref[(cref.LastIndexOf('.') + 1)..] : cref;
    var tick = name.IndexOf('`');
    return tick >= 0 ? name[..tick] + "<T>" : name;
}
