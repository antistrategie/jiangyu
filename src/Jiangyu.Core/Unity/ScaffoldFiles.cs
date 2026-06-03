using System.Reflection;

namespace Jiangyu.Core.Unity;

/// <summary>
/// Shared file-writing primitives for the project scaffolders. Writes record their
/// outcome on a <see cref="ScaffoldResult"/> (created / overwritten / preserved), skip
/// no-op overwrites so an unchanged file does not read as updated, and load embedded
/// templates by their logical resource path from this assembly.
/// </summary>
internal static class ScaffoldFiles
{
    public static void WriteFile(string destPath, string content, bool overwrite, ScaffoldResult result)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(destPath)!);
        if (File.Exists(destPath))
        {
            // Avoid re-counting a no-op write as "updated"; the modder cares when a
            // template's bytes actually drift, not when sync confirms it is still in place.
            var existing = File.ReadAllText(destPath);
            if (string.Equals(existing, content, StringComparison.Ordinal)) return;
            if (overwrite)
            {
                File.WriteAllText(destPath, content);
                result.OverwrittenFiles.Add(destPath);
            }
            return;
        }
        File.WriteAllText(destPath, content);
        result.CreatedFiles.Add(destPath);
    }

    public static void WriteIfMissing(string destPath, string content, ScaffoldResult result)
    {
        if (File.Exists(destPath))
        {
            result.PreservedFiles.Add(destPath);
            return;
        }
        WriteFile(destPath, content, overwrite: false, result);
    }

    public static string LoadEmbeddedTemplate(string logicalPath)
    {
        var assembly = typeof(ScaffoldFiles).Assembly;
        using var stream = assembly.GetManifestResourceStream(logicalPath)
            ?? throw new InvalidOperationException(
                $"Embedded template not found: {logicalPath}. " +
                $"Available resources: {string.Join(", ", assembly.GetManifestResourceNames())}");
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }
}
