using System.Text.Json;
using Jiangyu.Shared.Bundles;
using Jiangyu.Shared.Templates;
using MelonLoader;

namespace Jiangyu.Loader.Templates;

/// <summary>
/// Reads compiled <c>templateBindings</c> directives out of each loadable
/// mod's jiangyu.json. Bindings are pure Jiangyu metadata: they wire
/// cross-template runtime behaviour (e.g. leader→armor visual dispatch
/// override) without mutating MENACE's own template fields. Each binding
/// has a kind discriminator and a flat attribute bag; consumers
/// (e.g. <see cref="RuntimeActorVisualRefreshPatch"/>) filter by kind and
/// read the attributes they expect.
/// </summary>
internal sealed class TemplateBindingCatalog
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
    };

    private readonly List<LoadedBinding> _bindings = new();

    public IReadOnlyList<LoadedBinding> Bindings => _bindings;
    public bool HasBindings => _bindings.Count > 0;

    public IEnumerable<LoadedBinding> ByKind(string kind)
    {
        foreach (var b in _bindings)
            if (string.Equals(b.Kind, kind, StringComparison.Ordinal))
                yield return b;
    }

    public void Load(IReadOnlyList<DiscoveredMod> loadableMods, MelonLogger.Instance log)
    {
        foreach (var mod in loadableMods)
            LoadFromMod(mod, log);

        if (_bindings.Count == 0)
            return;

        var kindSummaries = _bindings
            .GroupBy(b => b.Kind, StringComparer.Ordinal)
            .OrderBy(g => g.Key, StringComparer.Ordinal)
            .Select(g => $"{g.Key}: {g.Count()}");

        log.Msg($"Loaded {_bindings.Count} template binding(s): {string.Join("; ", kindSummaries)}.");
    }

    private void LoadFromMod(DiscoveredMod mod, MelonLogger.Instance log)
    {
        if (string.IsNullOrEmpty(mod.ManifestPath) || !File.Exists(mod.ManifestPath))
            return;

        CompiledTemplatePatchManifest manifest;
        try
        {
            var json = File.ReadAllText(mod.ManifestPath);
            manifest = JsonSerializer.Deserialize<CompiledTemplatePatchManifest>(json, JsonOptions);
        }
        catch (Exception ex)
        {
            log.Error($"Mod '{mod.Name}': failed to read template bindings: {ex.Message}");
            return;
        }

        var bindings = manifest?.TemplateBindings;
        if (bindings == null || bindings.Count == 0)
            return;

        foreach (var directive in bindings)
        {
            if (directive == null) continue;
            if (string.IsNullOrWhiteSpace(directive.Kind))
            {
                log.Warning($"Mod '{mod.Name}': binding with empty kind; skipped.");
                continue;
            }

            _bindings.Add(new LoadedBinding(
                directive.Kind,
                directive.Attributes ?? new Dictionary<string, string>(StringComparer.Ordinal),
                mod.Name));
        }
    }
}

internal sealed class LoadedBinding
{
    public LoadedBinding(string kind, IReadOnlyDictionary<string, string> attributes, string ownerLabel)
    {
        Kind = kind;
        Attributes = attributes;
        OwnerLabel = ownerLabel;
    }

    public string Kind { get; }
    public IReadOnlyDictionary<string, string> Attributes { get; }
    public string OwnerLabel { get; }

    public string Get(string key) => Attributes.TryGetValue(key, out var v) ? v : null;
}
