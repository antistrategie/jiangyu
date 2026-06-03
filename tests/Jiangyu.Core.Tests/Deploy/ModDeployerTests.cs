using Jiangyu.Core.Deploy;
using Xunit;

namespace Jiangyu.Core.Tests.Deploy;

public sealed class ModDeployerTests : IDisposable
{
    private readonly string _root;
    private readonly string _compiled;
    private readonly string _dest;

    public ModDeployerTests()
    {
        _root = Path.Combine(Path.GetTempPath(), $"jiangyu-deploy-tests-{Guid.NewGuid():N}");
        _compiled = Path.Combine(_root, "compiled");
        _dest = Path.Combine(_root, "Mods", "MyMod");
        Directory.CreateDirectory(Path.Combine(_compiled, "code"));
        File.WriteAllText(Path.Combine(_compiled, "jiangyu.json"), "{}");
        File.WriteAllText(Path.Combine(_compiled, "a.bundle"), "bundle");
        File.WriteAllText(Path.Combine(_compiled, "code", "MyMod.Code.dll"), "dll");
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
            Directory.Delete(_root, recursive: true);
    }

    [Fact]
    public void Deploy_copies_compiled_tree_including_code()
    {
        ModDeployer.Deploy(_compiled, _dest);

        Assert.True(File.Exists(Path.Combine(_dest, "jiangyu.json")));
        Assert.True(File.Exists(Path.Combine(_dest, "a.bundle")));
        Assert.True(File.Exists(Path.Combine(_dest, "code", "MyMod.Code.dll")));
    }

    [Fact]
    public void Deploy_removes_stale_artifacts_in_the_destination()
    {
        // A previous deploy left a renamed code DLL and a dropped bundle behind.
        Directory.CreateDirectory(Path.Combine(_dest, "code"));
        File.WriteAllText(Path.Combine(_dest, "code", "OldName.Code.dll"), "stale");
        File.WriteAllText(Path.Combine(_dest, "removed.bundle"), "stale");
        File.WriteAllText(Path.Combine(_dest, "jiangyu.json.bak"), "stale");

        ModDeployer.Deploy(_compiled, _dest);

        Assert.False(File.Exists(Path.Combine(_dest, "code", "OldName.Code.dll")));
        Assert.False(File.Exists(Path.Combine(_dest, "removed.bundle")));
        Assert.False(File.Exists(Path.Combine(_dest, "jiangyu.json.bak")));
        // The current build's single code DLL is the only one present.
        Assert.Equal(new[] { "MyMod.Code.dll" }, Directory.GetFiles(Path.Combine(_dest, "code")).Select(Path.GetFileName).ToArray());
    }

    [Fact]
    public void Deploy_missing_compiled_dir_throws()
    {
        Assert.Throws<DirectoryNotFoundException>(
            () => ModDeployer.Deploy(Path.Combine(_root, "nope"), _dest));
    }

    [Fact]
    public void ResolveModDestination_builds_the_Mods_path_for_a_plain_name()
    {
        Assert.Equal(Path.Combine(_root, "Mods", "MyMod"), ModDeployer.ResolveModDestination(_root, "MyMod"));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("..")]
    [InlineData("../evil")]
    [InlineData("a/b")]
    [InlineData("/etc/cron.d")]
    public void ResolveModDestination_rejects_names_that_escape_Mods(string badName)
    {
        // A recursive delete on the resolved path makes an escaping name a data-loss bug.
        Assert.Throws<ArgumentException>(() => ModDeployer.ResolveModDestination(_root, badName));
    }
}
