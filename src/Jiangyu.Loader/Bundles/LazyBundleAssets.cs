using Il2CppInterop.Runtime;
using Jiangyu.Loader.Logging;
using MelonLoader;
using UnityEngine;

namespace Jiangyu.Loader.Bundles;

/// <summary>
/// The mods' bundled textures, sprites, audio clips and addition prefabs, indexed by name
/// at start-up and loaded from their bundle the first time something asks for one. An LZ4
/// bundle mounted with LoadFromFile holds only its header and asset table in memory, so a
/// name nothing ever requests costs nothing, and a loaded asset is pinned for the session
/// and served from the cache after that. Every consumer asks by name and by type, which is
/// what keeps the index type-free: the asset table names a path, and the request's type
/// decides how the bundle loads it.
/// </summary>
internal sealed class LazyBundleAssets
{
    internal sealed class Entry
    {
        public string Key;
        public Il2CppAssetBundle Bundle;
        public string Path;
        public string Owner;
        public string Mod;
        public UnityEngine.Object Loaded;
        public bool Tried;
    }

    private readonly List<UnityEngine.Object> _pinned;
    // Every name maps to the candidates registered under it, in load order. A name can
    // legitimately have several: a mod may ship a sprite and a texture under one logical
    // name, and an image or a generated asset is indexed under every type it might load
    // as, since the table cannot know which without loading. A lookup walks the
    // candidates from the last-loaded mod back and takes the first that loads as the
    // requested type, which keeps "later mods win" and never lets a wrong-typed
    // candidate shadow a right-typed one.
    //
    // Case-insensitive throughout: Unity lowercases every asset path it writes into a
    // bundle, while the objects inside keep the case of the files they came from, and a
    // modder's KDL keeps whichever case they typed.
    private readonly Dictionary<string, List<Entry>> _textures = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, List<Entry>> _sprites = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, List<Entry>> _audio = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, List<Entry>> _prefabs = new(StringComparer.OrdinalIgnoreCase);
    // The prefab entry each addition bundle holds, so a request that names the prefab by
    // its path or leaf rather than its key still lands on the one processed object.
    private readonly Dictionary<Il2CppAssetBundle, Entry> _prefabsByBundle = new(ReferenceEqualityComparer.Instance);
    // Backing textures reached through a sprite of the same name, resolved once each.
    private readonly Dictionary<string, Texture2D> _spriteBackingTextures = new(StringComparer.OrdinalIgnoreCase);

    public LazyBundleAssets(List<UnityEngine.Object> pinned) => _pinned = pinned;

    /// <summary>Loader log for the per-load debug lines; set once bundles are read.</summary>
    public MelonLogger.Instance Log { get; set; }

    /// <summary>
    /// Runs once per addition prefab, right after its first load and before any caller sees
    /// it: (key, owner label, mod id, prefab). The catalog rebinds shaders and restores
    /// vanilla scripts here.
    /// </summary>
    public Action<string, string, string, GameObject> OnPrefabLoaded { get; set; }

    public int TextureCount => _textures.Count;
    public int SpriteCount => _sprites.Count;
    public int AudioCount => _audio.Count;
    public int PrefabCount => _prefabs.Count;

    /// <summary>A texture answers to its own name or to the name of a sprite it backs.</summary>
    public bool HasTexture(string name) => name != null && (_textures.ContainsKey(name) || _sprites.ContainsKey(name));
    public bool HasSprite(string name) => name != null && _sprites.ContainsKey(name);
    public bool HasAudioClip(string name) => name != null && _audio.ContainsKey(name);
    public bool HasAdditionPrefab(string key) => key != null && _prefabs.ContainsKey(key);

    /// <param name="owner">Display label for log lines, "&lt;mod&gt;/&lt;bundle file&gt;".</param>
    /// <param name="mod">The owning mod's id, kept apart from the label since an id may itself contain a slash.</param>
    public void RegisterTexture(string name, Il2CppAssetBundle bundle, string path, string owner, string mod, LoaderLog log)
        => Register(_textures, "texture", name, bundle, path, owner, mod, log);

