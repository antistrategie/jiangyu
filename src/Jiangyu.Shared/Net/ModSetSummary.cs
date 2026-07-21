using System.Security.Cryptography;
using Jiangyu.Shared.Bundles;
using Jiangyu.Shared.Templates;

namespace Jiangyu.Shared.Net;

/// <summary>
/// One peer's replication-relevant identity: wire protocol, game build, loader version,
/// and the ordered mod set. Two peers whose summaries differ cannot share a sim, so the
/// handshake compares them field by field (<see cref="HandshakeComparer"/>) and rejects
/// on any difference.
/// </summary>
public sealed class ModSetSummary
{
    public int NetProtocol { get; set; }
    public string GameBuild { get; set; } = string.Empty;
    public string LoaderVersion { get; set; } = string.Empty;

    /// <summary>Mods in load order. Order participates in the comparison: bundle load
    /// order decides conflict winners, so a reordered set is a different sim.</summary>
    public List<ModSetEntry> Mods { get; set; } = [];
}

/// <summary>One mod in a peer's load order.</summary>
public sealed class ModSetEntry
{
    public string Name { get; set; } = string.Empty;
    public string Version { get; set; } = string.Empty;

    /// <summary>SHA-256 (hex) over the mod's manifest and compiled template program.
    /// Bundles are not hashed: at matching name, version, and template program, a
    /// bundle-level difference is a packaging error out of handshake scope.</summary>
    public string ManifestHash { get; set; } = string.Empty;
}

/// <summary>Builds the local <see cref="ModSetSummary"/> from a discovered mod load plan.</summary>
public static class ModSetSummaryBuilder
{
    public static ModSetSummary Build(ModLoadPlan plan, string gameBuild, string loaderVersion)
    {
        var summary = new ModSetSummary
        {
            NetProtocol = NetProtocol.Version,
            GameBuild = gameBuild,
            LoaderVersion = loaderVersion,
        };

        foreach (var mod in plan.LoadableMods)
        {
            summary.Mods.Add(new ModSetEntry
            {
                Name = mod.Name,
                Version = mod.Version,
                ManifestHash = HashModContent(mod),
            });
        }

        return summary;
    }

    private static string HashModContent(DiscoveredMod mod)
    {
        using var sha = SHA256.Create();
        AppendFile(sha, mod.ManifestPath);
        AppendFile(sha, Path.Combine(mod.DirectoryPath, CompiledTemplatePatchManifest.FileName));
        sha.TransformFinalBlock([], 0, 0);
        return ToHex(sha.Hash!);
    }

    private static void AppendFile(SHA256 sha, string path)
    {
        if (!File.Exists(path))
            return;
        var bytes = File.ReadAllBytes(path);
        sha.TransformBlock(bytes, 0, bytes.Length, null, 0);
    }

    private static string ToHex(byte[] hash)
    {
        var chars = new char[hash.Length * 2];
        for (var i = 0; i < hash.Length; i++)
        {
            chars[i * 2] = "0123456789abcdef"[hash[i] >> 4];
            chars[i * 2 + 1] = "0123456789abcdef"[hash[i] & 0xF];
        }

        return new string(chars);
    }
}
