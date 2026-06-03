using System;
using System.Collections.Generic;
using Il2CppInterop.Runtime;
using Jiangyu.Loader.Logging;
using Jiangyu.Sdk;
using UnityEngine;

namespace Jiangyu.Loader.Bundles;

/// <summary>
/// A mod's <see cref="IModAssets"/>: loads assets from the mod's own bundles by
/// name and type, on demand. The Il2Cpp type is resolved from <c>typeof(T)</c> so
/// the SDK can stay free of any Unity reference. Loaded assets are pinned for the
/// session and cached so repeated loads return the same wrapper.
/// </summary>
internal sealed class ModAssetRegistry : IModAssets
{
    private readonly string _modId;
    private readonly IReadOnlyList<Il2CppAssetBundle> _bundles;
    private readonly List<UnityEngine.Object> _pinned;
    private readonly IModHostLog _log;

    private readonly Dictionary<(Type, string), object> _cache = new();
    private List<string> _names;
    // Per bundle, a lowercased short-name (no extension) to full-path index, built once.
    private Dictionary<string, string>[] _nameIndex;

    public ModAssetRegistry(string modId, IReadOnlyList<Il2CppAssetBundle> bundles, List<UnityEngine.Object> pinned, IModHostLog log)
    {
        _modId = modId;
        _bundles = bundles;
        _pinned = pinned;
        _log = log;
    }

    public IReadOnlyList<string> Names
    {
        get
        {
            EnsureNameIndex();
            return _names;
        }
    }

    public bool TryLoad<T>(string name, out T asset) where T : class
    {
        asset = Load<T>(name);
        return asset != null;
    }

    public T Load<T>(string name) where T : class
    {
        if (string.IsNullOrEmpty(name))
            return null;

        var requestKey = (typeof(T), name);
        if (_cache.TryGetValue(requestKey, out var cached))
            return (T)cached;

        IntPtr typePtr;
        try
        {
            typePtr = IL2CPP.Il2CppObjectBaseToPtr(Il2CppType.From(typeof(T)));
        }
        catch (Exception ex)
        {
            _log.Warn($"[{_modId}] assets: '{typeof(T).FullName}' is not a Unity type, cannot load '{name}' ({ex.GetType().Name}).");
            return null;
        }

        EnsureNameIndex();
        for (var i = 0; i < _bundles.Count; i++)
        {
            foreach (var candidate in ResolveCandidates(i, name))
            {
                // Cache by the resolved asset name, not the request spelling, so the
                // same asset fetched by short name and by full path shares one load and
                // one pin instead of loading and pinning it twice.
                var resolvedKey = (typeof(T), candidate);
                if (_cache.TryGetValue(resolvedKey, out var hit))
                {
                    _cache[requestKey] = hit;
                    return (T)hit;
                }

                IntPtr ptr;
                try
                {
                    ptr = _bundles[i].LoadAsset(candidate, typePtr);
                }
                catch (Exception ex)
                {
                    _log.Warn($"[{_modId}] assets: loading '{name}' as {typeof(T).Name} threw {ex.GetType().Name}: {ex.Message}.");
                    continue;
                }
                if (ptr == IntPtr.Zero)
                    continue;

                T asset;
                try
                {
                    asset = (T)Activator.CreateInstance(typeof(T), ptr);
                }
                catch (Exception ex)
                {
                    _log.Warn($"[{_modId}] assets: wrapping '{name}' as {typeof(T).Name} threw {ex.GetType().Name}: {ex.Message}.");
                    return null;
                }

                if (asset is UnityEngine.Object unityObject)
                {
                    unityObject.hideFlags = HideFlags.DontUnloadUnusedAsset;
                    _pinned.Add(unityObject);
                }
                _cache[resolvedKey] = asset;
                _cache[requestKey] = asset;
                return asset;
            }
        }

        return null;
    }

    // The asset names to try for a request, most specific first: an exact full-path
    // match, then any asset whose short name matches, then the raw request so Unity's
    // own short-name resolution still gets a chance.
    private IEnumerable<string> ResolveCandidates(int bundleIndex, string name)
    {
        var index = _nameIndex[bundleIndex];
        var lower = name.ToLowerInvariant();
        if (index.TryGetValue(lower, out var fullPath))
            yield return fullPath;
        yield return name;
    }

    private void EnsureNameIndex()
    {
        if (_nameIndex != null)
            return;

        _names = new List<string>();
        _nameIndex = new Dictionary<string, string>[_bundles.Count];
        for (var i = 0; i < _bundles.Count; i++)
        {
            var map = new Dictionary<string, string>(StringComparer.Ordinal);
            var assetNames = _bundles[i].GetAllAssetNames();
            if (assetNames != null)
            {
                foreach (var full in assetNames)
                {
                    _names.Add(full);
                    var lowerFull = full.ToLowerInvariant();
                    map[lowerFull] = full;
                    var slash = lowerFull.LastIndexOf('/');
                    var stem = slash >= 0 ? lowerFull[(slash + 1)..] : lowerFull;
                    var dot = stem.LastIndexOf('.');
                    if (dot > 0)
                        stem = stem[..dot];
                    // First writer wins so a duplicate stem resolves deterministically.
                    if (!map.ContainsKey(stem))
                        map[stem] = full;
                }
            }
            _nameIndex[i] = map;
        }
    }
}

/// <summary>The assets view for a mod that ships no bundles.</summary>
internal sealed class NullModAssets : IModAssets
{
    public static readonly NullModAssets Instance = new();

    public IReadOnlyList<string> Names { get; } = Array.Empty<string>();

    public T Load<T>(string name) where T : class => null;

    public bool TryLoad<T>(string name, out T asset) where T : class
    {
        asset = null;
        return false;
    }
}
