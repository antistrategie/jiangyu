using System.Reflection;

namespace Jiangyu.Studio.Rpc;

/// <summary>
/// Markdown reference docs embedded in this assembly via
/// <c>EmbeddedResource Include="..\..\site\**\*.md"</c>. Loaded lazily once
/// at startup; both the MCP <c>resources/list</c> /
/// <c>resources/read</c> path and the <c>jiangyu_docs_*</c> tool wrappers
/// read through here. Tool wrappers exist because some MCP clients (notably
/// current GitHub Copilot builds) don't surface MCP resources to the agent
/// — exposing the same content as tools makes it cross-client.
/// </summary>
internal static class EmbeddedDocs
{
    private const string Prefix = "Jiangyu.Studio.Rpc.docs.";
    private static readonly Assembly LibraryAssembly = typeof(EmbeddedDocs).Assembly;

    public static IReadOnlyList<DocEntry> All { get; } = Discover();

    public static string? Read(string key)
    {
        var resourceName = Prefix + key;
        using var stream = LibraryAssembly.GetManifestResourceStream(resourceName);
        if (stream is null) return null;
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }

    private static List<DocEntry> Discover()
    {
        var result = new List<DocEntry>();
        foreach (var name in LibraryAssembly.GetManifestResourceNames())
        {
            if (!name.StartsWith(Prefix, StringComparison.Ordinal)) continue;
            if (!name.EndsWith(".md", StringComparison.Ordinal)) continue;

            var key = name[Prefix.Length..];
            result.Add(new DocEntry(key, DeriveFriendlyName(key)));
        }
        result.Sort((a, b) => string.Compare(a.Key, b.Key, StringComparison.Ordinal));
        return result;
    }

    private static string DeriveFriendlyName(string key)
    {
        var resourceName = Prefix + key;
        using var stream = LibraryAssembly.GetManifestResourceStream(resourceName);
        if (stream is not null)
        {
            using var reader = new StreamReader(stream);
            for (var i = 0; i < 20; i++)
            {
                var line = reader.ReadLine();
                if (line is null) break;
                if (line.StartsWith("# ", StringComparison.Ordinal))
                    return line[2..].Trim();
            }
        }

        var stem = key.EndsWith(".md", StringComparison.Ordinal) ? key[..^3] : key;
        return string.Join(' ', stem.Split('.').Select(
            s => s.Length > 0 ? char.ToUpperInvariant(s[0]) + s[1..].Replace('-', ' ') : s));
    }
}

internal readonly record struct DocEntry(string Key, string Name);
