using System.Text.Json;
using Jiangyu.Core.Models;

namespace Jiangyu.Core.Assets;

/// <summary>
/// Shared envelope for "is this cache file current?" checks. Each cache
/// (asset index, template index, structural baseline) has its own manifest
/// shape and its own staleness predicate. The envelope around the predicate
/// is identical: confirm the cache file exists, deserialise the manifest,
/// check non-null, then run the predicate.
///
/// Callers supply the predicate that turns a deserialised manifest into a
/// stale-reason string (or null when the manifest is current). The static
/// reasons surfaced for the missing or unreadable cases are caller-specific
/// because the modder-facing "rebuild" command differs (<c>jiangyu assets
/// index</c>, <c>jiangyu templates index</c>).
/// </summary>
internal static class IndexCacheValidator
{
    /// <summary>
    /// Resolve a <see cref="CachedIndexStatus"/> for a manifest-backed cache.
    /// </summary>
    /// <param name="cacheFilePath">The cache payload's file path. The cache
    /// is reported <see cref="CachedIndexState.Missing"/> when this file is
    /// absent regardless of the manifest's state.</param>
    /// <param name="manifestFilePath">The manifest's file path. The cache
    /// is reported missing when this file is absent too; manifest reads
    /// happen through <paramref name="loadManifest"/>.</param>
    /// <param name="loadManifest">Reads and deserialises the manifest from
    /// <paramref name="manifestFilePath"/>. Returns null on deserialise
    /// failure; the caller's <paramref name="unreadableManifestReason"/> is
    /// surfaced in that case.</param>
    /// <param name="staleReason">Inspects the deserialised manifest. Returns
    /// null when the manifest is current; otherwise returns a modder-facing
    /// reason string. The reason is surfaced verbatim on the result.</param>
    /// <param name="missingReason">Stale-reason string for the missing-file
    /// case. Usually points modders at the rebuild command.</param>
    /// <param name="unreadableManifestReason">Stale-reason string for the
    /// unreadable-manifest case.</param>
    public static CachedIndexStatus Validate<TManifest>(
        string cacheFilePath,
        string manifestFilePath,
        Func<string, TManifest?> loadManifest,
        Func<TManifest, string?> staleReason,
        string missingReason,
        string unreadableManifestReason)
        where TManifest : class
    {
        if (!File.Exists(cacheFilePath) || !File.Exists(manifestFilePath))
        {
            return new CachedIndexStatus
            {
                State = CachedIndexState.Missing,
                Reason = missingReason,
            };
        }

        TManifest? manifest;
        try
        {
            manifest = loadManifest(manifestFilePath);
        }
        catch (JsonException)
        {
            manifest = null;
        }

        if (manifest is null)
        {
            return new CachedIndexStatus
            {
                State = CachedIndexState.Stale,
                Reason = unreadableManifestReason,
            };
        }

        string? reason = staleReason(manifest);
        if (reason is not null)
        {
            return new CachedIndexStatus
            {
                State = CachedIndexState.Stale,
                Reason = reason,
            };
        }

        return new CachedIndexStatus
        {
            State = CachedIndexState.Current,
        };
    }
}