    public void RegisterSprite(string name, Il2CppAssetBundle bundle, string path, string owner, string mod, LoaderLog log)
        => Register(_sprites, "sprite", name, bundle, path, owner, mod, log);

    public void RegisterAudioClip(string name, Il2CppAssetBundle bundle, string path, string owner, string mod, LoaderLog log)
        => Register(_audio, "audio", name, bundle, path, owner, mod, log);

    public void RegisterAdditionPrefab(string key, Il2CppAssetBundle bundle, string path, string owner, string mod, LoaderLog log)
    {
        var entry = Register(_prefabs, "addition prefab", key, bundle, path, owner, mod, log);
        if (entry != null)
            _prefabsByBundle[bundle] = entry;
    }

    // The entry registered, or null when the same asset was already listed.
    private static Entry Register(
        Dictionary<string, List<Entry>> map, string kind, string name,
        Il2CppAssetBundle bundle, string path, string owner, string mod, LoaderLog log)
    {
        if (!map.TryGetValue(name, out var candidates))
            map[name] = candidates = new List<Entry>();
        else
        {
            foreach (var existing in candidates)
            {
                // The same bundle can list one file under two asset types; that is one asset.
                if (existing.Owner == owner && string.Equals(existing.Path, path, StringComparison.Ordinal))
                    return null;
            }
            // Two bundles of one mod sharing a name are that mod's own candidates (the
            // compile allows a sprite and a texture to share a logical name). Another mod
            // registering the name is an override, and the later mod wins.
            var last = candidates[^1];
            if (last.Mod != mod)
                log.Warning($"  Override {kind} '{name}': later-loaded mod '{owner}' replaces '{last.Owner}'.");
        }

        var entry = new Entry { Key = name, Bundle = bundle, Path = path, Owner = owner, Mod = mod };
        candidates.Add(entry);
        return entry;
    }

    public bool TryGetTexture(string name, out Texture2D texture)
    {
        texture = null;
        if (name == null)
            return false;

        if (_textures.TryGetValue(name, out var candidates))
        {
            texture = Load<Texture2D>(candidates, null, "texture", name);
            if (texture != null)
                return true;
        }

        // JIANGYU-CONTRACT: Sprite replacement lands via in-place mutation of the
        // backing Texture2D. A game Sprite carries the same .name as its backing texture
        // for the unique-texture-backed case, the only case compile-time validation
        // accepts, so a bundle sprite's backing texture answers to the sprite's name and
        // the mutation sweep finds it by the name it matched on the live sprite. An
        // explicit texture of the same name takes precedence above.
        if (_spriteBackingTextures.TryGetValue(name, out texture))
            return texture != null;
        if (!_sprites.TryGetValue(name, out var spriteCandidates))
            return false;

        var sprite = Load<Sprite>(spriteCandidates, null, "sprite", name);
        Texture2D backing = null;
        if (sprite != null)
        {
            // The cast throws when the sprite's texture reference resolves to some other
            // object type; such a sprite simply has no reachable backing texture.
            try { backing = sprite.texture; }
            catch (Exception) { backing = null; }
            if (backing != null)
                Pin(backing);
        }
        _spriteBackingTextures[name] = backing;
        texture = backing;
        return texture != null;
    }

    public bool TryGetSprite(string name, out Sprite sprite)
    {
        sprite = null;
        return name != null
               && _sprites.TryGetValue(name, out var candidates)
               && (sprite = Load<Sprite>(candidates, null, "sprite", name)) != null;
    }

    public bool TryGetAudioClip(string name, out AudioClip clip)
    {
        clip = null;
        return name != null
               && _audio.TryGetValue(name, out var candidates)
               && (clip = Load<AudioClip>(candidates, null, "audio clip", name)) != null;
    }

