using System.Text.Json;

namespace Jiangyu.Studio.Rpc;

/// <summary>
/// Shared helpers for RPC handler implementations: JSON-RPC parameter
/// extraction, path normalisation, and project-sandbox validation. Lives
/// in the shared library so Studio.Host (WebView dispatcher) and
/// Jiangyu.Mcp (stdio dispatcher) call the same code.
/// </summary>
public static class RpcHelpers
{
    public static string RequireString(JsonElement? parameters, string name)
    {
        if (parameters is not { } p || !p.TryGetProperty(name, out var prop) || prop.GetString() is not { } value)
            throw new ArgumentException($"Missing '{name}' parameter");
        return value;
    }

    public static long RequireLong(JsonElement? parameters, string name)
    {
        if (parameters is not { } p || !p.TryGetProperty(name, out var prop) || !prop.TryGetInt64(out var value))
            throw new ArgumentException($"Missing '{name}' parameter");
        return value;
    }

    public static string? TryGetString(JsonElement? parameters, string name)
    {
        if (parameters is not { } p || !p.TryGetProperty(name, out var prop))
            return null;
        return prop.ValueKind == JsonValueKind.String ? prop.GetString() : null;
    }

    public static int? TryGetInt(JsonElement? parameters, string name)
    {
        if (parameters is not { } p || !p.TryGetProperty(name, out var prop))
            return null;
        return prop.ValueKind == JsonValueKind.Number && prop.TryGetInt32(out var v) ? v : null;
    }

    /// <summary>
    /// Strict: returns false when the two paths are equal. Callers use this to reject
    /// moving/copying a directory into itself; the UI-side <c>isDescendant</c> includes equality.
    /// </summary>
    public static bool IsStrictDescendantPath(string ancestor, string descendant)
        => descendant.StartsWith(ancestor + Path.DirectorySeparatorChar, StringComparison.Ordinal);

    /// <summary>
    /// The UI's path utilities and Monaco model URIs expect forward slashes on
    /// every platform. Native APIs return backslashes on Windows, so normalise
    /// at the host boundary before sending paths to the frontend.
    /// </summary>
    public static string NormaliseSeparators(string path)
        => path.Replace(Path.DirectorySeparatorChar, '/');

    /// <summary>
    /// Defence-in-depth: filesystem ops must target paths inside the currently open
    /// project. Today the frontend is trusted and would never send paths outside,
    /// but a malicious mod rendering into a shared surface or a bug in the editor
    /// could. Silent safety-net — we don't trust the client's sandboxing.
    ///
    /// Symlinks are resolved before the prefix check so a link inside the project
    /// pointing outside it (e.g. <c>Mods/external -> /etc</c>) doesn't escape. Path
    /// components above the project root (<c>..</c>) are normalised by GetFullPath.
    /// For paths that don't exist yet (a write target), we walk up the chain
    /// until we find an existing parent and resolve from there.
    /// </summary>
    public static void EnsurePathInsideProject(string path)
    {
        var root = RpcContext.ProjectRoot;
        if (root is null)
            throw new InvalidOperationException("No project open");

        var cmp = OperatingSystem.IsWindows() || OperatingSystem.IsMacOS()
            ? StringComparison.OrdinalIgnoreCase
            : StringComparison.Ordinal;

        var resolvedRoot = ResolveRealPath(root) ?? Path.GetFullPath(root);
        var resolved = ResolveRealPath(path) ?? Path.GetFullPath(path);

        if (resolved.Equals(resolvedRoot, cmp)) return;
        if (resolved.StartsWith(resolvedRoot + Path.DirectorySeparatorChar, cmp)) return;

        throw new UnauthorizedAccessException($"Path is outside the open project: {path}");
    }

    /// <summary>
    /// Best-effort canonical-path resolution: follows symlinks for the deepest
    /// existing component, then re-attaches the trailing path segments.
    /// Returns null if <paramref name="path"/> can't be normalised at all.
    /// </summary>
    private static string? ResolveRealPath(string path)
    {
        try
        {
            var full = Path.GetFullPath(path);
            // Walk up to find an existing component, since ResolveLinkTarget
            // returns null for non-existent paths.
            var existing = full;
            var trailing = "";
            while (!File.Exists(existing) && !Directory.Exists(existing))
            {
                var parent = Path.GetDirectoryName(existing);
                if (string.IsNullOrEmpty(parent) || parent == existing) return full;
                trailing = Path.Combine(Path.GetFileName(existing), trailing);
                existing = parent;
            }

            // ResolveLinkTarget returns null when the path is not a link;
            // in that case the path is already canonical at this level.
            FileSystemInfo info = Directory.Exists(existing)
                ? new DirectoryInfo(existing)
                : new FileInfo(existing);
            var target = info.ResolveLinkTarget(returnFinalTarget: true);
            var resolvedExisting = target?.FullName ?? existing;
            return string.IsNullOrEmpty(trailing)
                ? resolvedExisting
                : Path.Combine(resolvedExisting, trailing);
        }
        catch
        {
            return null;
        }
    }
}
