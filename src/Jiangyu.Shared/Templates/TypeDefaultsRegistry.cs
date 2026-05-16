namespace Jiangyu.Shared.Templates;

/// <summary>
/// Per-type default values applied to a freshly-allocated composite
/// instance before modder-authored ops run. Closes the gap between the
/// C# class definition (where field initialisers don't exist for these
/// types) and the Inspector-baked values vanilla assets ship with.
///
/// Without this registry, a fresh <c>Stem.SoundVariation</c> has
/// <c>fixedVolume=0, fixedPitch=0</c> from raw allocation, and playback
/// is silent until the modder explicitly sets them. The registry seeds
/// those fields to vanilla defaults; modder ops in the composite body
/// still override.
///
/// Bypassed on the prototype-source (<c>from=</c>) construction path:
/// that path deep-copies from a vanilla element which already has the
/// right values, so applying defaults again would just do extra work.
/// </summary>
public static class TypeDefaultsRegistry
{
    // Type FQN -> { field name -> boxed default value }. Both the
    // Il2Cpp-prefixed and unwrapped names are listed so lookups work
    // regardless of how the resolver reports the type. Values are stored
    // boxed; the applier coerces them to the destination property's
    // declared type at write time.
    private static readonly Dictionary<string, Dictionary<string, object>> Defaults = new(StringComparer.Ordinal)
    {
        ["Il2CppStem.Sound"] = new()
        {
            ["fixedVolume"] = 1.0f,
            ["fixedPitch"] = 1.0f,
            ["dopplerLevel"] = 1.0f,
            ["minDistance"] = 1.0f,
            ["maxDistance"] = 500.0f,
        },
        ["Stem.Sound"] = new()
        {
            ["fixedVolume"] = 1.0f,
            ["fixedPitch"] = 1.0f,
            ["dopplerLevel"] = 1.0f,
            ["minDistance"] = 1.0f,
            ["maxDistance"] = 500.0f,
        },
        ["Il2CppStem.SoundVariation"] = new()
        {
            ["fixedVolume"] = 1.0f,
            ["fixedPitch"] = 1.0f,
        },
        ["Stem.SoundVariation"] = new()
        {
            ["fixedVolume"] = 1.0f,
            ["fixedPitch"] = 1.0f,
        },
    };

    public static IReadOnlyDictionary<string, object>? TryGetDefaultsFor(string typeFqn)
    {
        if (string.IsNullOrEmpty(typeFqn)) return null;
        return Defaults.TryGetValue(typeFqn, out var fields) ? fields : null;
    }
}
