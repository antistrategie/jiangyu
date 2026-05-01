using System.Reflection.PortableExecutable;
using System.Reflection.Metadata;
using Xunit;

namespace Jiangyu.Loader.Tests;

/// <summary>
/// Inspects the merged Release-built Jiangyu.Loader.dll and refuses any
/// referenced assembly that won't be present in the IL2CPP MelonLoader runtime
/// (currently System.IO.Pipelines, but the list is open-ended). Prevents a
/// silent regression where a System.Text.Json bump or new dep transitively
/// pulls a net10-only assembly into the merged loader.
///
/// Marked Skipped (not Passed) when the Release DLL isn't on disk, so local
/// Debug iteration shows yellow rather than green. CI's <c>test-dotnet</c>
/// job builds Release before running tests so the guard fires there too;
/// without that step it would silently skip on every PR.
/// </summary>
public class MergedLoaderDependencyGuardTests
{
    // Assemblies that must not appear as references in the merged loader.
    // System.IO.Pipelines was the original offender (net6 runtime ships none;
    // net8+ System.Text.Json transitively references it). Add others as we
    // discover them.
    private static readonly string[] ForbiddenReferences =
    {
        "System.IO.Pipelines",
    };

    private static string? FindReleaseLoaderDll()
    {
        // tests/Jiangyu.Loader.Tests/bin/<config>/<tfm>/ → repo root is 5 up.
        var here = AppContext.BaseDirectory;
        var repoRoot = Path.GetFullPath(Path.Combine(here, "..", "..", "..", "..", ".."));
        var candidate = Path.Combine(repoRoot, "src", "Jiangyu.Loader", "bin", "Release", "net6.0", "Jiangyu.Loader.dll");
        return File.Exists(candidate) ? candidate : null;
    }

    [SkippableFact]
    public void MergedLoader_DoesNotReferenceForbiddenAssemblies()
    {
        var loaderPath = FindReleaseLoaderDll();
        Skip.If(
            loaderPath is null,
            "Release-built Jiangyu.Loader.dll not on disk; build with `dotnet build -c Release` first. "
            + "CI runs Release before tests, so the guard fires there.");

        // Walk the assembly references via System.Reflection.Metadata so we
        // don't actually load the IL2CPP-tainted assembly (which would fail
        // on a vanilla net10 host because Il2CppInterop.Runtime is absent).
        using var stream = File.OpenRead(loaderPath);
        using var pe = new PEReader(stream);
        var reader = pe.GetMetadataReader();

        var referencedNames = reader.AssemblyReferences
            .Select(handle => reader.GetString(reader.GetAssemblyReference(handle).Name))
            .ToHashSet(StringComparer.Ordinal);

        var found = ForbiddenReferences.Where(referencedNames.Contains).ToList();
        Assert.True(
            found.Count == 0,
            $"Merged Jiangyu.Loader.dll references forbidden assembly(s): {string.Join(", ", found)}. "
            + "These are not present in MelonLoader's net6 IL2CPP runtime and will cause "
            + "missing-dependency warnings + Harmony scan exceptions at game launch. "
            + "Likely cause: a System.Text.Json (or sibling System.* package) bump pulled "
            + "this in transitively. Either pin the package back, or update the ILRepack "
            + "merge / deployment to ship the missing DLL alongside the loader.");
    }
}
