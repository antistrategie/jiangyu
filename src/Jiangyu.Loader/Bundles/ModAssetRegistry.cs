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
    private readonly Func<string, GameObject> _additionPrefab;
    private readonly Func<Il2CppAssetBundle, string, GameObject> _additionPrefabIn;

    private readonly Dictionary<(Type, string), object> _cache = new();
    private List<string> _names;
    // Per bundle, a lowercased short-name (no extension) to full-path index, built once.
    private Dictionary<string, string>[] _nameIndex;

    public ModAssetRegistry(
        string modId,
        IReadOnlyList<Il2CppAssetBundle> bundles,
        List<UnityEngine.Object> pinned,
        IModHostLog log,
        Func<string, GameObject> additionPrefab = null,
        Func<Il2CppAssetBundle, string, GameObject> additionPrefabIn = null)
    {
        _modId = modId;
        _bundles = bundles;
        _pinned = pinned;
        _log = log;
        _additionPrefab = additionPrefab;
        _additionPrefabIn = additionPrefabIn;
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

        // An addition prefab comes through the catalog, which rebinds its shaders and
        // restores its vanilla scripts on first load; a raw bundle load would hand back
        // the bare asset.
        if (_additionPrefab != null && typeof(T) == typeof(GameObject))
        {
            var prefab = _additionPrefab(Jiangyu.Shared.Replacements.AssetCategory.ToBundleAssetName(name));
            if (prefab != null)
                return (T)(object)prefab;
        }

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
        var lower = name.ToLowerInvariant();

        // An indexed match in ANY bundle wins over a raw-name match in any
        // bundle: the index pass runs to completion first, and only names no
        // bundle indexes reach the raw pass. The two passes are what keep the
        // lookup cheap. An index probe is a dictionary hit, so a request some
        // bundle indexes costs one native LoadAsset in total, where an
        // interleaved order costs a raw probe per bundle ahead of the match
        // (a native bundle scan whether or not that bundle holds the asset).
        // The orders differ only when two bundles claim the same name, one by
        // index and one by Unity's own resolution.
        for (var i = 0; i < _bundles.Count; i++)
        {
            if (!_nameIndex[i].TryGetValue(lower, out var fullPath))
                continue;
            if (TryLoadFrom<T>(i, fullPath, name, requestKey, typePtr, out var indexed, out var indexedAbort))
                return indexed;
            if (indexedAbort)
                return null;
        }

        // Fallback: the raw request spelling, so Unity's own short-name
        // resolution still gets a chance at names our index didn't capture.
        for (var i = 0; i < _bundles.Count; i++)
        {
            if (TryLoadFrom<T>(i, name, name, requestKey, typePtr, out var raw, out var rawAbort))
                return raw;
            if (rawAbort)
                return null;
        }

        return null;
    }

    // abort=true means the request cannot succeed in any bundle (the loaded
    // pointer would not wrap as T), so the caller stops rather than repeating
    // the same failure and warning once per bundle.
    private bool TryLoadFrom<T>(
        int bundleIndex, string candidate, string requestName,
        (Type, string) requestKey, IntPtr typePtr, out T asset, out bool abort) where T : class
    {
        asset = null;
        abort = false;

        // Cache by the resolved asset name, not the request spelling, so the
        // same asset fetched by short name and by full path shares one load and
        // one pin instead of loading and pinning it twice.
        var resolvedKey = (typeof(T), candidate);
        if (_cache.TryGetValue(resolvedKey, out var hit))
        {
            _cache[requestKey] = hit;
            asset = (T)hit;
            return true;
        }

        // A prefab named by path or leaf instead of its key still belongs to the catalog,
        // which processes it on first load; the bare bundle asset never stands in for it.
        if (_additionPrefabIn != null && typeof(T) == typeof(GameObject))
        {
            var processed = _additionPrefabIn(_bundles[bundleIndex], candidate);
            if (processed != null)
            {
                asset = (T)(object)processed;
                _cache[resolvedKey] = asset;
                _cache[requestKey] = asset;
                return true;
            }
        }

        IntPtr ptr;
        try
        {
            ptr = _bundles[bundleIndex].LoadAsset(candidate, typePtr);
        }
        catch (Exception ex)
        {
            _log.Warn($"[{_modId}] assets: loading '{requestName}' as {typeof(T).Name} threw {ex.GetType().Name}: {ex.Message}.");
            return false;
        }
        if (ptr == IntPtr.Zero)
            return false;

        try
        {
            asset = (T)Activator.CreateInstance(typeof(T), ptr);
        }
        catch (Exception ex)
        {
            _log.Warn($"[{_modId}] assets: wrapping '{requestName}' as {typeof(T).Name} threw {ex.GetType().Name}: {ex.Message}.");
            abort = true;
            return false;
        }

        if (asset is UnityEngine.Object unityObject)
        {
            unityObject.hideFlags = HideFlags.DontUnloadUnusedAsset;
            _pinned.Add(unityObject);
        }
        _cache[resolvedKey] = asset;
        _cache[requestKey] = asset;
        return true;
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
                    // First writer wins so a duplicate key resolves deterministically; the
                    // full-path key is unique per asset, so it always lands.
                    foreach (var key in NameKeys(full))
                    {
                        if (!map.ContainsKey(key))
                            map[key] = full;
                    }
                }
            }
            _nameIndex[i] = map;
        }
    }

    /// <summary>
    /// The lowercased lookup keys an asset answers to, so a UXML or prefab at
    /// <c>Assets/UI/dir/bar.uxml</c> resolves by its full path, by the category-relative
    /// path <c>dir/bar</c> (the <c>asset="dir/name"</c> convention), or by the leaf
    /// <c>bar</c>. Pure string work, exercised directly by the tests.
    /// </summary>
    internal static IEnumerable<string> NameKeys(string assetPath)
    {
        var lower = assetPath.ToLowerInvariant();
        yield return lower;

        var slash = lower.LastIndexOf('/');
        yield return StripExtension(slash >= 0 ? lower[(slash + 1)..] : lower);

        // The path under the category folder (after "Assets/<category>/"), so nested
        // assets get a stable, collision-free name that mirrors their subfolders.
        var firstSlash = lower.IndexOf('/');
        var secondSlash = firstSlash >= 0 ? lower.IndexOf('/', firstSlash + 1) : -1;
        if (secondSlash >= 0)
            yield return StripExtension(lower[(secondSlash + 1)..]);
    }

    private static string StripExtension(string name)
    {
        var dot = name.LastIndexOf('.');
        return dot > 0 ? name[..dot] : name;
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
