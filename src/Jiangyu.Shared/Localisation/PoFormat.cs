using System.Text;

namespace Jiangyu.Shared.Localisation;

/// <summary>One gettext PO message: a <c>msgctxt</c>/<c>msgid</c> key, its translation, the
/// fuzzy flag, and the extracted (<c>#.</c>) and reference (<c>#:</c>) comments.</summary>
public sealed class PoEntry
{
    public string? Context { get; set; }
    public string Id { get; set; } = string.Empty;
    public string Str { get; set; } = string.Empty;
    public bool Fuzzy { get; set; }
    public List<string> ExtractedComments { get; } = [];
    public List<string> References { get; } = [];

    /// <summary>A translation is usable when it is non-empty and not flagged fuzzy.</summary>
    public bool HasUsableTranslation => !Fuzzy && !string.IsNullOrEmpty(Str);
}

/// <summary>A parsed PO/POT file: its translatable entries, header excluded.</summary>
public sealed class PoFile
{
    public List<PoEntry> Entries { get; } = [];
}

/// <summary>
/// A minimal, dependency-free gettext PO reader and writer covering the subset the modkit uses:
/// <c>msgctxt</c>, <c>msgid</c>, <c>msgstr</c>, multi-line string continuations, the <c>fuzzy</c>
/// flag, and <c>#.</c> / <c>#:</c> comments. The reader concatenates continuation lines, so it
/// accepts both the single-line strings this writer emits and the multi-line form translation tools
/// produce. Lives in Jiangyu.Shared so both the compiler and the loader can read a mod's PO files.
/// </summary>
public static class PoFormat
{
    public static PoFile Parse(string text)
    {
        var file = new PoFile();
        PoEntry? current = null;
        var field = Field.None;

        void Flush()
        {
            // The header (empty msgid, no msgctxt) carries metadata, not a translatable string.
            if (current != null && !(current.Id.Length == 0 && current.Context == null))
                file.Entries.Add(current);
            current = null;
            field = Field.None;
        }

        foreach (var raw in SplitLines(text))
        {
            var line = raw.Trim();
            if (line.Length == 0)
            {
                Flush();
                continue;
            }

            if (line[0] == '#')
            {
                current ??= new PoEntry();
                if (line.StartsWith("#.", StringComparison.Ordinal))
                    current.ExtractedComments.Add(line[2..].Trim());
                else if (line.StartsWith("#:", StringComparison.Ordinal))
                    current.References.Add(line[2..].Trim());
                else if (line.StartsWith("#,", StringComparison.Ordinal) && line.Contains("fuzzy"))
                    current.Fuzzy = true;
                continue;
            }

            if (TryKeyword(line, "msgctxt", out var ctxt))
            {
                current ??= new PoEntry();
                current.Context = ctxt;
                field = Field.Context;
            }
            else if (TryKeyword(line, "msgid", out var id))
            {
                current ??= new PoEntry();
                current.Id = id;
                field = Field.Id;
            }
            else if (TryKeyword(line, "msgstr", out var str))
            {
                current ??= new PoEntry();
                current.Str = str;
                field = Field.Str;
            }
            else if (line[0] == '"' && current != null)
            {
                var more = Unquote(line);
                switch (field)
                {
                    case Field.Context: current.Context += more; break;
                    case Field.Id: current.Id += more; break;
                    case Field.Str: current.Str += more; break;
                }
            }
        }

        Flush();
        return file;
    }

    public static string Write(PoFile file)
    {
        var sb = new StringBuilder();
        sb.Append("msgid \"\"\n");
        sb.Append("msgstr \"\"\n");
        sb.Append("\"Content-Type: text/plain; charset=UTF-8\\n\"\n");
        sb.Append("\"Content-Transfer-Encoding: 8bit\\n\"\n\n");

        foreach (var entry in file.Entries)
        {
            foreach (var comment in entry.ExtractedComments)
                sb.Append("#. ").Append(comment).Append('\n');
            foreach (var reference in entry.References)
                sb.Append("#: ").Append(reference).Append('\n');
            if (entry.Fuzzy)
                sb.Append("#, fuzzy\n");
            if (entry.Context != null)
                WriteField(sb, "msgctxt", entry.Context);
            WriteField(sb, "msgid", entry.Id);
            WriteField(sb, "msgstr", entry.Str);
            sb.Append('\n');
        }

        return sb.ToString();
    }

    /// <summary>Decode a PO string literal's escapes (<c>\n</c>, <c>\t</c>, <c>\"</c>, ...).</summary>
    public static string Unescape(string s)
    {
        if (s.IndexOf('\\') < 0)
            return s;
        var sb = new StringBuilder(s.Length);
        for (var i = 0; i < s.Length; i++)
        {
            if (s[i] == '\\' && i + 1 < s.Length)
            {
                i++;
                sb.Append(s[i] switch { 'n' => '\n', 't' => '\t', 'r' => '\r', '"' => '"', '\\' => '\\', var c => c });
            }
            else
            {
                sb.Append(s[i]);
            }
        }
        return sb.ToString();
    }

    private enum Field { None, Context, Id, Str }

    private static bool TryKeyword(string line, string keyword, out string value)
    {
        value = string.Empty;
        if (!line.StartsWith(keyword, StringComparison.Ordinal))
            return false;
        var rest = line[keyword.Length..].TrimStart();
        if (rest.Length == 0 || rest[0] != '"')
            return false;
        value = Unquote(rest);
        return true;
    }

    // The text between the first and last double-quote on the line, unescaped.
    private static string Unquote(string line)
    {
        var first = line.IndexOf('"');
        var last = line.LastIndexOf('"');
        if (first < 0 || last <= first)
            return string.Empty;
        return Unescape(line.Substring(first + 1, last - first - 1));
    }

    private static string Escape(string s)
    {
        var sb = new StringBuilder(s.Length + 8);
        foreach (var c in s)
            sb.Append(c switch
            {
                '\\' => "\\\\",
                '"' => "\\\"",
                '\n' => "\\n",
                '\t' => "\\t",
                '\r' => "\\r",
                _ => c.ToString(),
            });
        return sb.ToString();
    }

    // Single line when the value has no embedded newline, otherwise the gettext multi-line form
    // with an empty lead line and one continuation per source line.
    private static void WriteField(StringBuilder sb, string keyword, string value)
    {
        if (!value.Contains('\n'))
        {
            sb.Append(keyword).Append(" \"").Append(Escape(value)).Append("\"\n");
            return;
        }

        sb.Append(keyword).Append(" \"\"\n");
        var parts = value.Split('\n');
        for (var i = 0; i < parts.Length; i++)
        {
            var last = i == parts.Length - 1;
            if (last && parts[i].Length == 0)
                break;
            var segment = last ? parts[i] : parts[i] + "\n";
            sb.Append('"').Append(Escape(segment)).Append("\"\n");
        }
    }

    private static IEnumerable<string> SplitLines(string text)
        => text.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n');
}
