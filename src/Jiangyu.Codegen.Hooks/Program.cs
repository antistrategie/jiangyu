using System.Reflection;
using System.Text.Json;
using Jiangyu.Codegen.Hooks;
using Jiangyu.Core.Validation;

// jiangyu-codegen-hooks <manifestDirOrFile> <outputFile> [--update-surface]
//
// Resolves each hook anchor in the manifest against the game's Il2CppInterop assembly
// (read-only, no execution) and emits the hook catalogue the SDK ships. A hook whose
// owner type, event accessor, or patched method no longer resolves is reported and
// forces a non-zero exit -- the game-update contract check for the hook surface. The
// event hooks are also compile-checked by the loader (it references the typed
// accessors); the string-named Harmony postfixes are not, so this is their only guard.
// After emitting, the surface baseline reports what the bound owner types gained or
// lost (candidate new hooks); --update-surface rewrites that committed baseline.

if (args.Length < 2)
{
    Console.Error.WriteLine("usage: jiangyu-codegen-hooks <manifestDirOrFile> <outputFile> [--update-surface]");
    return 2;
}
var manifestPath = args[0];
var outputFile = args[1];
var updateSurface = args.Contains("--update-surface");
// All baseline results live together in the repo's validation/ directory (alongside the
// structural baseline), resolved relative to the working directory like the catalogue output.
var baselinePath = Path.Combine("validation", "hook-surface-baseline.json");

using var loaded = GameAssembly.Load(out var loadError);
if (loaded is null)
{
    Console.Error.WriteLine($"hookgen: {loadError}");
    return 2;
}
var game = loaded.Assembly;

// manifestPath is a single .json file or a directory of them (one per layer); all
// `hooks` arrays merge.
var jsonOptions = new JsonSerializerOptions { PropertyNameCaseInsensitive = true };
var manifestFiles = Directory.Exists(manifestPath)
    ? Directory.EnumerateFiles(manifestPath, "*.json").OrderBy(p => p, StringComparer.Ordinal).ToList()
    : new List<string> { manifestPath };
var entries = new List<HookEntry>();
foreach (var mf in manifestFiles)
{
    var m = JsonSerializer.Deserialize<HookManifest>(File.ReadAllText(mf), jsonOptions);
    if (m?.Hooks != null) entries.AddRange(m.Hooks);
}

const BindingFlags AnyMethod =
    BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static;

var errors = new List<string>();
var models = new List<HookModel>();
foreach (var e in entries)
{
    var anchor = "";
    if (e.Kind is "event" or "postfix")
    {
        var owner = game.GetType(e.Owner);
        if (owner is null)
        {
            errors.Add($"{e.Layer}.{e.Name}: owner type '{e.Owner}' not found (game update?)");
            continue;
        }
        anchor = $"{e.Owner.Split('.')[^1]}.{e.Member}";

        if (e.Kind == "event")
        {
            if (owner.GetMethod("add_" + e.Member, AnyMethod) is null)
            {
                errors.Add($"{e.Layer}.{e.Name}: event accessor '{e.Owner}.add_{e.Member}' not found (game update?)");
                continue;
            }
        }
        else if (!owner.GetMethods(AnyMethod).Any(m => m.Name == e.Member))
        {
            errors.Add($"{e.Layer}.{e.Name}: method '{e.Owner}.{e.Member}' not found (game update?)");
            continue;
        }
    }

    var payload = (e.Payload ?? Array.Empty<HookPayloadEntry>())
        .Select(p => new HookPayload(p.Name, p.Type, p.Summary ?? ""))
        .ToList();
    models.Add(new HookModel(e.Layer, e.Name, e.Context, e.Kind, anchor, e.Summary ?? "", payload));
}

if (errors.Count > 0)
{
    Console.Error.WriteLine($"hookgen: {errors.Count} unresolved hook anchor(s) -- game surface may have changed:");
    foreach (var er in errors) Console.Error.WriteLine($"  - {er}");
    return 1;
}

Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(outputFile))!);
File.WriteAllText(outputFile, HookEmit.EmitCatalog(models));
Console.WriteLine($"hookgen: wrote {outputFile} ({models.Count} hook(s))");

var ownerTypes = entries.Where(e => e.Kind is "event" or "postfix").Select(e => e.Owner);
SurfaceBaseline.CheckOrUpdate(game, ownerTypes, baselinePath, updateSurface, Console.WriteLine);

return 0;

sealed record HookManifest(HookEntry[] Hooks);
sealed record HookEntry(
    string Layer, string Name, string Context, string Kind,
    string Owner, string Member, string? Summary, HookPayloadEntry[]? Payload);
sealed record HookPayloadEntry(string Name, string Type, string? Summary);
