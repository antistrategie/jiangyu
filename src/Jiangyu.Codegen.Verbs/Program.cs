using System.Reflection;
using System.Text.Json;
using Jiangyu.Codegen.Verbs;
using Jiangyu.Core.Validation;

// jiangyu-codegen-verbs <manifest.json> <outputDir> [--update-surface]
//
// Reflects the game's Il2CppInterop assembly (read-only, no execution), resolves each
// manifest entry against it, and emits a thin verb wrapper per group into <outputDir>.
// A manifest entry whose type/method no longer resolves is reported and forces a
// non-zero exit -- that is the game-update contract check: a patch that renamed or
// removed a target surfaces here, not in a modder's crash. The pure emission lives in
// VerbEmit (unit-tested); this file is the reflection + IO around it. After emitting,
// the surface baseline reports what the bound types gained or lost (candidate new
// verbs); --update-surface rewrites that committed baseline.

if (args.Length < 2)
{
    Console.Error.WriteLine("usage: jiangyu-codegen-verbs <manifest.json> <outputDir> [--update-surface]");
    return 2;
}
var manifestPath = args[0];
var outputDir = args[1];
var updateSurface = args.Contains("--update-surface");
// All baseline results live together in the repo's validation/ directory (alongside the
// structural baseline), resolved relative to the working directory like the .g.cs output.
var baselinePath = Path.Combine("validation", "verb-surface-baseline.json");

using var loaded = GameAssembly.Load(out var loadError);
if (loaded is null)
{
    Console.Error.WriteLine($"verbgen: {loadError}");
    return 2;
}
var game = loaded.Assembly;

// manifestPath is a single .json file or a directory of them (each domain owns one,
// so they can be authored in parallel without colliding); all `verbs` arrays merge.
var jsonOptions = new JsonSerializerOptions { PropertyNameCaseInsensitive = true };
var manifestFiles = Directory.Exists(manifestPath)
    ? Directory.EnumerateFiles(manifestPath, "*.json").OrderBy(p => p, StringComparer.Ordinal).ToList()
    : new List<string> { manifestPath };
var verbs = new List<VerbEntry>();
foreach (var mf in manifestFiles)
{
    var m = JsonSerializer.Deserialize<Manifest>(File.ReadAllText(mf), jsonOptions);
    if (m?.Verbs != null) verbs.AddRange(m.Verbs);
}

var byGroup = new Dictionary<string, List<IReadOnlyList<string>>>(StringComparer.Ordinal);
var errors = new List<string>();

foreach (var v in verbs)
{
    var onType = game.GetType(v.OnType);
    if (onType is null)
    {
        errors.Add($"{v.Group}.{v.Name}: type '{v.OnType}' not found (game update?)");
        continue;
    }
    var method = onType
        .GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static)
        .FirstOrDefault(m => m.Name == v.Method);
    if (method is null)
    {
        errors.Add($"{v.Group}.{v.Name}: method '{v.OnType}.{v.Method}' not found (game update?)");
        continue;
    }

    var paramTypeNames = method.GetParameters().Select(p => TypeNames.Of(p.ParameterType)).ToList();
    var member = VerbEmit.EmitMember(
        v.Name, method.Name, v.Receiver, v.Mutating,
        TypeNames.Of(method.ReturnType), TypeNames.Of(onType), paramTypeNames, v.Summary);

    if (!byGroup.TryGetValue(v.Group, out var members))
        byGroup[v.Group] = members = new();
    members.Add(member);
}

if (errors.Count > 0)
{
    Console.Error.WriteLine($"verbgen: {errors.Count} unresolved binding(s) -- game surface may have changed:");
    foreach (var e in errors) Console.Error.WriteLine($"  - {e}");
    return 1;
}

Directory.CreateDirectory(outputDir);
foreach (var (group, members) in byGroup)
{
    var file = Path.Combine(outputDir, $"{group}.g.cs");
    File.WriteAllText(file, VerbEmit.EmitFile(group, members));
    Console.WriteLine($"verbgen: wrote {file} ({members.Count} verb(s))");
}

SurfaceBaseline.CheckOrUpdate(game, verbs.Select(v => v.OnType), baselinePath, updateSurface, Console.WriteLine);

return 0;

sealed record Manifest(VerbEntry[] Verbs);
sealed record VerbEntry(string Group, string Name, string OnType, string Method, string Receiver, string? Summary, bool Mutating = false);
