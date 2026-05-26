using System.Text;

namespace Jiangyu.Core.Templates.Kdl;

/// <summary>
/// KdlSharp drops slashdash (<c>/-</c>) nodes at parse time per the KDL spec,
/// so a parse and serialise round-trip would erase any <c>/-</c> block in the
/// source. Authors use slashdash to park reference examples next to active
/// templates (e.g. a perk clone or a single overridden set they may want to
/// revive later). This class strips <c>/-</c> blocks from source text before
/// the parse pipeline runs, then reinjects them into the formatted output at
/// the same structural position.
///
/// A captured block records a <see cref="Block.Path"/>: a list of child
/// indices describing how to navigate to the parked block's intended
/// insertion point. <c>[N]</c> means "before the Nth child of the document
/// root" (i.e. before the Nth top-level <c>clone</c> or <c>patch</c>).
/// <c>[N, M]</c> means "descend into the Nth child of the root, then insert
/// before the Mth child of its body". Deeper paths follow the same pattern.
/// Path indices count non-slashdash, non-comment, non-blank lines only.
///
/// Immediately-preceding <c>//</c> comment lines (and blank lines between
/// them and the slashdash) are captured as part of the parked block so the
/// commentary travels with it.
///
/// Brace tracking is string-literal aware: a <c>{</c> or <c>}</c> inside a
/// quoted value (with standard <c>\"</c> escaping) does not shift depth.
/// </summary>
public static class KdlSlashdashPreserver
{
    /// <summary>
    /// A captured parked block. <see cref="Text"/> is the raw source segment
    /// (including any preceding adjacent <c>//</c> comment or blank lines).
    /// <see cref="Path"/> is the child-index path describing where the block
    /// belongs: <c>[N]</c> for "before root's Nth child", <c>[N, M]</c> for
    /// "inside root's Nth child, before its Mth child", and so on.
    /// </summary>
    public readonly record struct Block(string Text, IReadOnlyList<int> Path);

    /// <summary>
    /// Scan <paramref name="text"/> for <c>/-</c>-prefixed lines at any depth,
    /// strip them (plus any immediately-preceding <c>//</c> comment or blank
    /// lines) into <paramref name="stripped"/>, and return the captured blocks
    /// with their position anchors.
    /// </summary>
    public static List<Block> Extract(string text, out string stripped)
    {
        var blocks = new List<Block>();
        var output = new StringBuilder(text.Length);
        // Stack of ancestor child indices: nodePath[d] is which child of its
        // parent the body currently open at depth d+1 came from.
        var nodePath = new List<int>();
        // Per-depth counter of non-/- children encountered so far. Depth d's
        // counter lives at index d; the active depth is nodePath.Count.
        var nextChildIndex = new List<int> { 0 };
        // Position in output marking the start of the buffered run of `//`
        // comment and blank lines. If a /- arrives, the rollback captures
        // from here so the commentary travels with the parked block.
        var pendingStart = 0;
        var i = 0;

        while (i < text.Length)
        {
            var lineStart = i;
            var newlineIdx = text.IndexOf('\n', lineStart);
            var lineExclusiveEnd = newlineIdx < 0 ? text.Length : newlineIdx + 1;
            var contentStart = SkipLineWhitespace(text, lineStart);
            var hasContent = contentStart < text.Length && text[contentStart] != '\n';

            if (hasContent
                && contentStart + 1 < text.Length
                && text[contentStart] == '/' && text[contentStart + 1] == '-')
            {
                var depth = nodePath.Count;
                var path = new int[depth + 1];
                for (var k = 0; k < depth; k++) path[k] = nodePath[k];
                path[depth] = nextChildIndex[depth];

                var raw = output.ToString(pendingStart, output.Length - pendingStart);
                output.Length = pendingStart;

                var blockEnd = FindSlashdashBlockEnd(text, contentStart);
                raw += text[lineStart..blockEnd];
                blocks.Add(new Block(raw, path));

                i = blockEnd;
                if (i < text.Length && text[i] == '\n') i++;
                pendingStart = output.Length;
                continue;
            }

            var isComment = hasContent && contentStart + 1 < text.Length
                && text[contentStart] == '/' && text[contentStart + 1] == '/';
            var isBlank = !hasContent;
            var isClosingBrace = hasContent && text[contentStart] == '}';
            var isNodeLine = hasContent && !isComment && !isClosingBrace;

            output.Append(text, lineStart, lineExclusiveEnd - lineStart);
            i = lineExclusiveEnd;

            if (isClosingBrace && nodePath.Count > 0)
            {
                nodePath.RemoveAt(nodePath.Count - 1);
                nextChildIndex.RemoveAt(nextChildIndex.Count - 1);
            }
            else if (isNodeLine)
            {
                var depth = nodePath.Count;
                var thisIndex = nextChildIndex[depth];
                nextChildIndex[depth] = thisIndex + 1;

                var (open, close) = CountBracesOutsideStrings(text, lineStart, lineExclusiveEnd);
                if (open > close)
                {
                    nodePath.Add(thisIndex);
                    nextChildIndex.Add(0);
                }
            }

            if (!isComment && !isBlank) pendingStart = output.Length;
        }

        stripped = output.ToString();
        return blocks;
    }

