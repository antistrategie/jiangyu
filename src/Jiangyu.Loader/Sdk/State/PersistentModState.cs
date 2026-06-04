using System;
using System.Collections.Generic;
using System.Text.Encodings.Web;
using System.Text.Json;
using Jiangyu.Sdk;

namespace Jiangyu.Loader.Sdk.State;

/// <summary>
/// A mod's per-save-slot state. The mod mutates the live blobs returned by
/// <see cref="Get{T}"/>; the loader serialises them to a sidecar next to the save
/// file when the game saves, and reloads them when the game loads. Blobs are
/// deserialised lazily on first <see cref="Get{T}"/> so a mod only pays for the
/// types it asks for, and types it never reads survive a save unchanged.
/// </summary>
internal sealed class PersistentModState : IModState
{
    private static readonly JsonSerializerOptions Options = new()
    {
        IncludeFields = true,
        WriteIndented = true,
        // Write '+' (nested-type separator) and the like as themselves, not \uXXXX.
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    private readonly Dictionary<Type, object> _live = new();

    // Raw JSON loaded from the sidecar, keyed by type full name, not yet realised.
    private Dictionary<string, JsonElement> _pending;

    public T Get<T>() where T : class, new()
    {
        if (_live.TryGetValue(typeof(T), out var existing))
            return (T)existing;

        T blob = null;
        if (_pending != null && _pending.TryGetValue(typeof(T).FullName ?? typeof(T).Name, out var raw))
        {
            try { blob = raw.Deserialize<T>(Options); }
            catch { blob = null; }
        }

        blob ??= new T();
        _live[typeof(T)] = blob;
        return blob;
    }

    /// <summary>State persists automatically when the game saves, so this is not
    /// required; it is a no-op kept for API stability.</summary>
    public void Save() { }

    public bool HasState => _live.Count > 0 || (_pending != null && _pending.Count > 0);

    /// <summary>Serialise the live blobs (and any loaded-but-unread ones) to JSON: a
    /// type-full-name to blob-object map, each blob a nested object.</summary>
    public string Serialize()
    {
        var map = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
        if (_pending != null)
            foreach (var pair in _pending)
                map[pair.Key] = pair.Value;
        foreach (var pair in _live)
            map[pair.Key.FullName ?? pair.Key.Name] = JsonSerializer.SerializeToElement(pair.Value, pair.Key, Options);
        return JsonSerializer.Serialize(map, Options);
    }

    /// <summary>Replace the state from a sidecar's JSON; blobs realise lazily on Get.</summary>
    public void Load(string json)
    {
        _live.Clear();
        _pending = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(json, Options)
            ?? new Dictionary<string, JsonElement>(StringComparer.Ordinal);
    }
}
