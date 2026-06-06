using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using MelonLoader.Utils;

namespace Jiangyu.Loader.Diagnostics;

/// <summary>
/// Shared infrastructure for the loader's disk-writing diagnostics: the output
/// directory under <c>&lt;UserData&gt;/jiangyu-inspect/</c>, the JSON serialiser
/// settings, and a filename sanitiser. The on-demand inspectors return their dumps
/// over the Studio bridge instead; the init-time injection-gate report writes here.
/// </summary>
internal static class InspectionSink
{
    private const string OutputDirectoryName = "jiangyu-inspect";

    internal static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    internal static string SanitiseForFileName(string name)
    {
        if (string.IsNullOrEmpty(name))
            return "unknown";
        var invalid = Path.GetInvalidFileNameChars();
        var builder = new StringBuilder(name.Length);
        foreach (var c in name)
        {
            builder.Append(Array.IndexOf(invalid, c) >= 0 || char.IsWhiteSpace(c) ? '_' : c);
        }
        return builder.ToString();
    }

    internal static string GetOutputDirectory()
    {
        var outDir = Path.Combine(MelonEnvironment.UserDataDirectory, OutputDirectoryName);
        Directory.CreateDirectory(outDir);
        return outDir;
    }
}