    /// <summary>
    /// Inject each captured parked block into <paramref name="formatted"/> at
    /// the line indicated by its <see cref="Block.Path"/>. Blocks whose path
    /// terminates past the last child of their target body are placed before
    /// that body's closing brace; blocks anchored past the last top-level
    /// node append at end-of-file.
    /// </summary>
    public static string Reinject(string formatted, IReadOnlyList<Block> blocks)
    {
        if (blocks.Count == 0) return formatted;

        var lines = formatted.Split('\n');
        var nodePath = new List<int>();
        var nextChildIndex = new List<int> { 0 };
        var remaining = new List<Block>(blocks);
        var insertsByLine = new Dictionary<int, List<string>>();

        for (var lineIdx = 0; lineIdx < lines.Length; lineIdx++)
        {
            var line = lines[lineIdx];
            var contentStart = SkipLineWhitespace(line, 0);
            var hasContent = contentStart < line.Length;
            var isComment = hasContent && contentStart + 1 < line.Length
                && line[contentStart] == '/' && line[contentStart + 1] == '/';
            var isClosingBrace = hasContent && line[contentStart] == '}';
            var isNodeLine = hasContent && !isComment && !isClosingBrace;

            if (isClosingBrace && nodePath.Count > 0)
            {
                TryMatchBlocks(remaining, nodePath, nextChildIndex, lineIdx, insertsByLine);
                nodePath.RemoveAt(nodePath.Count - 1);
                nextChildIndex.RemoveAt(nextChildIndex.Count - 1);
            }
            else if (isNodeLine)
            {
                TryMatchBlocks(remaining, nodePath, nextChildIndex, lineIdx, insertsByLine);

                var depth = nodePath.Count;
                var thisIndex = nextChildIndex[depth];
                nextChildIndex[depth] = thisIndex + 1;

                var (open, close) = CountBracesOutsideStrings(line, 0, line.Length);
                if (open > close)
                {
                    nodePath.Add(thisIndex);
                    nextChildIndex.Add(0);
                }
            }
        }

        foreach (var block in remaining)
        {
            if (!insertsByLine.TryGetValue(lines.Length, out var list))
                insertsByLine[lines.Length] = list = [];
            list.Add(block.Text.TrimEnd());
        }

        var sb = new StringBuilder();
        for (var i = 0; i < lines.Length; i++)
        {
            if (insertsByLine.TryGetValue(i, out var inserts))
            {
                foreach (var block in inserts)
                {
                    sb.Append(block);
                    sb.Append('\n');
                    sb.Append('\n');
                }
            }
            if (i == lines.Length - 1)
                sb.Append(lines[i]);
            else
            {
                sb.Append(lines[i]);
                sb.Append('\n');
            }
        }
        if (insertsByLine.TryGetValue(lines.Length, out var trailing))
        {
            foreach (var block in trailing)
            {
                sb.Append('\n');
                sb.Append(block);
                sb.Append('\n');
            }
        }
        return sb.ToString();
    }

    private static void TryMatchBlocks(
        List<Block> remaining,
        List<int> nodePath,
        List<int> nextChildIndex,
        int lineIdx,
        Dictionary<int, List<string>> insertsByLine)
    {
        var depth = nodePath.Count;
        var currentChild = nextChildIndex[depth];

        for (var b = remaining.Count - 1; b >= 0; b--)
        {
            var block = remaining[b];
            if (block.Path.Count != depth + 1) continue;

            var ancestorMatch = true;
            for (var k = 0; k < depth; k++)
                if (block.Path[k] != nodePath[k]) { ancestorMatch = false; break; }
            if (!ancestorMatch) continue;

            if (block.Path[depth] != currentChild) continue;

            if (!insertsByLine.TryGetValue(lineIdx, out var list))
                insertsByLine[lineIdx] = list = [];
            list.Add(block.Text.TrimEnd());
            remaining.RemoveAt(b);
        }
    }

    private static (int open, int close) CountBracesOutsideStrings(string text, int start, int endExclusive)
    {
        var open = 0;
        var close = 0;
        var inString = false;
        var escape = false;
        for (var i = start; i < endExclusive; i++)
        {
            var c = text[i];
            if (escape) { escape = false; continue; }
            if (inString)
            {
                if (c == '\\') { escape = true; continue; }
                if (c == '"') inString = false;
                continue;
            }
            if (c == '"') { inString = true; continue; }
            if (c == '/' && i + 1 < endExclusive && text[i + 1] == '/') break;
            if (c == '{') open++;
            else if (c == '}') close++;
        }
        return (open, close);
    }

    private static int SkipLineWhitespace(string text, int start)
    {
        var i = start;
        while (i < text.Length && (text[i] == ' ' || text[i] == '\t')) i++;
        return i;
    }

    private static int FindSlashdashBlockEnd(string text, int start)
    {
        var depth = 0;
        var sawOpenBrace = false;
        var inString = false;
        var escape = false;
        for (var i = start; i < text.Length; i++)
        {
            var c = text[i];
            if (escape) { escape = false; continue; }
            if (inString)
            {
                if (c == '\\') { escape = true; continue; }
                if (c == '"') inString = false;
                continue;
            }
            if (c == '"') { inString = true; continue; }
            if (c == '{') { depth++; sawOpenBrace = true; }
            else if (c == '}')
            {
                depth--;
                if (depth == 0 && sawOpenBrace) return i + 1;
            }
            else if (c == '\n' && depth == 0 && !sawOpenBrace)
            {
                return i;
            }
        }
        return text.Length;
    }
}
