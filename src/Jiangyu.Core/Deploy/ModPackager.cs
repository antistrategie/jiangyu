using System.IO.Compression;
using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;
using Jiangyu.Core.Models;
using Jiangyu.Shared.Bundles;

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

        // Dev verbs must never ship: refuse to package a build whose code DLL still carries a
        // [DevVerb]. A release build excludes the *.Dev.cs that defines them, so this only trips when
        // a dev build was packaged by mistake.
        GuardNoDevVerbs(compiledDir);

        var version = string.IsNullOrWhiteSpace(manifest.Version) ? "0.0.0" : manifest.Version;
        var destDir = outputDir ?? projectDir;
        Directory.CreateDirectory(destDir);

        var archivePath = Path.Combine(destDir, $"{manifest.Name}-{version}.zip");
        Pack(compiledDir, manifest.Name, archivePath);
        return new PackResult(manifest.Name, version, archivePath);
    }

    // Throw if any compiled code DLL references the [DevVerb] attribute (i.e. applies it to a class),
    // which means a dev verb would ship. Scans the metadata's type references, so it needs no game or
    // SDK assemblies on a resolver path.
    private static void GuardNoDevVerbs(string compiledDir)
    {
        var codeDir = Path.Combine(compiledDir, CompiledLayout.CodeDirName);
        if (!Directory.Exists(codeDir))
            return;
        foreach (var dll in Directory.EnumerateFiles(codeDir, "*.dll", SearchOption.AllDirectories))
        {
            if (!ReferencesDevVerb(dll))
                continue;
            throw new InvalidOperationException(
                $"'{Path.GetFileName(dll)}' carries a [DevVerb] dev verb, which must not ship in a release. "
                + "Put dev verbs in a *.Dev.cs file, then package a release build: "
                + "'jiangyu compile --release' followed by 'jiangyu package'.");
        }
    }

    private static bool ReferencesDevVerb(string dllPath)
    {
        try
        {
            using var stream = File.OpenRead(dllPath);
            using var pe = new PEReader(stream);
            if (!pe.HasMetadata)
                return false;
            var reader = pe.GetMetadataReader();
            foreach (var handle in reader.TypeReferences)
            {
                var typeRef = reader.GetTypeReference(handle);
                // Match the SDK's attribute specifically, not any same-named type a mod might have.
                if (reader.GetString(typeRef.Name) == "DevVerbAttribute"
                    && reader.GetString(typeRef.Namespace) == "Jiangyu.Sdk")
                    return true;
            }
            return false;
        }
        catch (Exception ex)
        {
            // Fail closed: a guard that cannot read the DLL it is about to ship must not wave it
            // through, or a dev build could slip past on a transient read error.
            throw new InvalidOperationException(
                $"could not scan '{Path.GetFileName(dllPath)}' for dev verbs before packaging: {ex.Message}");
        }
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
