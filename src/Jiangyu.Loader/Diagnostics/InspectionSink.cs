using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using MelonLoader;
using MelonLoader.Utils;

namespace Jiangyu.Loader.Diagnostics;

/// <summary>
/// Shared infrastructure for the loader's diagnostic inspectors. Owns the
/// opt-in flag-file resolution, the output directory, per-kind retention
/// sweep, and the JSON serialiser settings. Both
/// <see cref="SceneIdentityInspector"/> and <see cref="TemplateStateInspector"/>
/// route through here so the gating semantics stay identical.
///
/// Enabled by a flag file in <c>&lt;UserData&gt;</c>:
/// <list type="bullet">
///   <item><c>jiangyu-inspect.flag</c> — unlimited retention (dumps accumulate forever).</item>
///   <item><c>jiangyu-inspect.&lt;N&gt;.flag</c> — rolling retention, keep at most N files
///     per kind in <c>&lt;UserData&gt;/jiangyu-inspect/</c>; oldest are deleted after each write.</item>
/// </list>
/// File contents are ignored. Remove the flag to disable. Numbered flag wins if both exist.
/// </summary>
internal static class InspectionSink
{
    private const string PlainFlagFileName = "jiangyu-inspect.flag";
    private const string NumberedFlagPattern = "jiangyu-inspect.*.flag";
    private const string OutputDirectoryName = "jiangyu-inspect";

    internal static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    // The flag is set before launch and rarely toggles during a session, so we
    // cache its resolution and only re-check on scene loads. OnUpdate would
    // otherwise call Directory.GetFiles every frame.
    private static FlagState? _cachedFlag;

    public static bool IsEnabled() => GetCachedFlag().Enabled;

    public static void RefreshFlagCache() => _cachedFlag = ResolveFlag();

    internal static FlagState GetCachedFlag() => _cachedFlag ??= ResolveFlag();

    // Numbered flag wins when both are present. A non-positive or unparseable
    // N is silently ignored so a stray `jiangyu-inspect.ignored.flag` does not
    // disable the plain flag.
    private static FlagState ResolveFlag()
    {
        try
        {
            var userData = MelonEnvironment.UserDataDirectory;
            var numbered = Directory.GetFiles(userData, NumberedFlagPattern);
            foreach (var path in numbered)
            {
                var name = Path.GetFileName(path);
                if (string.Equals(name, PlainFlagFileName, StringComparison.Ordinal))
                    continue;
                if (TryParseRetention(name, out var cap))
                    return new FlagState(true, cap);
            }

            if (File.Exists(Path.Combine(userData, PlainFlagFileName)))
                return new FlagState(true, null);

            return new FlagState(false, null);
        }
        catch
        {
            return new FlagState(false, null);
        }
    }

    private static bool TryParseRetention(string fileName, out int cap)
    {
        cap = 0;
        const string prefix = "jiangyu-inspect.";
        const string suffix = ".flag";
        if (!fileName.StartsWith(prefix, StringComparison.Ordinal)
            || !fileName.EndsWith(suffix, StringComparison.Ordinal))
            return false;
        var middle = fileName.Substring(prefix.Length, fileName.Length - prefix.Length - suffix.Length);
        return int.TryParse(middle, out cap) && cap > 0;
    }

    internal readonly struct FlagState
    {
        public FlagState(bool enabled, int? retentionCap)
        {
            Enabled = enabled;
            RetentionCap = retentionCap;
        }

        public bool Enabled { get; }
        public int? RetentionCap { get; }
    }

    // Per-kind LRU-by-name sweep. Files are bucketed by filename suffix so a
    // frequent kind (the periodic runtime sweep fires every 300 frames after
    // t=600) cannot evict a rare kind (templates dump, once per scene load).
    // Filenames are UTC timestamp-prefixed, so alphanumeric ordering within a
    // bucket equals chronological ordering. Called once per write, after the
    // new file lands, so cap=N means "at most N files per kind survive".
    internal static void EnforceRetention(int? cap, MelonLogger.Instance log)
    {
        if (!cap.HasValue)
            return;

        try
        {
            var outDir = GetOutputDirectory();
            var files = new DirectoryInfo(outDir).GetFiles();
            if (files.Length == 0)
                return;

            var buckets = new Dictionary<string, List<FileInfo>>(StringComparer.Ordinal);
            foreach (var file in files)
            {
                var kind = ClassifyDumpKind(file.Name);
                if (!buckets.TryGetValue(kind, out var bucket))
                {
                    bucket = new List<FileInfo>();
                    buckets[kind] = bucket;
                }
                bucket.Add(file);
            }

            foreach (var bucket in buckets.Values)
            {
                if (bucket.Count <= cap.Value)
                    continue;

                bucket.Sort((a, b) => string.CompareOrdinal(a.Name, b.Name));
                var toDelete = bucket.Count - cap.Value;
                for (var i = 0; i < toDelete; i++)
                {
                    try { bucket[i].Delete(); }
                    catch (Exception ex)
                    {
                        log.Warning($"[inspect] rotation: could not delete {bucket[i].Name}: {ex.Message}");
                    }
                }
            }
        }
        catch (Exception ex)
        {
            log.Warning($"[inspect] rotation sweep failed: {ex.Message}");
        }
    }

    private static string ClassifyDumpKind(string fileName)
    {
        if (fileName.Contains("-templates-", StringComparison.Ordinal))
            return "templates";
        return "runtime";
    }

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
