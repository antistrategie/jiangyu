namespace Jiangyu.Core.Templates;

/// <summary>
/// Line-level heuristics for KDL files. Not a parser — good enough for status
/// badges, quick scans, or UI summaries.
/// </summary>
public static class KdlHeuristics
{
    /// <summary>
    /// Whether the given line begins a KDL node whose first token equals
    /// <paramref name="nodeType"/>. Accepts any whitespace or <c>{</c> after
    /// the node name; ignores leading whitespace; skips <c>//</c> line comments
    /// and <c>/-</c> slashdash-commented nodes. Misses nodes that share a line
    /// with another node.
    /// </summary>
    public static bool IsNodeHeader(string line, string nodeType)
    {
        var trimmed = line.AsSpan().TrimStart();
        if (trimmed.Length == 0) return false;
        if (trimmed.StartsWith("//", StringComparison.Ordinal)) return false;
        if (trimmed.StartsWith("/-", StringComparison.Ordinal)) return false;
        if (!trimmed.StartsWith(nodeType, StringComparison.Ordinal)) return false;
        if (trimmed.Length == nodeType.Length) return false;
        var tail = trimmed[nodeType.Length];
        return char.IsWhiteSpace(tail) || tail == '{';
    }
}
