using System.IO.Compression;
using Jiangyu.Core.Models;

namespace Jiangyu.Core.Deploy;

/// <summary>
/// Packages an already-compiled mod into a distributable <c>&lt;name&gt;-&lt;version&gt;.zip</c>.
/// Like <see cref="ModDeployer"/>, it works on the existing <c>compiled/</c> output and does
/// not compile, so a stale build is the caller's responsibility (run compile first). The
/// archive holds a single top-level <c>&lt;name&gt;/</c> folder so a player extracts it straight
/// into the game's <c>Mods/</c>.
/// </summary>
public static class ModPackager
{
    public readonly record struct PackResult(string ModName, string Version, string ArchivePath);

    /// <summary>Zip <paramref name="projectDir"/>'s <c>compiled/</c> output into
    /// <paramref name="outputDir"/> (default: the project directory). Throws when there is no
    /// compiled output to package.</summary>
    public static PackResult PackProject(string projectDir, string? outputDir = null)
    {
        var compiledDir = Path.Combine(projectDir, "compiled");
        if (!Directory.Exists(compiledDir))
            throw new InvalidOperationException("compiled/ not found. Run compile first.");

        var manifest = ModManifest.TryLoad(compiledDir)
            ?? throw new InvalidOperationException($"compiled/{ModManifest.FileName} not found. Run compile first.");

        var version = string.IsNullOrWhiteSpace(manifest.Version) ? "0.0.0" : manifest.Version;
        var destDir = outputDir ?? projectDir;
        Directory.CreateDirectory(destDir);

        var archivePath = Path.Combine(destDir, $"{manifest.Name}-{version}.zip");
        Pack(compiledDir, manifest.Name, archivePath);
        return new PackResult(manifest.Name, version, archivePath);
    }

    private static void Pack(string compiledDir, string modName, string archivePath)
    {
        if (File.Exists(archivePath))
            File.Delete(archivePath);

        using var zip = ZipFile.Open(archivePath, ZipArchiveMode.Create);
        var files = Directory.EnumerateFiles(compiledDir, "*", SearchOption.AllDirectories)
            .OrderBy(path => path, StringComparer.Ordinal);
        foreach (var file in files)
        {
            var relative = Path.GetRelativePath(compiledDir, file).Replace('\\', '/');
            zip.CreateEntryFromFile(file, $"{modName}/{relative}");
        }
    }
}
