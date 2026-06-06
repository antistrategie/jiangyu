using System.Reflection;
using System.Reflection.Emit;
using Jiangyu.Core.Deploy;

namespace Jiangyu.Core.Tests.Deploy;

public class LoaderDeployerTests
{
    [Fact]
    public void Inspect_DiscriminatesDevFromUser()
    {
        // The dev loader is the one carrying the merged Jiangyu.Loader.Diagnostics.DevServices.
        var dev = EmitLoaderFixture(withDevServices: true, version: "1.2.3");
        var user = EmitLoaderFixture(withDevServices: false, version: "1.2.3");
        try
        {
            Assert.Equal("dev", LoaderVariantDetector.Inspect(dev).Variant);
            Assert.Equal("user", LoaderVariantDetector.Inspect(user).Variant);
        }
        finally
        {
            File.Delete(dev);
            File.Delete(user);
        }
    }

    [Fact]
    public void Inspect_ReadsBuildInfoVersion()
    {
        var asm = EmitLoaderFixture(withDevServices: false, version: "0.6.0-18-gabcdef");
        try
        {
            Assert.Equal("0.6.0-18-gabcdef", LoaderVariantDetector.Inspect(asm).Version);
        }
        finally
        {
            File.Delete(asm);
        }
    }

    [Fact]
    public void Inspect_NullVersionWhenNoBuildInfoConstant()
    {
        var asm = EmitLoaderFixture(withDevServices: false, version: null);
        try
        {
            Assert.Null(LoaderVariantDetector.Inspect(asm).Version);
        }
        finally
        {
            File.Delete(asm);
        }
    }

    // Emits a minimal managed assembly shaped like a merged loader: optionally the
    // dev sentinel type, and (optionally) the Jiangyu.Loader.BuildInfo.Version const
    // the detector reads. Reflection.Emit only — no Roslyn / extra package.
    private static string EmitLoaderFixture(bool withDevServices, string? version)
    {
        var ab = new PersistedAssemblyBuilder(new AssemblyName("LoaderFixture"), typeof(object).Assembly);
        var module = ab.DefineDynamicModule("LoaderFixture");

        if (withDevServices)
            module.DefineType("Jiangyu.Loader.Diagnostics.DevServices", TypeAttributes.Public).CreateType();

        if (version is not null)
        {
            var buildInfo = module.DefineType(
                "Jiangyu.Loader.BuildInfo",
                TypeAttributes.Public | TypeAttributes.Abstract | TypeAttributes.Sealed);
            var field = buildInfo.DefineField(
                "Version", typeof(string),
                FieldAttributes.Public | FieldAttributes.Static | FieldAttributes.Literal);
            field.SetConstant(version);
            buildInfo.CreateType();
        }
        else
        {
            // A type in the namespace but no Version const, so the reader returns null.
            module.DefineType("Jiangyu.Loader.BuildInfo", TypeAttributes.Public).CreateType();
        }

        var path = Path.Combine(Path.GetTempPath(), $"loaderfix-{Guid.NewGuid()}.dll");
        ab.Save(path);
        return path;
    }

    [Fact]
    public void ResolveDestination_IsModsLoaderDll()
    {
        var dest = LoaderDeployer.ResolveDestination("/games/Menace");
        Assert.Equal(Path.Combine("/games/Menace", "Mods", "Jiangyu.Loader.dll"), dest);
    }

    [Fact]
    public void Deploy_CopiesIntoModsAndOverwrites()
    {
        var root = NewTempDir();
        try
        {
            var src = Path.Combine(root, "loader", "dev", "Jiangyu.Loader.dll");
            Directory.CreateDirectory(Path.GetDirectoryName(src)!);
            File.WriteAllText(src, "dev-build");

            var gameDir = Path.Combine(root, "game");

            var dest = LoaderDeployer.Deploy(src, gameDir);

            Assert.Equal(Path.Combine(gameDir, "Mods", "Jiangyu.Loader.dll"), dest);
            Assert.Equal("dev-build", File.ReadAllText(dest));

            // A second deploy overwrites in place (the update / switch case).
            File.WriteAllText(src, "user-build");
            LoaderDeployer.Deploy(src, gameDir);
            Assert.Equal("user-build", File.ReadAllText(dest));
        }
        finally { Cleanup(root); }
    }

    [Fact]
    public void Deploy_ThrowsWhenSourceMissing()
    {
        var root = NewTempDir();
        try
        {
            Assert.Throws<FileNotFoundException>(() =>
                LoaderDeployer.Deploy(Path.Combine(root, "nope.dll"), Path.Combine(root, "game")));
        }
        finally { Cleanup(root); }
    }

    [Fact]
    public void Inspect_NullForMissingFile()
    {
        Assert.Null(LoaderVariantDetector.Inspect(Path.Combine(Path.GetTempPath(), $"absent-{Guid.NewGuid()}.dll")).Variant);
    }

    [Fact]
    public void Inspect_NullForNonManagedFile()
    {
        var root = NewTempDir();
        try
        {
            var junk = Path.Combine(root, "Jiangyu.Loader.dll");
            File.WriteAllText(junk, "not a PE file");
            Assert.Null(LoaderVariantDetector.Inspect(junk).Variant);
        }
        finally { Cleanup(root); }
    }

    [Fact]
    public void DetectDeployed_NullWhenNoLoaderDeployed()
    {
        var root = NewTempDir();
        try { Assert.Null(LoaderVariantDetector.DetectDeployed(root)); }
        finally { Cleanup(root); }
    }

    private static string NewTempDir()
    {
        var dir = Path.Combine(Path.GetTempPath(), $"jiangyu-loader-{Guid.NewGuid()}");
        Directory.CreateDirectory(dir);
        return dir;
    }

    private static void Cleanup(string dir)
    {
        if (Directory.Exists(dir))
            Directory.Delete(dir, recursive: true);
    }
}