    /// <summary>The prefab a KDL reference resolves to: the last-loaded mod's, across every mod.</summary>
    public bool TryGetAdditionPrefab(string key, out GameObject prefab)
        => TryGetAdditionPrefab(key, null, out prefab);

    /// <summary>
    /// The prefab under <paramref name="key"/>, limited to the bundles of <paramref name="modId"/>
    /// when given: a mod's own <c>Context.Assets</c> sees only its own bundles, so another
    /// mod's prefab of the same key never answers for it.
    /// </summary>
    public bool TryGetAdditionPrefab(string key, string modId, out GameObject prefab)
    {
        prefab = null;
        if (key == null || !_prefabs.TryGetValue(key, out var candidates))
            return false;
        prefab = Load<GameObject>(candidates, modId, "addition prefab", key, PrefabLoaded);
        return prefab != null;
    }

    /// <summary>
    /// The processed prefab of an addition bundle when <paramref name="path"/> is the asset
    /// the bundle's prefab entry names, so a load by full path or leaf through a mod's
    /// <c>Context.Assets</c> gets the same shader rebind and script restore as a load by key.
    /// </summary>
    public bool TryGetAdditionPrefab(Il2CppAssetBundle bundle, string path, string modId, out GameObject prefab)
    {
        prefab = null;
        if (bundle == null || path == null || !_prefabsByBundle.TryGetValue(bundle, out var entry))
            return false;
        if (!string.Equals(entry.Path, path, StringComparison.OrdinalIgnoreCase))
            return false;
        if (modId != null && !string.Equals(entry.Mod, modId, StringComparison.Ordinal))
            return false;
        prefab = LoadEntry<GameObject>(entry, "addition prefab", entry.Key, PrefabLoaded);
        return prefab != null;
    }

    private void PrefabLoaded(Entry entry) => OnPrefabLoaded?.Invoke(entry.Key, entry.Owner, entry.Mod, (GameObject)entry.Loaded);

    // The first candidate, from the last-loaded mod back, that loads as T.
    private T Load<T>(List<Entry> candidates, string modId, string kind, string name, Action<Entry> onFirstLoad = null)
        where T : UnityEngine.Object
    {
        for (var i = candidates.Count - 1; i >= 0; i--)
        {
            var entry = candidates[i];
            if (modId != null && !string.Equals(entry.Mod, modId, StringComparison.Ordinal))
                continue;
            var loaded = LoadEntry<T>(entry, kind, name, onFirstLoad);
            if (loaded != null)
                return loaded;
        }
        return null;
    }

    // A candidate is tried once; a miss stays a miss.
    private T LoadEntry<T>(Entry entry, string kind, string name, Action<Entry> onFirstLoad) where T : UnityEngine.Object
    {
        if (!entry.Tried)
        {
            entry.Tried = true;
            entry.Loaded = LoadFromBundle<T>(entry, kind, name);
            if (entry.Loaded != null)
                onFirstLoad?.Invoke(entry);
        }
        return entry.Loaded as T;
    }

    private T LoadFromBundle<T>(Entry entry, string kind, string name) where T : UnityEngine.Object
    {
        IntPtr ptr;
        try
        {
            ptr = entry.Bundle.LoadAsset(entry.Path, IL2CPP.Il2CppObjectBaseToPtr(Il2CppType.From(typeof(T))));
        }
        catch (Exception ex)
        {
            Log?.Warning($"  Loading {kind} '{name}' from {entry.Owner} threw {ex.GetType().Name}: {ex.Message}.");
            return null;
        }
        if (ptr == IntPtr.Zero)
            return null;

        var asset = (T)Activator.CreateInstance(typeof(T), ptr);
        Pin(asset);
        LoaderDebug.Write(Log, $"  Loaded on first use: {kind} '{name}' from {entry.Owner}.");
        return asset;
    }

    private void Pin(UnityEngine.Object asset)
    {
        asset.hideFlags = HideFlags.DontUnloadUnusedAsset;
        _pinned.Add(asset);
    }
}
