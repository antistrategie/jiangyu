using System.Text.RegularExpressions;
using Jiangyu.Core.Models;

namespace Jiangyu.Core.Compile;

/// <summary>
/// Checks that every Unity GUID reachable from <c>unity/Assets/</c> resolves
/// to either a non-imported asset or an <c>Imported/&lt;name&gt;/</c> rip
/// declared in <c>manifest.importedPrefabs</c>. Catches the silent-pink-
/// material failure where a contributor bakes a humanoid against a vanilla
/// rip but forgets to declare the rip in the manifest.
/// </summary>
internal static partial class ImportedPrefabValidator
{
    [GeneratedRegex(@"guid:\s*([0-9a-fA-F]{32})")]
    private static partial Regex MetaGuidRegex();

    /// <summary>
    /// Returns a modder-facing error message when at least one violation is
    /// found, or null when everything resolves. Scans only <c>.mat</c>,
    /// <c>.prefab</c> and <c>.asset</c> files because those are the file
    /// types Unity uses GUIDs in; binary <c>.asset</c> files won't
    /// regex-match and are silently skipped.
    /// </summary>
    public static string? Validate(string projectDir, ModManifest manifest)
    {
        var assetsRoot = Path.Combine(projectDir, "unity", "Assets");
        var importedRoot = Path.Combine(assetsRoot, "Imported");
        if (!Directory.Exists(assetsRoot) || !Directory.Exists(importedRoot))
            return null;

        var guidToSubdir = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var subdir in Directory.EnumerateDirectories(importedRoot))
        {
            var subdirName = Path.GetFileName(subdir);
            foreach (var meta in Directory.EnumerateFiles(subdir, "*.meta", SearchOption.AllDirectories))
            {
                try
                {
                    var match = MetaGuidRegex().Match(File.ReadAllText(meta));
                    if (match.Success) guidToSubdir.TryAdd(match.Groups[1].Value, subdirName);
                }
                catch (IOException) { }
                catch (UnauthorizedAccessException) { }
            }
        }
        if (guidToSubdir.Count == 0) return null;

        var declared = new HashSet<string>(
            (IEnumerable<string>?)manifest.ImportedPrefabs ?? [],
            StringComparer.OrdinalIgnoreCase);

        var extensions = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { ".mat", ".prefab", ".asset" };
        var importedPrefix = importedRoot + Path.DirectorySeparatorChar;
        var violationsByFile = new SortedDictionary<string, SortedSet<string>>(StringComparer.Ordinal);

        foreach (var file in Directory.EnumerateFiles(assetsRoot, "*", SearchOption.AllDirectories))
        {
            if (file.StartsWith(importedPrefix, StringComparison.OrdinalIgnoreCase)) continue;
            if (!extensions.Contains(Path.GetExtension(file))) continue;

            string content;
            try { content = File.ReadAllText(file); }
            catch (IOException) { continue; }
            catch (UnauthorizedAccessException) { continue; }

            foreach (Match match in MetaGuidRegex().Matches(content))
            {
                var guid = match.Groups[1].Value;
                if (!guidToSubdir.TryGetValue(guid, out var subdir)) continue;
                if (declared.Contains(subdir)) continue;

                var rel = Path.GetRelativePath(projectDir, file);
                if (!violationsByFile.TryGetValue(rel, out var subdirs))
                {
                    subdirs = new SortedSet<string>(StringComparer.Ordinal);
                    violationsByFile[rel] = subdirs;
                }
                subdirs.Add(subdir);
            }
        }

        if (violationsByFile.Count == 0) return null;

        var missingSubdirs = new SortedSet<string>(
            violationsByFile.Values.SelectMany(v => v),
            StringComparer.Ordinal);

        var lines = new List<string>
        {
            "Mod content references host-game rips that are missing from 'importedPrefabs' in jiangyu.json.",
            "Either add the following names to the manifest and re-run compile, or remove the references from the listed files:",
        };
        foreach (var subdir in missingSubdirs)
            lines.Add($"  - {subdir}");
        lines.Add("References found in:");
        foreach (var (file, subdirs) in violationsByFile)
            lines.Add($"  {file} -> Imported/{string.Join(", Imported/", subdirs)}/");

        return string.Join("\n", lines);
    }
}
